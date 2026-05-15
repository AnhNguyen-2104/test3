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
        private readonly CadDocumentService cadService = new CadDocumentService();

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
        private readonly int[] axErrorCode    = new int[4];
        private readonly int[] axWarningCode  = new int[4];
        private readonly int[] axAxisStatus   = new int[4];
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
        private string globalZDown = "";
        private string globalZSafe = "";
        private string globalSpeed = "1000";

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

            Text          = "Gantry SCADA Robot Control";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize   = new Size(1440, 860);
            WindowState   = FormWindowState.Maximized;
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

                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
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
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
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
