using System.Collections.Generic;
using System.Threading.Tasks;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — WebView2 message routing.
    /// Nhận tất cả tin nhắn từ giao diện HTML và điều hướng sang handler tương ứng.
    /// </summary>
    public partial class Form1
    {
        private async void CoreWebView2_WebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var message = serializer.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                if (message == null) return;

                string action = GetString(message, "action");
                var payload   = GetMap(message, "payload");

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

                    // ── PLC Connection ───────────────────────────────────────────
                    case "connectToggle":
                        await HandleConnectToggleAsync(payload);
                        break;

                    // ── PLC Motion Control ──────────────────────────────────────
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

                    // ── DXF / CAD ────────────────────────────────────────────────
                    case "openDxf":
                        await HandleOpenDxfAsync();
                        break;

                    case "selectCadPoint":
                        selectedCadPointKey = GetString(payload, "key");
                        await PushDxfStateAsync();
                        break;

                    case "assignPoint":
                        await HandleAssignPointAsync(
                            GetString(payload, "slot"),
                            GetString(payload, "key", selectedCadPointKey));
                        break;

                    case "setProcessValue":
                        await HandleProcessValueAsync(
                            GetString(payload, "key"),
                            GetString(payload, "value"));
                        break;

                    case "setOffset":
                        offsetX = GetDouble(payload, "x", offsetX);
                        offsetY = GetDouble(payload, "y", offsetY);
                        SaveSettingsToFile();
                        await PushDxfStateAsync();
                        await HandleScanLimitsAsync();
                        break;

                    case "setProcessRowValue":
                        await HandleProcessRowValueAsync(
                            GetInt(payload, "index", -1),
                            GetString(payload, "field"),
                            GetString(payload, "value"));
                        break;

                    case "importCadToProcess":
                        await HandleImportCadToProcessAsync();
                        await PushDxfStateAsync();
                        break;

                    case "saveGcode":
                        await HandleSaveGcodeAsync(GetString(payload, "text"));
                        break;

                    case "previewGcode":
                        await HandlePreviewGcodeAsync(GetString(payload, "text"));
                        break;

                    case "newGcode":
                        await HandleNewGcodeAsync();
                        break;

                    case "runAction":
                        await NotifyAsync("info", "DXF RUN", "Các nút Resume, Pause, Start đã có UI HTML.");
                        break;

                    // ── Telemetry ────────────────────────────────────────────────
                    case "addTelemetryRegister":
                        await HandleAddTelemetryRegisterAsync(GetString(payload, "register"));
                        break;

                    case "removeTelemetryRegister":
                        await HandleRemoveTelemetryRegisterAsync(GetString(payload, "register"));
                        break;

                    case "addTelemetryBuffer":
                        await HandleAddTelemetryBufferAsync(
                            GetString(payload, "path"),
                            GetInt(payload, "length", 1));
                        break;

                    case "removeTelemetryBuffer":
                        await HandleRemoveTelemetryBufferAsync(GetString(payload, "path"));
                        break;

                    case "writeBufferRequest":
                        await HandleWriteBufferRequestAsync(
                            GetString(payload, "path"),
                            GetInt(payload, "value", 0));
                        break;

                    case "sendCadX":
                        await HandleSendCadXAsync();
                        break;

                    case "clearBuffer":
                        await HandleClearBufferAsync();
                        break;

                    // ── Settings ─────────────────────────────────────────────────
                    case "setG0Speed":
                        rapidSpeed = GetInt(payload, "value", 10000).ToString();
                        SaveSettingsToFile();
                        break;

                    case "setPlcConnection":
                        plcIpAddress = GetString(payload, "ip");
                        plcPort = GetInt(payload, "port", 3000);
                        SaveSettingsToFile();
                        break;

                    case "setWorkspace":
                        workspaceWidth = GetDouble(payload, "width", workspaceWidth);
                        workspaceHeight = GetDouble(payload, "height", workspaceHeight);
                        SaveSettingsToFile();
                        await PushDxfStateAsync();
                        break;

                    case "setWcsOffset":
                        {
                            string wcs = GetString(payload, "wcs");
                            double wx = GetDouble(payload, "x", 0);
                            double wy = GetDouble(payload, "y", 0);
                            int wcsIdx = 0;
                            if (wcs == "G55") wcsIdx = 1;
                            else if (wcs == "G56") wcsIdx = 2;
                            else if (wcs == "G57") wcsIdx = 3;
                            else if (wcs == "G58") wcsIdx = 4;
                            else if (wcs == "G59") wcsIdx = 5;
                            activeWcs = wcs;
                            wcsOffsetX[wcsIdx] = wx;
                            wcsOffsetY[wcsIdx] = wy;
                            SaveSettingsToFile();
                            await NotifyAsync("success", "WCS", $"{wcs} offset X={wx} Y={wy}");
                        }
                        break;

                    // ── Log ─────────────────────────────────────────────────────
                    case "clearLogs":
                        await HandleClearLogsAsync();
                        break;
                }
            }
            catch (System.Exception ex)
            {
                // Không để exception thoát ra ngoài async void — sẽ crash app
                System.Diagnostics.Debug.WriteLine("MessageHandler error: " + ex);
                try { await NotifyAsync("error", "UI bridge", ex.Message); } catch { }
            }
        }
    }
}
