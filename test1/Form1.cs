using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Text;

namespace test1
{
    public partial class Form1 : Form
    {
        // QD75 Positioning Module Buffer Memory Addresses
        // Monitor base: Axis1=G800, Axis2=G900, Axis3=G1000, Axis4=G1100
        private static readonly int[] MonitorBaseG = { 800, 900, 1000, 1100 };
        // Control base: Axis1=G1500, Axis2=G1600, Axis3=G1700, Axis4=G1800
        private static readonly int[] ControlBaseG = { 1500, 1600, 1700, 1800 };
        // Program data base (Move/Mcode/Dwell/Speed/Position/Center): Axis1=G2000...
        private static readonly int[] ProgramBaseG = { 2000, 2100, 2200, 2300 };

        // Monitor offsets (from MonitorBaseG)
        private const int OffCurrentPos = 0;      // Current Feed Value (32-bit)
        private const int OffCurrentSpeed = 4;     // Current Speed (32-bit)
        private const int OffErrorCode = 6;        // Error Code (16-bit)
        private const int OffWarningCode = 7;      // Warning Code (16-bit)
        private const int OffAxisStatus = 9;       // Axis Status Md.26 (16-bit)

        // Control offsets (from ControlBaseG)
        private const int OffStartNo = 0;          // Start No. (16-bit)
        private const int OffErrorReset = 2;       // Error Reset (16-bit)
        private const int OffJogSpeed = 4;         // JOG Speed (32-bit)
        private const int OffNewSpeed = 18;        // New Speed Value (32-bit)

        private const string JogBaseRegister = "M3000";
        private const string EmergencyStopRegister = "M3100";

        private readonly WebView2 webView;
        private readonly Timer plcPollTimer = new Timer();

        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 256
        };
        private readonly CadDocumentService cadService = new CadDocumentService();
        private readonly List<MonitorRow> monitorRows = new List<MonitorRow>();
        private readonly List<ProcessRow> processRows = new List<ProcessRow>();
        private readonly Dictionary<string, string> assignedPointKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // telemetry configuration
        private readonly List<string> telemetryRegisters = new List<string> { "U0\\G800", "U0\\G900", "U0\\G1000", "U0\\G1100" };
        private readonly List<TelemetryBuffer> telemetryBuffers = new List<TelemetryBuffer> { new TelemetryBuffer { Path = "U0\\G2006", Length = 2 } };

        // Axis data (4 axes)
        private readonly int[] axCurrentPos = new int[4];
        private readonly int[] axCurrentSpeed = new int[4];
        private readonly int[] axErrorCode = new int[4];
        private readonly int[] axWarningCode = new int[4];
        private readonly int[] axAxisStatus = new int[4];
        private readonly int[] axStartNo = new int[4];
        private readonly int[] axErrorReset = new int[4];
        private readonly int[] axJogSpeed = new int[4];
        private readonly int[] axNewSpeed = new int[4];
        private int logicalStation = 0;

        // simple in-memory log store for PLC I/O operations
        private readonly List<LogEntry> logs = new List<LogEntry>();

        private PLCCommunication plcComm;
        private CadDocumentService.CadLoadResult activeCadDocument;
        private volatile bool webReady;
        private volatile bool isClosing;
        private volatile bool isPolling;
        private string currentView = "control";
        private string currentTheme = "dark";
        private string plcIpAddress = "192.168.3.39";
        private int plcPort = 3000;
        private string connectionBanner = "PLC disconnected";
        private string integrityState = "IDLE";
        private string integrityDetail = "STOP";
        private string integrityTone = "idle";
        private string selectedCadPointKey;
        private float currentJogSpeedD406 = 1000f;

