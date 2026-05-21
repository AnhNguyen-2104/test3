using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — fields, constructor, WebView2 initialization, and lifecycle.
    /// Chức năng chi tiết được tách sang các file partial riêng biệt:
    ///   Form1.MessageHandler.cs  — xử lý tin nhắn từ UI
    ///   Form1.PlcControl.cs     — điều khiển PLC (Jog, GoHome, Poll, ...)
    ///   Form1.DxfHandler.cs     — mở DXF, tính đường đi, import
    ///   Form1.StatePublisher.cs — đẩy trạng thái lên UI
    ///   Form1.Models.cs         — các model nội bộ
    /// </summary>
    public partial class Form1 : Form
    {
        // QD75 constants — xem định nghĩa tại QD75BufferWriter.cs
        private static int[] MonitorBaseG => QD75BufferWriter.MonitorBaseG;
        private static int[] ControlBaseG => QD75BufferWriter.ControlBaseG;
        private static int[] ProgramBaseG => QD75BufferWriter.ProgramBaseG;
        private const int OffCurrentPos   = QD75BufferWriter.OffCurrentPos;
        private const int OffCurrentSpeed = QD75BufferWriter.OffCurrentSpeed;
        private const int OffErrorCode    = QD75BufferWriter.OffErrorCode;
        private const int OffWarningCode  = QD75BufferWriter.OffWarningCode;
        private const int OffAxisStatus   = QD75BufferWriter.OffAxisStatus;
        private const int OffStartNo      = QD75BufferWriter.OffStartNo;
        private const int OffErrorReset   = QD75BufferWriter.OffErrorReset;
        private const int OffJogSpeed     = QD75BufferWriter.OffJogSpeed;
        private const int OffNewSpeed     = QD75BufferWriter.OffNewSpeed;

        private const string JogBaseRegister      = "M3000";
        private const string EmergencyStopRegister = "M3100";

        // WebView2 + timer
        private readonly WebView2 webView;
        private readonly System.Windows.Forms.Timer plcPollTimer = new System.Windows.Forms.Timer();

        // Serializer
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 256
        };

        // Services
        private readonly CadDocumentService       cadService             = new CadDocumentService();
        private readonly GcodeCoordinateService   gcodeCoordinateService = new GcodeCoordinateService();

        // Data lists
        private readonly List<MonitorRow>  monitorRows  = new List<MonitorRow>();
        private readonly List<ProcessRow>  processRows  = new List<ProcessRow>();
        private readonly Dictionary<string, string> assignedPointKeys
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Telemetry configuration
        private readonly List<string>          telemetryRegisters = new List<string> { "U0\\G800", "U0\\G900", "U0\\G1000", "U0\\G1100" };
        private readonly List<TelemetryBuffer> telemetryBuffers   = new List<TelemetryBuffer> { new TelemetryBuffer { Path = "U0\\G2006", Length = 2 } };

        // Axis data (4 axes)
        private readonly int[] axCurrentPos   = new int[4];
        private readonly int[] axCurrentSpeed = new int[4];
        private readonly int[] axMCode        = new int[4];
        private readonly int[] axErrorCode    = new int[4];
        private readonly int[] axWarningCode  = new int[4];
        private readonly int[] axAxisStatus   = new int[4];
        private readonly int[] axSignals      = new int[4]; // Md.30: b0=Limit-, b1=Limit+, b6=Home(DOG)
        private readonly int[] axCurrentDataNo = new int[4];
        private readonly int[] axLastDataNo    = new int[4];
        private readonly int[] axErrorReset   = new int[4];
        private readonly int[] axJogSpeed     = new int[4];
        private readonly int[] axNewSpeed     = new int[4];
        private int logicalStation = 0;

        // Log store
        private readonly List<LogEntry> logs = new List<LogEntry>();

        // PLC connection
        private PLCCommunication plcComm;

        // CAD / DXF
        private CadDocumentService.CadLoadResult activeCadDocument;
        private string selectedCadPointKey;
        private string activeDocumentKind = "DXF";
        private string globalZDown  = "";
        private string globalZSafe  = "";
        private string globalZStart = "";
        private string globalSpeed = "1000";
        private string rapidSpeed  = "10000"; // Tốc độ G00 — cài đặt từ Settings tab
        private double offsetX = 0.0;
        private double offsetY = 0.0;
        private double workspaceWidth  = 170.0; // Kích thước không gian làm việc X (mm)
        private double workspaceHeight = 170.0; // Kích thước không gian làm việc Y (mm)
        private string globalDwellM3 = "100"; // Dwell time (ms) cho M03 (bắt đầu dispensing)
        private string globalDwellM4 = "100"; // Dwell time (ms) cho M04 (kết thúc dispensing)
        private string memberPassword = ""; // Mật khẩu thành viên (admin tạo)
        // G54-G59 Work Coordinate System offsets (chỉ áp dụng cho G-code)
        private string activeWcs = "G54";
        private readonly double[] wcsOffsetX = new double[6]; // G54=0, G55=1, ..., G59=5
        private readonly double[] wcsOffsetY = new double[6];
        private string rawGcodeText = string.Empty;
        private QD75RingBufferRunner activeRingRunner; // Ring buffer runner cho >600 điểm

        // UI state
        private volatile bool webReady;
        private volatile bool isClosing;
        private volatile bool isPolling;
        private string currentView    = "control";
        private string currentTheme   = "dark";
        private string plcIpAddress   = "192.168.3.39";
        private int    plcPort        = 3000;
        private string connectionBanner = "PLC disconnected";
        private string integrityState = "IDLE";
        private string integrityDetail = "STOP";
        private string integrityTone  = "idle";
        private float  currentJogSpeedD406 = 1000f;

        // ── Constructor ──────────────────────────────────────────────────────────
        public Form1()
        {
            InitializeComponent();

            Text          = "DACDT 2026";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize   = new Size(1440, 860);
            WindowState   = FormWindowState.Maximized;
            FormBorderStyle = FormBorderStyle.None;
            BackColor     = Color.FromArgb(10, 15, 30);

            webView = new WebView2
            {
                Dock                  = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(10, 15, 30)
            };
            Controls.Add(webView);
            Controls.SetChildIndex(webView, 0);

            InitializeProcessRows();
            UpdateConnectionState(false, "PLC disconnected");
            UpdateIntegrityState(false);

            plcPollTimer.Interval = 50; // 50 ms real-time polling
            plcPollTimer.Tick    += PlcPollTimer_Tick;

            Shown += async (sender, e) => await InitializeWebViewAsync();

            // Load settings từ file text
            LoadSettingsFromFile();
        }

        // ── Settings persistence ─────────────────────────────────────────────────
        private static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_settings.txt");

        private void LoadSettingsFromFile()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "rapidSpeed": rapidSpeed = val; break;
                        case "globalSpeed": globalSpeed = val; break;
                        case "workspaceWidth": double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out workspaceWidth); break;
                        case "workspaceHeight": double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out workspaceHeight); break;
                        case "offsetX": double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out offsetX); break;
                        case "offsetY": double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out offsetY); break;
                        case "plcIpAddress": plcIpAddress = val; break;
                        case "plcPort": int.TryParse(val, out plcPort); break;
                        case "logicalStation": int.TryParse(val, out logicalStation); break;
                        case "globalZStart": globalZStart = val; break;
                        case "globalZDown": globalZDown = val; break;
                        case "globalZSafe": globalZSafe = val; break;
                        case "globalDwellM3": globalDwellM3 = val; break;
                        case "globalDwellM4": globalDwellM4 = val; break;
                        case "memberPassword": memberPassword = val; break;
                        case "activeWcs": activeWcs = val; break;
                        default:
                            // WCS offsets: wcsG54X, wcsG54Y, wcsG55X, ...
                            for (int i = 0; i < 6; i++)
                            {
                                string gName = "G5" + (4 + i);
                                if (key == "wcs" + gName + "X") { double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out wcsOffsetX[i]); break; }
                                if (key == "wcs" + gName + "Y") { double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out wcsOffsetY[i]); break; }
                            }
                            break;
                    }
                }
            }
            catch { /* ignore load errors */ }
        }

        private void SaveSettingsToFile()
        {
            try
            {
                var lines = new List<string>
                {
                    "# DACDT_2026 Settings",
                    $"rapidSpeed={rapidSpeed}",
                    $"globalSpeed={globalSpeed}",
                    $"workspaceWidth={workspaceWidth.ToString("0.###", CultureInfo.InvariantCulture)}",
                    $"workspaceHeight={workspaceHeight.ToString("0.###", CultureInfo.InvariantCulture)}",
                    $"offsetX={offsetX.ToString("0.###", CultureInfo.InvariantCulture)}",
                    $"offsetY={offsetY.ToString("0.###", CultureInfo.InvariantCulture)}",
                    $"plcIpAddress={plcIpAddress}",
                    $"plcPort={plcPort}",
                    $"logicalStation={logicalStation}",
                    $"globalZStart={globalZStart}",
                    $"globalZDown={globalZDown}",
                    $"globalZSafe={globalZSafe}",
                    $"globalDwellM3={globalDwellM3}",
                    $"globalDwellM4={globalDwellM4}",
                    $"memberPassword={memberPassword}",
                    $"activeWcs={activeWcs}",
                };
                for (int i = 0; i < 6; i++)
                {
                    string gName = "G5" + (4 + i);
                    lines.Add($"wcs{gName}X={wcsOffsetX[i].ToString("0.###", CultureInfo.InvariantCulture)}");
                    lines.Add($"wcs{gName}Y={wcsOffsetY[i].ToString("0.###", CultureInfo.InvariantCulture)}");
                }
                File.WriteAllLines(SettingsFilePath, lines);
            }
            catch { /* ignore save errors */ }
        }

        // ── WebView2 initialization ───────────────────────────────────────────────
        private async Task InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DACDT_2026", "WebView2");
                System.IO.Directory.CreateDirectory(userDataFolder);

                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
                {
                    // Enable WebGL and hardware acceleration for Three.js 3D view
                    AdditionalBrowserArguments = "--enable-webgl --enable-gpu --ignore-gpu-blocklist --enable-unsafe-webgpu"
                };
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                await webView.EnsureCoreWebView2Async(environment);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled  = false;
                webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled             = true;
                webView.CoreWebView2.Settings.IsStatusBarEnabled             = false;
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                string uiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html");
                webView.Source = new Uri(uiPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Failed to initialize HTML dashboard. Please check Microsoft Edge WebView2 Runtime."
                        + Environment.NewLine + Environment.NewLine + ex.Message,
                    "WebView2",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        private bool allowClose = false; // Chỉ cho phép đóng khi nhấn EXIT APP

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; // Chặn Alt+F4 và X button
                return;
            }

            isClosing = true;
            webReady  = false;

            plcPollTimer.Stop();
            plcPollTimer.Tick -= PlcPollTimer_Tick;

            if (plcComm != null)
            {
                try { plcComm.Dispose(); } catch { }
                plcComm = null;
            }

            base.OnFormClosing(e);
        }
    }
}
