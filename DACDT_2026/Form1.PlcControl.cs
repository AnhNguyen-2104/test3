using System;
using System.Globalization;
using System.Threading.Tasks;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — PLC control handlers: connect/disconnect, jog, go-home,
    /// reset error, start, set jog speed, emergency stop, polling timer.
    /// </summary>
    public partial class Form1
    {
        // ── Connection ───────────────────────────────────────────────────────────
        private async Task HandleConnectToggleAsync(System.Collections.Generic.Dictionary<string, object> payload)
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

        private void DisconnectPlc(bool updateUi = true)
        {
            plcPollTimer.Stop();

            if (plcComm != null)
            {
                try { plcComm.Dispose(); } catch { }
                plcComm = null;
            }

            foreach (var row in monitorRows)
                row.Status = "Disconnected";

            if (updateUi)
            {
                UpdateConnectionState(false, "PLC disconnected");
                UpdateIntegrityState(false);
            }
        }

        // ── Velocity (placeholder) ───────────────────────────────────────────────
        private async Task HandleSetVelocityAsync(int value)
        {
            await NotifyAsync("info", "PLC", "Velocity control via Cd.14 buffer not yet implemented.");
            await PushControlStateAsync();
        }

        // ── Jog ─────────────────────────────────────────────────────────────────
        private async Task HandleJogWriteAsync(int offset, bool active)
        {
            if (offset < 0) return;

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
                    switch (offset)
                    {
                        case 0: dir = "Right (X+)"; break;
                        case 1: dir = "Left (X-)";  break;
                        case 2: dir = "Up (Y+)";    break;
                        case 3: dir = "Down (Y-)";  break;
                        case 4: dir = "Z+";         break;
                        case 5: dir = "Z-";         break;
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

        // ── Go Home ──────────────────────────────────────────────────────────────
        private async Task HandleGoHomeWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M502", v);
                UpdateIntegrityState(true);
                AddLogEntry("M502", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "GoHome");
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

        // ── Reset Error ──────────────────────────────────────────────────────────
        private async Task HandleResetErrorWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M300", v);
                UpdateIntegrityState(true);
                AddLogEntry("M300", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "ResetError");
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

        // ── Start ────────────────────────────────────────────────────────────────
        private async Task HandleStartWriteAsync(bool active)
        {
            try
            {
                EnsureConnected();
                int v = active ? 1 : 0;
                plcComm.WriteDeviceValue("M2000", v);
                UpdateIntegrityState(true);
                AddLogEntry("M2000", v.ToString(CultureInfo.InvariantCulture), "Write", "OK", "Start");
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

        // ── Jog Speed ────────────────────────────────────────────────────────────
        private async Task HandleSetJogSpeedAsync(double value)
        {
            try
            {
                EnsureConnected();
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

        // ── Emergency Stop ───────────────────────────────────────────────────────
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

        // ── Poll Timer ───────────────────────────────────────────────────────────
        private async void PlcPollTimer_Tick(object sender, EventArgs e)
        {
            if (isClosing || isPolling || plcComm == null || !plcComm.IsConnected) return;

            isPolling = true;
            try
            {
                var comm = plcComm;
                if (comm == null || !comm.IsConnected) { isPolling = false; return; }

                await System.Threading.Tasks.Task.Run(() =>
                {
                    if (isClosing) return;

                    // Read 4 axes
                    for (int i = 0; i < 4; i++)
                    {
                        try
                        {
                            if (isClosing) return;

                            // Position & speed from D registers
                            int dBase = i * 10;
                            int[] posData = comm.ReadDeviceRange($"D{dBase}", 2);
                            axCurrentPos[i] = (posData[1] << 16) | (posData[0] & 0xFFFF);

                            int[] speedData = comm.ReadDeviceRange($"D{dBase + 4}", 2);
                            axCurrentSpeed[i] = (speedData[1] << 16) | (speedData[0] & 0xFFFF);

                            // M code hiện tại do PLC ladder ghi vào D104, D114, D124, D134
                            // (axis 1 = D104, axis 2 = D114, ... — pattern +10 per axis giống position/speed)
                            try
                            {
                                int[] mcodeData = comm.ReadDeviceRange($"D{dBase + 104}", 1);
                                axMCode[i] = mcodeData[0];
                            }
                            catch { axMCode[i] = 0; }

                            // Error, warning, axis status from buffer memory
                            int[] mon = comm.ReadBuffer(0, MonitorBaseG[i], 38);
                            axErrorCode[i]     = mon[OffErrorCode];
                            axWarningCode[i]   = mon[OffWarningCode];
                            axAxisStatus[i]    = mon[OffAxisStatus];
                            axCurrentDataNo[i] = mon[35]; // Md.44
                            axLastDataNo[i]    = mon[37]; // Md.46

                            int[] ctl = comm.ReadBuffer(0, ControlBaseG[i], 20);
                            axErrorReset[i] = ctl[OffErrorReset];
                            axNewSpeed[i]   = (ctl[OffNewSpeed + 1] << 16) | (ctl[OffNewSpeed] & 0xFFFF);
                        }
                        catch { /* silently skip */ }
                    }

                    // Jog speed D406 (float)
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

                    // Monitor rows
                    foreach (var row in monitorRows)
                    {
                        try
                        {
                            if (isClosing) return;
                            row.Value  = comm.ReadDeviceValue(row.Register).ToString(CultureInfo.InvariantCulture);
                            row.Status = "OK";
                        }
                        catch (Exception ex) { row.Status = ex.Message; }
                    }
                });

                if (isClosing) return;
                UpdateIntegrityState(true);
                await PushControlStateAsync();
                if (currentView == "telemetry") await PushTelemetryStateAsync();
            }
            catch (Exception ex)
            {
                if (!isClosing) UpdateIntegrityFault(ex.Message);
            }
            finally
            {
                isPolling = false;
            }
        }

        // ── Shared helpers ───────────────────────────────────────────────────────
        private void EnsureConnected()
        {
            if (plcComm == null || !plcComm.IsConnected)
                throw new InvalidOperationException("PLC is not connected.");
        }

        private void UpdateConnectionState(bool connected, string bannerText)
            => connectionBanner = bannerText;

        private void UpdateIntegrityState(bool connected)
        {
            integrityState  = connected ? "READY" : "IDLE";
            integrityDetail = connected ? "RUN"   : "STOP";
            integrityTone   = connected ? "ready" : "idle";
        }

        private void UpdateIntegrityFault(string errorMessage)
        {
            integrityState  = "FAULT";
            integrityDetail = string.IsNullOrWhiteSpace(errorMessage) ? "PLC error" : errorMessage;
            integrityTone   = "fault";
        }

        private static string GetSequentialDevice(string baseDevice, int offset)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                baseDevice, @"^(?<prefix>[A-Za-z]+)(?<address>\d+)$");
            if (!match.Success)
                throw new InvalidOperationException("Invalid base device: " + baseDevice);

            string prefix  = match.Groups["prefix"].Value;
            int    address = int.Parse(match.Groups["address"].Value, CultureInfo.InvariantCulture);
            return prefix + (address + offset).ToString(CultureInfo.InvariantCulture);
        }
    }
}