        public Form1()
        {
            InitializeComponent();

            Text = "Gantry SCADA Robot Control";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1440, 860);
            WindowState = FormWindowState.Maximized;
            BackColor = Color.FromArgb(10, 15, 30);

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(10, 15, 30)
            };
            Controls.Add(webView);
            Controls.SetChildIndex(webView, 0);

            InitializeProcessRows();
            UpdateConnectionState(false, "PLC disconnected");
            UpdateIntegrityState(false);

            plcPollTimer.Interval = 50; // Real-time 50ms polling
            plcPollTimer.Tick += PlcPollTimer_Tick;

            Shown += async (sender, e) => await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "test1",
                    "WebView2");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                string uiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html");
                webView.Source = new Uri(uiPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Failed to initialize HTML dashboard. Please check Microsoft Edge WebView2 Runtime." + Environment.NewLine + Environment.NewLine + ex.Message,
                    "WebView2",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                Dictionary<string, object> message = serializer.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                if (message == null)
                {
                    return;
                }

                string action = GetString(message, "action");
                Dictionary<string, object> payload = GetMap(message, "payload");

                switch (action)
                {
                    case "uiReady":
                        webReady = true;
                        await PushAllStateAsync();
                        break;

                    case "switchView":
                        currentView = GetString(payload, "view", currentView);
                        await PushAllStateAsync();
                        break;

                    case "setTheme":
                        currentTheme = GetString(payload, "theme", currentTheme);
                        await PushAllStateAsync();
                        break;

                    case "connectToggle":
                        await HandleConnectToggleAsync(payload);
                        break;

                    case "setVelocity":
                        await HandleSetVelocityAsync(GetInt(payload, "value", 0));
                        break;

                    case "jogStart":
                        await HandleJogWriteAsync(GetInt(payload, "offset", -1), true);
                        break;

                    case "jogStop":
                        await HandleJogWriteAsync(GetInt(payload, "offset", -1), false);
                        break;

                    case "goHomeStart":
                        await HandleGoHomeWriteAsync(true);
                        break;

                    case "goHomeStop":
                        await HandleGoHomeWriteAsync(false);
                        break;

                    case "resetErrorStart":
                        await HandleResetErrorWriteAsync(true);
                        break;

                    case "resetErrorStop":
                        await HandleResetErrorWriteAsync(false);
                        break;

                    case "startActionStart":
                        await HandleStartWriteAsync(true);
                        break;

                    case "startActionStop":
                        await HandleStartWriteAsync(false);
                        break;

                    case "setJogSpeed":
                        await HandleSetJogSpeedAsync(GetDouble(payload, "value", 0));
                        break;

                    case "emergencyStop":
                        await HandleEmergencyStopAsync();
                        break;

                    case "openDxf":
                        await HandleOpenDxfAsync();
                        break;

                    case "selectCadPoint":
                        selectedCadPointKey = GetString(payload, "key");
                        await PushDxfStateAsync();
                        break;

                    case "assignPoint":
                        await HandleAssignPointAsync(GetString(payload, "slot"), GetString(payload, "key", selectedCadPointKey));
                        break;

                    case "setProcessValue":
                        await HandleProcessValueAsync(GetString(payload, "key"), GetString(payload, "value"));
                        break;

                    case "setProcessRowValue":
                        await HandleProcessRowValueAsync(GetInt(payload, "index", -1), GetString(payload, "field"), GetString(payload, "value"));
                        break;

                    case "runAction":
                        await NotifyAsync("info", "DXF RUN", "Các nút Resume, Pause, Start đã có UI HTML. Phần map biến PLC sẽ nối tiếp ở bước sau.");
                        break;

                    // telemetry control from UI
                    case "addTelemetryRegister":
                        await HandleAddTelemetryRegisterAsync(GetString(payload, "register"));
                        break;

                    case "removeTelemetryRegister":
                        await HandleRemoveTelemetryRegisterAsync(GetString(payload, "register"));
                        break;

                    case "addTelemetryBuffer":
                        await HandleAddTelemetryBufferAsync(GetString(payload, "path"), GetInt(payload, "length", 1));
                        break;

                    case "removeTelemetryBuffer":
                        await HandleRemoveTelemetryBufferAsync(GetString(payload, "path"));
                        break;

                    case "writeBufferRequest":
                        await HandleWriteBufferRequestAsync(GetString(payload, "path"), GetInt(payload, "value", 0));
                        break;

                    case "sendCadX":
                        await HandleSendCadXAsync();
                        break;

                    case "importCadToProcess":
                        await HandleImportCadToProcessAsync();
                        break;

                    case "clearLogs":
                        await HandleClearLogsAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "UI bridge", ex.Message);
            }
        }

        private async Task HandleConnectToggleAsync(Dictionary<string, object> payload)
        {
            logicalStation = GetInt(payload, "station", logicalStation);

            if (plcComm != null && plcComm.IsConnected)
            {
                DisconnectPlc();
                await NotifyAsync("info", "PLC", "Đã ngắt kết nối PLC.");
                await PushControlStateAsync();
                return;
            }

            try
            {
                DisconnectPlc(false);

                plcComm = new PLCCommunication(plcIpAddress, plcPort, logicalStation);
                // Set logical station number from UI
                // plcDevice.ActLogicalStationNumber is set in PLCCommunication constructor
                if (!plcComm.Connect())
                {
                    UpdateConnectionState(false, "PLC disconnected");
                    UpdateIntegrityFault("PLC connection returned an error.");
                    await NotifyAsync("error", "PLC", "PLC connect returned an error.");
                    await PushControlStateAsync();
                    return;
                }

                UpdateConnectionState(true, "PLC connected");
                UpdateIntegrityState(true);
                plcPollTimer.Start();
                await PushControlStateAsync();
                await NotifyAsync("success", "PLC", "PLC connected successfully.");
            }
            catch (Exception ex)
            {
                UpdateConnectionState(false, "PLC disconnected");
                UpdateIntegrityFault(ex.Message);
                await PushControlStateAsync();
                await NotifyAsync("error", "PLC", ex.Message);
            }
        }

        private async Task HandleSetVelocityAsync(int value)
        {
            // Velocity write removed - use Cd.14 buffer addresses if needed
            await NotifyAsync("info", "PLC", "Velocity control via Cd.14 buffer not yet implemented.");
            await PushControlStateAsync();
        }

        private async Task HandleSetTelemetryWatchListAsync(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("registers", out object regsObj) && regsObj is object[] regsArr)
            {
                telemetryRegisters.Clear();
                foreach (var r in regsArr)
                {
                    if (r is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        telemetryRegisters.Add(s.Trim().ToUpperInvariant());
                    }
                }
                await PushTelemetryStateAsync();
            }
        }

        private async Task HandleAddRegisterAsync(string register)
        {
            register = (register ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(register))
            {
                return;
            }

            if (!telemetryRegisters.Contains(register))
            {
                telemetryRegisters.Add(register);
                await PushTelemetryStateAsync();
            }
        }

        private async Task HandleRemoveRegisterAsync(string register)
        {
            if (telemetryRegisters.Remove(register))
            {
                await PushTelemetryStateAsync();
            }
        }

        private async Task HandleJogWriteAsync(int offset, bool active)
        {
            if (offset < 0)
            {
                return;
            }

            try
            {
                EnsureConnected();
                string register = GetSequentialDevice(JogBaseRegister, offset);
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue(register, v);
                UpdateIntegrityState(true);
                AddLogEntry(register, v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "Jog");
                
                if (active)
                {
                    string dir = "Unknown";
                    switch(offset) {
                        case 0: dir = "Right (X+)"; break;
                        case 1: dir = "Left (X-)"; break;
                        case 2: dir = "Up (Y+)"; break;
                        case 3: dir = "Down (Y-)"; break;
                        case 4: dir = "Z+"; break;
                        case 5: dir = "Z-"; break;
                    }
                    await NotifyAsync("info", "Jog", $"Started Jog {dir} ({register})");
                }
            }
            catch (Exception ex)
            {
                if (active)
                {
                    UpdateIntegrityFault(ex.Message);
                    AddLogEntry(JogBaseRegister, (active ? 1 : 0).ToString(CultureInfo.InvariantCulture), "Write", "Error", ex.Message);
                    await NotifyAsync("error", "Jog", ex.Message);
                    await PushControlStateAsync();
                }
            }
        }

        private async Task HandleGoHomeWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M502", v);
                UpdateIntegrityState(true);
                AddLogEntry("M502", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "GoHome");
                
                if (active)
                {
                    await NotifyAsync("warning", "System", "Activated GO HOME command (M502)");
                }
            }
            catch (Exception ex)
            {
                if (active)
                {
                    UpdateIntegrityFault(ex.Message);
                    AddLogEntry("M502", (active ? 1 : 0).ToString(CultureInfo.InvariantCulture), "Write", "Error", ex.Message);
                    await NotifyAsync("error", "Go Home", ex.Message);
                    await PushControlStateAsync();
                }
            }
        }

        private async Task HandleResetErrorWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M300", v);
                UpdateIntegrityState(true);
                AddLogEntry("M300", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "ResetError");
                
                if (active)
                {
                    await NotifyAsync("success", "System", "Activated RESET ERROR command (M300)");
                }
            }
            catch (Exception ex)
            {
                if (active)
                {
                    UpdateIntegrityFault(ex.Message);
                    AddLogEntry("M300", (active ? 1 : 0).ToString(CultureInfo.InvariantCulture), "Write", "Error", ex.Message);
                    await NotifyAsync("error", "Reset Error", ex.Message);
                    await PushControlStateAsync();
                }
            }
        }

        private async Task HandleStartWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M2000", v);
                UpdateIntegrityState(true);
                AddLogEntry("M2000", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "Start");
                
                if (active)
                {
                    await NotifyAsync("success", "System", "Activated START command (M2000)");
                }
            }
            catch (Exception ex)
            {
                if (active)
                {
                    UpdateIntegrityFault(ex.Message);
                    AddLogEntry("M2000", (active ? 1 : 0).ToString(CultureInfo.InvariantCulture), "Write", "Error", ex.Message);
                    await NotifyAsync("error", "Start", ex.Message);
                    await PushControlStateAsync();
                }
            }
        }

        private async Task HandleSetJogSpeedAsync(double value)
        {
            try
            {
                EnsureConnected();
                
                // Quy đổi số thực (IEEE 754) sang 32-bit integer để ghi xuống 2 thanh ghi D406-D407
                float fVal = (float)value;
                byte[] bytes = BitConverter.GetBytes(fVal);
                int intVal = BitConverter.ToInt32(bytes, 0);

                plcComm.WriteDeviceValue("D406", intVal);
                AddLogEntry("D406", value.ToString("F3", CultureInfo.InvariantCulture), "Write", "OK", "SetJogSpeed(Float)");
                await NotifyAsync("success", "Settings", $"Updated Jog speed (Real): {value:F3} (D406)");
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "Settings", "Error updating Jog speed: " + ex.Message);
            }
        }

        private async Task HandleEmergencyStopAsync()
        {
            try
            {
                EnsureConnected();
                plcComm.WriteDeviceValue(EmergencyStopRegister, 1);
                AddLogEntry(EmergencyStopRegister, "1", "Write", "OK", "EmergencyStop");
                UpdateIntegrityFault("Emergency stop triggered");
                await PushControlStateAsync();
                await NotifyAsync("error", "PLC", "Emergency stop written to " + EmergencyStopRegister + ".");
            }
            catch (Exception ex)
            {
                UpdateIntegrityFault(ex.Message);
                AddLogEntry(EmergencyStopRegister, "1", "Write", "Error", ex.Message);
                await PushControlStateAsync();
                await NotifyAsync("error", "PLC", ex.Message);
            }
        }

        private async Task HandleOpenDxfAsync()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*";
                dialog.Title = "Open DXF file";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                dialog.RestoreDirectory = true;
                dialog.FileName = string.Empty;
                if (!string.IsNullOrWhiteSpace(activeCadDocument?.DirectoryPath) && Directory.Exists(activeCadDocument.DirectoryPath))
                {
                    dialog.InitialDirectory = activeCadDocument.DirectoryPath;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    string selectedPath = Path.GetFullPath(dialog.FileName);
                    AddLogEntry("DXF", selectedPath, "Read", "Selected", "OpenFileDialog");

                    LoadCadDocument(selectedPath);
                    if (activeCadDocument != null && activeCadDocument.Primitives != null)
                    {
                        var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);
                        activeCadDocument.Primitives.Clear();
                        foreach (var path in paths)
                        {
                            activeCadDocument.Primitives.AddRange(path);
                        }
                    }
                    currentView = "dxf";
                    await PushDxfStateAsync();
                    AddLogEntry("DXF", activeCadDocument?.FilePath ?? selectedPath, "Read", "OK", $"Loaded file: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");
                    await NotifyAsync("success", "DXF", $"Loaded: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");
                }
                catch (Exception ex)
                {
                    await NotifyAsync("error", "DXF", ex.Message);
                }
            }
        }

        private async Task HandleAssignPointAsync(string slot, string pointKey)
        {
            if (string.IsNullOrEmpty(pointKey)) return;
            var point = activeCadDocument?.Points?.FirstOrDefault(p => string.Equals(p.Key, pointKey, StringComparison.OrdinalIgnoreCase));
            if (point == null)
            {
                await NotifyAsync("info", "DXF", "Please select a point before assigning.");
                return;
            }

            assignedPointKeys[slot] = point.Key;
            selectedCadPointKey = point.Key;

            await PushDxfStateAsync();
        }

        private string globalZDown = "";
        private string globalZSafe = "";

        private async Task HandleProcessValueAsync(string key, string value)
        {
            if (string.Equals(key, "speed", StringComparison.OrdinalIgnoreCase))
            {
                // Áp dụng tốc độ cho toàn bộ các lệnh chạy
                foreach (var row in processRows)
                {
                    row.Speed = value;
                }
            }
            else if (string.Equals(key, "zDown", StringComparison.OrdinalIgnoreCase))
            {
                globalZDown = value;
            }
            else if (string.Equals(key, "zSafe", StringComparison.OrdinalIgnoreCase))
            {
                globalZSafe = value;
            }
            
            await PushDxfStateAsync();
            await NotifyAsync("success", "Configuration", $"Updated {key} = {value}");
        }

        private async Task HandleProcessRowValueAsync(int index, string field, string value)
        {
            if (index < 0 || index >= processRows.Count) return;
            var row = processRows[index];
            if (field == "mcode") row.MCodeValue = value;
            else if (field == "dwell") row.Dwell = value;
            else if (field == "speed") row.Speed = value;

            await PushDxfStateAsync();
        }

        private void LoadCadDocument(string filePath)
        {
            activeCadDocument = cadService.Load(filePath);
            selectedCadPointKey = activeCadDocument.Points.FirstOrDefault()?.Key;
            ResetPointAssignments();
        }

        private void ResetPointAssignments()
        {
            assignedPointKeys.Clear();
        }

        private async void PlcPollTimer_Tick(object sender, EventArgs e)
        {
            if (isClosing || isPolling || plcComm == null || !plcComm.IsConnected)
            {
                return;
            }

            isPolling = true;
            try
            {
                // Move PLC COM reads to background thread to avoid blocking UI
                var comm = plcComm;
                if (comm == null || !comm.IsConnected) { isPolling = false; return; }

                await Task.Run(() =>
                {
                    if (isClosing) return;
                    // Read all 4 axes: monitor area (15 words) + control area (20 words)
                    for (int i = 0; i < 4; i++)
                    {
                        try
                        {
                            if (isClosing) return;
                            // Read Current Position and Speed from D registers as requested
                            // Axis 1: D0, D4 | Axis 2: D10, D14 | Axis 3: D20, D24 | Axis 4: D30, D34
                            int dBase = i * 10;
                            int[] posData = comm.ReadDeviceRange($"D{dBase}", 2);
                            axCurrentPos[i] = (posData[1] << 16) | (posData[0] & 0xFFFF);

                            int[] speedData = comm.ReadDeviceRange($"D{dBase + 4}", 2);
                            axCurrentSpeed[i] = (speedData[1] << 16) | (speedData[0] & 0xFFFF);

                            // Other parameters still from Buffer Memory
                            int[] mon = comm.ReadBuffer(0, MonitorBaseG[i], 15);
                            axErrorCode[i]    = mon[OffErrorCode];
                            axWarningCode[i]  = mon[OffWarningCode];
                            axAxisStatus[i]   = mon[OffAxisStatus];

                            int[] ctl = comm.ReadBuffer(0, ControlBaseG[i], 20);
                            axStartNo[i]     = ctl[OffStartNo];
                            axErrorReset[i]  = ctl[OffErrorReset];
                            axNewSpeed[i]    = (ctl[OffNewSpeed + 1] << 16) | (ctl[OffNewSpeed] & 0xFFFF);
                        }
                        catch
                        {
                            // Silently skip failed axis reads during polling
                        }
                    }

                    // Read global Jog Speed (D406 - Float)
                    try
                    {
                        if (!isClosing)
                        {
                            int[] d406Raw = comm.ReadDeviceRange("D406", 2);
                            byte[] bytes = BitConverter.GetBytes((d406Raw[1] << 16) | (d406Raw[0] & 0xFFFF));
                            currentJogSpeedD406 = BitConverter.ToSingle(bytes, 0);
                        }
                    }
                    catch { }

                    // Read monitor rows on background thread too
                    foreach (MonitorRow row in monitorRows)
                    {
                        try
                        {
                            if (isClosing) return;
                            int value = comm.ReadDeviceValue(row.Register);
                            row.Value = value.ToString(CultureInfo.InvariantCulture);
                            row.Status = "OK";
                        }
                        catch (Exception ex)
                        {
                            row.Status = ex.Message;
                        }
                    }
                });

                if (isClosing) return;
                UpdateIntegrityState(true);

                // Push control state always; push telemetry only when viewing telemetry tab
                await PushControlStateAsync();
                if (currentView == "telemetry")
                {
                    await PushTelemetryStateAsync();
                }
            }
            catch (Exception ex)
            {
                if (!isClosing)
                {
                    UpdateIntegrityFault(ex.Message);
                }
            }
            finally
            {
                isPolling = false;
            }
        }

        private void DisconnectPlc(bool updateUi = true)
        {
            plcPollTimer.Stop();

            if (plcComm != null)
            {
                try
                {
                    plcComm.Dispose();
                }
                catch
                {
                }

                plcComm = null;
            }

            foreach (MonitorRow row in monitorRows)
            {
                row.Status = "Disconnected";
            }

            if (updateUi)
            {
                UpdateConnectionState(false, "PLC disconnected");
                UpdateIntegrityState(false);
            }
        }

        private void InitializeProcessRows()
        {
            processRows.Clear();
            processRows.Add(new ProcessRow { Key = "start", MotionType = "Điểm bắt đầu" });
            processRows.Add(new ProcessRow { Key = "glueStart", MotionType = "Điểm bắt đầu bơm", MCodeValue = "Bật keo" });
            processRows.Add(new ProcessRow { Key = "glueEnd", MotionType = "Điểm kết thúc bơm", MCodeValue = "Tắt keo" });
            processRows.Add(new ProcessRow { Key = "zDown", MotionType = "Độ cao Z hạ" });
            processRows.Add(new ProcessRow { Key = "zSafe", MotionType = "Độ cao Z an toàn" });
            processRows.Add(new ProcessRow { Key = "speed", MotionType = "Tốc độ" });
        }

        private ProcessRow GetProcessRow(string key)
        {
            return processRows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureConnected()
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                throw new InvalidOperationException("PLC is not connected.");
            }
        }

        private static string GetSequentialDevice(string baseDevice, int offset)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(baseDevice, @"^(?<prefix>[A-Za-z]+)(?<address>\d+)$");
            if (!match.Success)
            {
                throw new InvalidOperationException("Invalid base device: " + baseDevice);
            }

            string prefix = match.Groups["prefix"].Value;
            int address = int.Parse(match.Groups["address"].Value, CultureInfo.InvariantCulture);
            return prefix + (address + offset).ToString(CultureInfo.InvariantCulture);
        }

        private void UpdateConnectionState(bool connected, string bannerText)
        {
            connectionBanner = bannerText;
        }

        private void UpdateIntegrityState(bool connected)
        {
            integrityState = connected ? "READY" : "IDLE";
            integrityDetail = connected ? "RUN" : "STOP";
            integrityTone = connected ? "ready" : "idle";
        }

        private void UpdateIntegrityFault(string errorMessage)
        {
            integrityState = "FAULT";
            integrityDetail = string.IsNullOrWhiteSpace(errorMessage) ? "PLC error" : errorMessage;
            integrityTone = "fault";
        }

        private Task PushAllStateAsync()
        {
            return Task.WhenAll(PushControlStateAsync(), PushDxfStateAsync(), PushTelemetryStateAsync(), PushLogsStateAsync());
        }

        /// <summary>
        /// Convert raw buffer value to mm (Pr.1=0: raw × 10⁻¹ μm = raw/10000 mm)
        /// </summary>
        private static string FormatPositionMm(int rawValue)
        {
            double mm = rawValue / 10000.0;
            return mm.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static string FormatSpeedMm(int rawValue)
        {
            // Thường là 10^-2 mm/min cho Mitsubishi monitor
            double mmMin = rawValue / 100.0;
            return mmMin.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatAxisStatus(int status)
        {
            // Trạng thái từ Md.26 của trục
            switch (status)
            {
                case -2: return "Step standby";
                case -1: return "Error";
                case 0: return "Standby";
                case 1: return "Stopped";
                case 2: return "Interpolation";
                case 3: return "JOG operation";
                case 4: return "Manual pulse generator";
                case 5: return "Analyzing";
                case 6: return "Special start standby";
                case 7: return "OPR (Homing)";
                case 8: return "Position control";
                case 9: return "Speed control";
                case 10: return "Speed ctrl (spd-pos)";
                case 11: return "Pos ctrl (spd-pos)";
                case 12: return "Pos ctrl (pos-spd)";
                default: return $"Unknown ({status})";
            }
        }

        private Task PushControlStateAsync()
        {
            bool connected = plcComm != null && plcComm.IsConnected;
            string dash = "--";

            var axesData = new object[4];
            for (int i = 0; i < 4; i++)
            {
                int mb = MonitorBaseG[i];
                int cb = ControlBaseG[i];

                // Nếu `axAxisStatus[i]` có thể là số âm, ta xử lý ép kiểu bù 2 vì GetDevice trả về ushort qua COM.
                int rawStatus = axAxisStatus[i];
                if (rawStatus > 32767) rawStatus -= 65536;

                axesData[i] = new
                {
                    index = i + 1,
                    currentPos     = connected ? FormatPositionMm(axCurrentPos[i]) : dash,
                    currentPosAddr = $"D{i * 10}",
                    currentSpeed   = connected ? FormatSpeedMm(axCurrentSpeed[i]) : dash,
                    currentSpeedAddr = $"D{i * 10 + 4}",
                    errorCode      = connected ? axErrorCode[i].ToString(CultureInfo.InvariantCulture) : dash,
                    errorCodeAddr  = $"U0\\G{mb + OffErrorCode}",
                    warningCode    = connected ? axWarningCode[i].ToString(CultureInfo.InvariantCulture) : dash,
                    warningCodeAddr = $"U0\\G{mb + OffWarningCode}",
                    axisStatus     = connected ? FormatAxisStatus(rawStatus) : dash,
                    axisStatusAddr = $"U0\\G{mb + OffAxisStatus}",
                    startNo        = connected ? axStartNo[i].ToString(CultureInfo.InvariantCulture) : dash,
                    startNoAddr    = $"U0\\G{cb + OffStartNo}",
                    errorReset     = connected ? axErrorReset[i].ToString(CultureInfo.InvariantCulture) : dash,
                    errorResetAddr = $"U0\\G{cb + OffErrorReset}",
                    jogSpeed       = connected ? FormatSpeedMm(axJogSpeed[i]) : dash,
                    jogSpeedAddr   = "D406",
                    newSpeed       = connected ? axNewSpeed[i].ToString(CultureInfo.InvariantCulture) : dash,
                    newSpeedAddr   = $"U0\\G{cb + OffNewSpeed}"
                };
            }

            object payload = new
            {
                view = currentView,
                theme = currentTheme,
                connection = new
                {
                    connected,
                    station = logicalStation,
                    banner = connectionBanner,
                    meta = $"MX Component logical station: {logicalStation}",
                    buttonText = connected ? "DISCONNECT PLC Q" : "CONNECT PLC Q"
                },
                axes = axesData,
                jogSpeedD406 = currentJogSpeedD406,
                events = new object[0]
            };

            return PostToUiAsync("controlState", payload);
        }

        private Task PushDxfStateAsync()
        {
            object payload = new
            {
                view = currentView,
                theme = currentTheme,
                filePath = activeCadDocument?.FilePath ?? string.Empty,
                fileName = activeCadDocument?.FileName ?? string.Empty,
                bounds = activeCadDocument == null
                    ? new
                    {
                        left = 0.0,
                        top = 0.0,
                        right = 100.0,
                        bottom = 100.0,
                        width = 100.0,
                        height = 100.0
                    }
                    : new
                    {
                        left = activeCadDocument.Bounds.Left,
                        top = activeCadDocument.Bounds.Top,
                        right = activeCadDocument.Bounds.Right,
                        bottom = activeCadDocument.Bounds.Bottom,
                        width = activeCadDocument.Bounds.Width,
                        height = activeCadDocument.Bounds.Height
                    },
                primitives = activeCadDocument == null
                    ? new List<object>()
                    : activeCadDocument.Primitives.Select(primitive => (object)new
                    {
                        sourceType = primitive.SourceType,
                        points = primitive.Points.Select(point => new
                        {
                            x = point.X,
                            y = point.Y
                        }).ToList(),
                        center = primitive.Center != null ? new { x = primitive.Center.X, y = primitive.Center.Y } : null,
                        isCw = primitive.IsCw,
                        isCircle = primitive.IsCircle
                    }).ToList(),
                points = activeCadDocument == null
                    ? new List<object>()
                    : activeCadDocument.Points.Select(point => (object)new
                    {
                        index = point.Index,
                        lineType = point.LineType,
                        x = point.X,
                        y = point.Y,
                        xDisplay = point.XDisplay,
                        yDisplay = point.YDisplay,
                        key = point.Key
                    }).ToList(),
                selectedPointKey = selectedCadPointKey ?? string.Empty,
                assignedPointKeys,
                processRows = processRows.Select(row => new
                {
                    key = row.Key,
                    motionType = row.MotionType,
                    mCodeValue = row.MCodeValue ?? string.Empty,
                    dwell = row.Dwell ?? string.Empty,
                    speed = row.Speed ?? string.Empty,
                    endCoordinate = row.EndCoordinate ?? string.Empty,
                    centerCoordinate = row.CenterCoordinate ?? string.Empty
                }).ToList()
            };

            return PostToUiAsync("dxfState", payload);
        }

        private Task NotifyAsync(string kind, string title, string message)
        {
            return PostToUiAsync("notify", new
            {
                kind,
                title,
                message
            });
        }

        private Task PushTelemetryStateAsync()
        {
            bool connected = plcComm != null && plcComm.IsConnected;
            var dValues = new System.Collections.Generic.List<object>();
            var buffers = new System.Collections.Generic.List<object>();

            foreach (var reg in telemetryRegisters)
            {
                if (connected)
                {
                    try
                    {
                        int v = plcComm.ReadDeviceValue(reg);
                        dValues.Add(new { register = reg, value = v, ok = true });
                    }
                    catch (Exception ex)
                    {
                        dValues.Add(new { register = reg, value = (int?)null, ok = false, error = ex.Message });
                    }
                }
                else
                {
                    dValues.Add(new { register = reg, value = (int?)null, ok = false, error = "Disconnected" });
                }
            }

            foreach (var buf in telemetryBuffers)
            {
                if (connected)
                {
                    try
                    {
                        int[] arr = plcComm.ReadDeviceRange(buf.Path, buf.Length);
                        buffers.Add(new { path = buf.Path, values = arr, ok = true });
                    }
                    catch (Exception ex)
                    {
                        buffers.Add(new { path = buf.Path, values = new int[0], ok = false, error = ex.Message });
                    }
                }
                else
                {
                    buffers.Add(new { path = buf.Path, values = new int[0], ok = false, error = "Disconnected" });
                }
            }

            var payload = new
            {
                view = currentView,
                theme = currentTheme,
                connected,
                dValues,
                buffers
            };

            return PostToUiAsync("telemetry", payload);
        }

        private Task PushLogsStateAsync()
        {
            var outLogs = logs.Select(l => new
            {
                timestamp = l.Timestamp.ToString("o"),
                direction = l.Direction,
                address = l.Address,
                value = l.Value,
                status = l.Status,
                message = l.Message
            }).ToList();

            var payload = new { view = currentView, theme = currentTheme, logs = outLogs };
            return PostToUiAsync("logsState", payload);
        }

        private void AddLogEntry(string address, string value, string direction = "Write", string status = "OK", string message = null)
        {
            try
            {
                logs.Insert(0, new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Direction = direction,
                    Address = address,
                    Value = value,
                    Status = status,
                    Message = message
                });

                // keep recent 500 entries
                if (logs.Count > 500) logs.RemoveRange(500, logs.Count - 500);

                // fire-and-forget push to UI
                _ = PushLogsStateAsync();
            }
            catch
            {
                // ignore logging errors
            }
        }

        private Task HandleClearLogsAsync()
        {
            logs.Clear();
            return PushLogsStateAsync();
        }

        private async Task HandleAddTelemetryRegisterAsync(string register)
        {
            if (string.IsNullOrWhiteSpace(register)) return;
            register = register.Trim().ToUpperInvariant();
            if (telemetryRegisters.Exists(r => string.Equals(r, register, StringComparison.OrdinalIgnoreCase)))
            {
                await NotifyAsync("info", "Telemetry", "Register already exists.");
                return;
            }

            telemetryRegisters.Add(register);
            await PushTelemetryStateAsync();
        }

        private async Task HandleRemoveTelemetryRegisterAsync(string register)
        {
            if (string.IsNullOrWhiteSpace(register)) return;
            var item = telemetryRegisters.Find(r => string.Equals(r, register, StringComparison.OrdinalIgnoreCase));
            if (item != null) telemetryRegisters.Remove(item);
            await PushTelemetryStateAsync();
        }

        private async Task HandleAddTelemetryBufferAsync(string path, int length)
        {
            if (string.IsNullOrWhiteSpace(path) || length <= 0) return;
            telemetryBuffers.Add(new TelemetryBuffer { Path = path.Trim(), Length = Math.Max(1, length) });
            await PushTelemetryStateAsync();
        }

        private async Task HandleRemoveTelemetryBufferAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var buf = telemetryBuffers.FirstOrDefault(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));
            if (buf != null) telemetryBuffers.Remove(buf);
            await PushTelemetryStateAsync();
        }

        private async Task HandleWriteBufferRequestAsync(string path, int value)
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                await NotifyAsync("error", "Telemetry", "PLC is not connected.");
                return;
            }

            try
            {
                // For testing, attempt to write 32-bit as two words if possible
                string used;
                int result = plcComm.WriteInt32ToDevicePath(path, value, out used);
                AddLogEntry(path, value.ToString(CultureInfo.InvariantCulture), "Write", result == 0 ? "OK" : $"Error({result})", used);
                if (result == 0)
                {
                    await NotifyAsync("success", "Telemetry", $"Successfully wrote to {path} using {used}.");
                }
                else
                {
                    await NotifyAsync("error", "Telemetry", $"Failed to write ({result}) using {used}.");
                }
            }
            catch (Exception ex)
            {
                AddLogEntry(path, value.ToString(CultureInfo.InvariantCulture), "Write", "Error", ex.Message);
                await NotifyAsync("error", "Telemetry", ex.Message);
            }
        }

        private short BuildPositioningIdentifierWord(string motionType, int interpolatedAxis = 1, int accelTimeNo = 0, int decelTimeNo = 0)
        {
            string s = (motionType ?? string.Empty).Trim().ToLowerInvariant();

            bool isEnd = s.Contains("end") || s.Contains("hoàn thành");
            bool isContinuousPath = s.Contains("continuous path");
            bool isContinuousPositioning = s.Contains("continuous positioning");

            // Fallback for old Vietnamese labels.
            if (!isContinuousPath && !isContinuousPositioning)
            {
                if (s.Contains("liên tục")) isContinuousPath = true;
                if (s.Contains("điểm kế tiếp")) isContinuousPositioning = true;
            }

            int da2ControlSystem = 0x0A; // ABS Linear 2
            if (s.Contains("arc cw"))
            {
                da2ControlSystem = 0x0F;
            }
            else if (s.Contains("arc ccw"))
            {
                da2ControlSystem = 0x10;
            }
            else if (s.Contains("circle"))
            {
                da2ControlSystem = s.Contains("ccw") ? 0x10 : 0x0F;
            }

            int da1OperationPattern = 0x03; // Continuous path
            if (isEnd)
            {
                da1OperationPattern = 0x00;
            }
            else if (isContinuousPositioning)
            {
                da1OperationPattern = 0x01;
            }
            else if (isContinuousPath)
            {
                da1OperationPattern = 0x03;
            }

            int wordValue =
                ((da2ControlSystem & 0xFF) << 8) |
                ((interpolatedAxis & 0x03) << 6) |
                ((decelTimeNo & 0x03) << 4) |
                ((accelTimeNo & 0x03) << 2) |
                (da1OperationPattern & 0x03);

            return unchecked((short)wordValue);
        }

        private async Task HandleSendCadXAsync()
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                await NotifyAsync("error", "Telemetry", "PLC is not connected.");
                return;
            }

            // always use processRows
            List<ProcessRow> rowsToSend = processRows;

            if (rowsToSend.Count == 0)
            {
                await NotifyAsync("info", "Telemetry", "No points to send.");
                return;
            }

            // sendCadX = Axis 1 (X axis). Other axes use ProgramBaseG[1..3].
            int axisIndex = 0;
            int baseG = ProgramBaseG[axisIndex];
            const int stride = 10;
            // follow buffer mapping exactly for X axis
            const int offsetMoveCode = 0; // U0\G(2000 + (n-1)*10 + 0)
            const int offsetMCode = 1;    // U0\G(2000 + (n-1)*10 + 1)
            const int offsetDwell = 2;    // U0\G(2000 + (n-1)*10 + 2)
            const int offsetSpeed = 4;    // U0\G(2000 + (n-1)*10 + 4) -> Low word at G2004, High at G2005
            const int offsetPosX = 6;     // U0\G(2000 + (n-1)*10 + 6)  <-- Position X
            const int offsetCenterX = 8;  // U0\G(2000 + (n-1)*10 + 8)  <-- Center X

            int n = 1;
            foreach (var row in rowsToSend)
            {
                // parse EndCoordinate and CenterCoordinate formatted as "X;Y"
                int endX = 0;
                int centerX = 0;
                bool hasEnd = false;
                bool hasCenter = false;

                if (!string.IsNullOrWhiteSpace(row.EndCoordinate))
                {
                    var parts = row.EndCoordinate.Split(';');
                    if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double ex)) { endX = Convert.ToInt32(Math.Round(ex)); hasEnd = true; }
                }
                if (!string.IsNullOrWhiteSpace(row.CenterCoordinate))
                {
                    var parts = row.CenterCoordinate.Split(';');
                    if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double cx)) { centerX = Convert.ToInt32(Math.Round(cx)); hasCenter = true; }
                }

                // parse M code, dwell, speed
                int mcodeVal = 0;
                if (!string.IsNullOrWhiteSpace(row.MCodeValue))
                {
                    int parsed;
                    if (int.TryParse(row.MCodeValue, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) mcodeVal = parsed;
                }

                int dwellVal = 0;
                if (!string.IsNullOrWhiteSpace(row.Dwell))
                {
                    int parsed;
                    if (int.TryParse(row.Dwell, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) dwellVal = parsed;
                }

                int speedVal = 0;
                if (!string.IsNullOrWhiteSpace(row.Speed))
                {
                    int parsed;
                    if (int.TryParse(row.Speed, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) speedVal = parsed;
                }

                if (mcodeVal < short.MinValue || mcodeVal > short.MaxValue)
                {
                    await NotifyAsync("error", "Telemetry", $"M code out of 16-bit range at point {n}: {mcodeVal}");
                    n++;
                    continue;
                }
                if (dwellVal < short.MinValue || dwellVal > short.MaxValue)
                {
                    await NotifyAsync("error", "Telemetry", $"Dwell out of 16-bit range at point {n}: {dwellVal}");
                    n++;
                    continue;
                }

                short moveCode = BuildPositioningIdentifierWord(row.MotionType);

                string deviceBase = $"U0\\G{baseG + (n - 1) * stride}";

                try
                {
                    // Write full Posn identifier word to U0\G(2000 + (n - 1) * 10).
                    string deviceMove = $"U0\\G{baseG + (n - 1) * stride + offsetMoveCode}";
                    string usedMove;
                    int rMove = plcComm.WriteInt16ToDevicePath(deviceMove, moveCode, out usedMove);
                    AddLogEntry(deviceMove, moveCode.ToString(CultureInfo.InvariantCulture), "Write", rMove == 0 ? "OK" : $"Error({rMove})", $"Posn identifier hex=0x{((ushort)moveCode):X4}; {usedMove}");

                    // write M code (16-bit, single word)
                    string deviceM = $"U0\\G{baseG + (n - 1) * stride + offsetMCode}";
                    string usedM;
                    int rM = plcComm.WriteInt16ToDevicePath(deviceM, (short)mcodeVal, out usedM);
                    AddLogEntry(deviceM, mcodeVal.ToString(CultureInfo.InvariantCulture), "Write", rM == 0 ? "OK" : $"Error({rM})", "MCode:" + usedM);

                    // write dwell (16-bit, single word)
                    string deviceDwell = $"U0\\G{baseG + (n - 1) * stride + offsetDwell}";
                    string usedD;
                    int rD = plcComm.WriteInt16ToDevicePath(deviceDwell, (short)dwellVal, out usedD);
                    AddLogEntry(deviceDwell, dwellVal.ToString(CultureInfo.InvariantCulture), "Write", rD == 0 ? "OK" : $"Error({rD})", "Dwell:" + usedD);

                    // write speed
                    string deviceSpeed = $"U0\\G{baseG + (n - 1) * stride + offsetSpeed}";
                    string usedS;
                    int rS = plcComm.WriteInt32ToDevicePath(deviceSpeed, speedVal, out usedS);
                    AddLogEntry(deviceSpeed, speedVal.ToString(CultureInfo.InvariantCulture), "Write", rS == 0 ? "OK" : $"Error({rS})", "Speed:" + usedS);

                    // write position X
                    if (hasEnd)
                    {
                        string devicePosX = $"U0\\G{baseG + (n - 1) * stride + offsetPosX}";
                        string usedX;
                        int rX = plcComm.WriteInt32ToDevicePath(devicePosX, endX, out usedX);
                        AddLogEntry(devicePosX, endX.ToString(CultureInfo.InvariantCulture), "Write", rX == 0 ? "OK" : $"Error({rX})", usedX);
                        if (rX != 0) await NotifyAsync("error", "Telemetry", $"Failed to write End X {devicePosX}: {rX}");
                    }

                    // write center X
                    if (hasCenter)
                    {
                        string deviceCenterX = $"U0\\G{baseG + (n - 1) * stride + offsetCenterX}";
                        string usedCx;
                        int rCx = plcComm.WriteInt32ToDevicePath(deviceCenterX, centerX, out usedCx);
                        AddLogEntry(deviceCenterX, centerX.ToString(CultureInfo.InvariantCulture), "Write", rCx == 0 ? "OK" : $"Error({rCx})", usedCx);
                        if (rCx != 0) await NotifyAsync("error", "Telemetry", $"Failed to write Center X {deviceCenterX}: {rCx}");
                    }
                }
                catch (Exception ex)
                {
                    AddLogEntry(deviceBase, string.Empty, "Write", "Error", ex.Message);
                    await NotifyAsync("error", "Telemetry", ex.Message);
                }

                n++;
            }

            await NotifyAsync("success", "Telemetry", "Sent X-axis CAD coordinates to PLC.");
        }

        private async Task HandleImportCadToProcessAsync()
        {
            if (activeCadDocument == null || activeCadDocument.Primitives == null || activeCadDocument.Primitives.Count == 0)
            {
                await NotifyAsync("info", "DXF", "No CAD data available.");
                return;
            }

            var rows = BuildConnectedPathsFromCad();
            if (rows.Count == 0)
            {
                await NotifyAsync("info", "DXF", "No valid paths found.");
                return;
            }

            // Lấy toạ độ điểm bật/tắt keo để chèn M Code
            string glueStartCoord = null;
            string glueEndCoord = null;

            if (assignedPointKeys.TryGetValue("glueStart", out string gStartKey))
            {
                var pt = activeCadDocument.Points.FirstOrDefault(p => p.Key == gStartKey);
                if (pt != null) glueStartCoord = FormatPoint(pt);
            }
            if (assignedPointKeys.TryGetValue("glueEnd", out string gEndKey))
            {
                var pt = activeCadDocument.Points.FirstOrDefault(p => p.Key == gEndKey);
                if (pt != null) glueEndCoord = FormatPoint(pt);
            }

            foreach (var row in rows)
            {
                if (glueStartCoord != null && string.Equals(row.EndCoordinate, glueStartCoord))
                {
                    row.MCodeValue = "1";
                }
                if (glueEndCoord != null && string.Equals(row.EndCoordinate, glueEndCoord))
                {
                    row.MCodeValue = "2";
                }
            }

            processRows.Clear();
            processRows.AddRange(rows);

            await PushDxfStateAsync();
            await NotifyAsync("success", "DXF", $"Compiled {rows.Count} movement commands into the process table.");
        }

        private List<ProcessRow> BuildConnectedPathsFromCad()
        {
            var result = new List<ProcessRow>();
            if (activeCadDocument == null || activeCadDocument.Primitives == null) return result;

            var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);

            for (int pathIdx = 0; pathIdx < paths.Count; pathIdx++)
            {
                var path = paths[pathIdx];
                bool isLastPath = (pathIdx == paths.Count - 1);

                for (int pIdx = 0; pIdx < path.Count; pIdx++)
                {
                    var prim = path[pIdx];
                    if (prim.Points == null || prim.Points.Count < 2) continue;

                    bool isLastInPath = (pIdx == path.Count - 1);

                    string suffix;
                    if (isLastInPath)
                    {
                        suffix = isLastPath ? " (End)" : " (Continuous Positioning)";
                    }
                    else
                    {
                        suffix = " (Continuous Path)";
                    }

                    // If primitive is Line or Polyline, emit its points
                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        for (int i = 1; i < prim.Points.Count; i++) // skip first point since it's just the start
                        {
                            bool isLastInPrim = (i == prim.Points.Count - 1);
                            string currentSuffix = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";

                            var row = new ProcessRow();
                            row.MotionType = "Line" + currentSuffix;
                            row.EndCoordinate = string.Format(CultureInfo.InvariantCulture, "{0:0.###};{1:0.###}", prim.Points[i].X, prim.Points[i].Y);
                            row.CenterCoordinate = string.Empty;
                            result.Add(row);
                        }
                    }
                    else if (prim.SourceType.Contains("Arc") || prim.SourceType.Contains("Circle"))
                    {
                        var row = new ProcessRow();
                        string arcType = prim.IsCw ? "Arc CW" : "Arc CCW";
                        if (prim.SourceType.Contains("Circle")) arcType = "Circle";

                        row.MotionType = arcType + suffix;
                        
                        var endPt = prim.Points.Last();
                        row.EndCoordinate = string.Format(CultureInfo.InvariantCulture, "{0:0.###};{1:0.###}", endPt.X, endPt.Y);
                        if (prim.Center != null)
                        {
                            row.CenterCoordinate = string.Format(CultureInfo.InvariantCulture, "{0:0.###};{1:0.###}", prim.Center.X, prim.Center.Y);
                        }
                        result.Add(row);
                    }
                }
            }

            return result;
        }

        private List<List<CadDocumentService.CadPrimitiveData>> GetConnectedPathsFromCad(List<CadDocumentService.CadPrimitiveData> primitives)
        {
            var unassigned = new List<CadDocumentService.CadPrimitiveData>(primitives);
            var paths = new List<List<CadDocumentService.CadPrimitiveData>>();

            while (unassigned.Count > 0)
            {
                var currentPath = new List<CadDocumentService.CadPrimitiveData>();
                var current = unassigned[0];
                unassigned.RemoveAt(0);
                currentPath.Add(current);

                bool added = true;
                while (added)
                {
                    added = false;
                    var tailPrim = currentPath.Last();
                    var headPrim = currentPath.First();

                    if (tailPrim.Points == null || tailPrim.Points.Count == 0 || headPrim.Points == null || headPrim.Points.Count == 0) break;
                    
                    var tailPt = tailPrim.Points.Last();
                    var headPt = headPrim.Points.First();

                    for (int i = 0; i < unassigned.Count; i++)
                    {
                        var cand = unassigned[i];
                        if (cand.Points == null || cand.Points.Count == 0) continue;

                        var candStart = cand.Points.First();
                        var candEnd = cand.Points.Last();

                        // Try to attach to tail (Append)
                        if (AreClose(tailPt, candStart))
                        {
                            currentPath.Add(cand);
                            unassigned.RemoveAt(i);
                            added = true;
                            break;
                        }
                        else if (AreClose(tailPt, candEnd))
                        {
                            // Reverse candidate points
                            cand.Points.Reverse();
                            // If it's an Arc, reversing it means CW becomes CCW
                            if (cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Add(cand);
                            unassigned.RemoveAt(i);
                            added = true;
                            break;
                        }
                        // Try to attach to head (Prepend)
                        else if (AreClose(headPt, candEnd))
                        {
                            currentPath.Insert(0, cand);
                            unassigned.RemoveAt(i);
                            added = true;
                            break;
                        }
                        else if (AreClose(headPt, candStart))
                        {
                            cand.Points.Reverse();
                            if (cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Insert(0, cand);
                            unassigned.RemoveAt(i);
                            added = true;
                            break;
                        }
                    }
                }
                paths.Add(currentPath);
            }
            return paths;
        }

        private bool AreClose(CadDocumentService.CadCoordinate a, CadDocumentService.CadCoordinate b)
        {
            return Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
        }

        private CadDocumentService.CadPointData FindCircumferencePointFromPrimitives(CadDocumentService.CadLoadResult doc, CadDocumentService.CadPointData center)
        {
            if (doc == null || doc.Primitives == null) return null;
            // choose a point from primitives that is not equal to center and at reasonable distance
            foreach (var prim in doc.Primitives)
            {
                if (prim?.Points == null) continue;
                foreach (var pt in prim.Points)
                {
                    if (pt == null) continue;
                    double dx = pt.X - (center?.X ?? 0);
                    double dy = pt.Y - (center?.Y ?? 0);
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > 1e-3) // found a circumference-like point
                    {
                        return new CadDocumentService.CadPointData
                        {
                            X = pt.X,
                            Y = pt.Y,
                            XDisplay = pt.X.ToString(CultureInfo.InvariantCulture),
                            YDisplay = pt.Y.ToString(CultureInfo.InvariantCulture),
                            Key = Guid.NewGuid().ToString(),
                            Index = -1,
                            LineType = "circum"
                        };
                    }
                }
            }
            return null;
        }

        private async Task PostToUiAsync(string type, object payload)
        {
            if (isClosing || !webReady)
            {
                return;
            }

            try
            {
                if (webView == null || webView.IsDisposed || webView.CoreWebView2 == null)
                {
                    return;
                }

                string json = serializer.Serialize(new { type, payload });
                await webView.CoreWebView2.ExecuteScriptAsync("window.app && window.app.receive(" + json + ");");
            }
            catch (ObjectDisposedException)
            {
                // WebView2 was disposed during close — ignore
            }
            catch (InvalidOperationException)
            {
                // WebView2 not in valid state — ignore
            }
        }

        private static Dictionary<string, object> GetMap(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value))
            {
                return new Dictionary<string, object>();
            }

            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static string GetString(Dictionary<string, object> source, string key, string fallback = "")
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }

        private static int GetInt(Dictionary<string, object> source, string key, int fallback = 0)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is long)
            {
                return Convert.ToInt32((long)value, CultureInfo.InvariantCulture);
            }

            if (value is double)
            {
                return Convert.ToInt32((double)value, CultureInfo.InvariantCulture);
            }

            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static double GetDouble(Dictionary<string, object> source, string key, double fallback = 0.0)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string FormatPoint(CadDocumentService.CadPointData point)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}", point.X, point.Y);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Set closing flag FIRST to stop all async operations immediately
            isClosing = true;
            webReady = false;

            plcPollTimer.Stop();
            plcPollTimer.Tick -= PlcPollTimer_Tick;

            if (plcComm != null)
            {
                try { plcComm.Dispose(); } catch { }
                plcComm = null;
            }

            base.OnFormClosing(e);
        }

        private sealed class MonitorRow
        {
            public string Register { get; set; }
            public string Value { get; set; }
            public string Status { get; set; }
        }

        private sealed class ProcessRow
        {
            public string Key { get; set; }
            public string MotionType { get; set; }
            public string MCodeValue { get; set; }
            public string Dwell { get; set; }
            public string Speed { get; set; }
            public string EndCoordinate { get; set; }
            public string CenterCoordinate { get; set; }
        }

        private sealed class TelemetryBuffer
        {
            public string Path { get; set; }
            public int Length { get; set; }
        }

        private sealed class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Direction { get; set; }
            public string Address { get; set; }
            public string Value { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
        }


    }
}
