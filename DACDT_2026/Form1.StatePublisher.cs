using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — State publishers: đẩy dữ liệu lên giao diện HTML/WebView2.
    /// Bao gồm: controlState, dxfState, telemetry, logs, notify, PostToUiAsync.
    /// </summary>
    public partial class Form1
    {
        // ── Push all states at once ──────────────────────────────────────────────
        private Task PushAllStateAsync()
            => Task.WhenAll(PushControlStateAsync(), PushDxfStateAsync(), PushTelemetryStateAsync(), PushLogsStateAsync());

        // ── Control / Axis state ─────────────────────────────────────────────────
        private static string FormatPositionMm(int rawValue) => QD75BufferWriter.FormatPositionMm(rawValue);
        private static string FormatSpeedMm(int rawValue)    => QD75BufferWriter.FormatSpeedMm(rawValue);
        private static string FormatAxisStatus(int status)   => QD75BufferWriter.FormatAxisStatus(status);

        private Task PushControlStateAsync()
        {
            bool   connected = plcComm != null && plcComm.IsConnected;
            string dash      = "--";

            var axesData = new object[4];
            for (int i = 0; i < 4; i++)
            {
                int mb = MonitorBaseG[i];
                int cb = ControlBaseG[i];

                int rawStatus = axAxisStatus[i];
                if (rawStatus > 32767) rawStatus -= 65536;

                axesData[i] = new
                {
                    index            = i + 1,
                    currentPos       = connected ? FormatPositionMm(axCurrentPos[i])  : dash,
                    currentPosAddr   = $"D{i * 10}",
                    currentSpeed     = connected ? FormatSpeedMm(axCurrentSpeed[i])   : dash,
                    currentSpeedAddr = $"D{i * 10 + 4}",
                    errorCode        = connected ? axErrorCode[i].ToString(CultureInfo.InvariantCulture) : dash,
                    errorCodeAddr    = $"U0\\G{mb + OffErrorCode}",
                    warningCode      = connected ? axWarningCode[i].ToString(CultureInfo.InvariantCulture) : dash,
                    warningCodeAddr  = $"U0\\G{mb + OffWarningCode}",
                    axisStatus       = connected ? FormatAxisStatus(rawStatus) : dash,
                    axisStatusAddr   = $"U0\\G{mb + OffAxisStatus}",
                    currentDataNo    = connected ? axCurrentDataNo[i].ToString(CultureInfo.InvariantCulture) : dash,
                    currentDataNoAddr= $"U0\\G{mb + 35}",
                    lastDataNo       = connected ? axLastDataNo[i].ToString(CultureInfo.InvariantCulture) : dash,
                    lastDataNoAddr   = $"U0\\G{mb + 37}",
                    errorReset       = connected ? axErrorReset[i].ToString(CultureInfo.InvariantCulture) : dash,
                    errorResetAddr   = $"U0\\G{cb + OffErrorReset}",
                    jogSpeed         = connected ? FormatSpeedMm(axJogSpeed[i]) : dash,
                    jogSpeedAddr     = "D406",
                    newSpeed         = connected ? axNewSpeed[i].ToString(CultureInfo.InvariantCulture) : dash,
                    newSpeedAddr     = $"U0\\G{cb + OffNewSpeed}"
                };
            }

            var payload = new
            {
                view   = currentView,
                theme  = currentTheme,
                connection = new
                {
                    connected,
                    station    = logicalStation,
                    banner     = connectionBanner,
                    meta       = $"MX Component logical station: {logicalStation}",
                    buttonText = connected ? "DISCONNECT PLC Q" : "CONNECT PLC Q"
                },
                axes           = axesData,
                jogSpeedD406   = currentJogSpeedD406,
                events         = new object[0]
            };

            return PostToUiAsync("controlState", payload);
        }

        // ── DXF / Process state ──────────────────────────────────────────────────
        private async Task PushDxfStateAsync()
        {
            // Snapshot tất cả dữ liệu cần thiết trên UI thread trước khi chuyển sang background
            var snapDoc        = activeCadDocument;
            var snapRows       = processRows.ToList();
            var snapKind       = activeDocumentKind;
            var snapView       = currentView;
            var snapTheme      = currentTheme;
            var snapSpeed      = globalSpeed;
            var snapOx         = offsetX;
            var snapOy         = offsetY;
            var snapPointKey   = selectedCadPointKey ?? string.Empty;
            var snapAssigned   = new System.Collections.Generic.Dictionary<string, string>(assignedPointKeys);
            var snapRawText    = snapKind == "GCODE" ? rawGcodeText : string.Empty;

            // Serialize toàn bộ payload trên background thread — không block UI
            string json = await Task.Run(() =>
            {
                string rawTrunc = snapRawText != null && snapRawText.Length > 200000
                    ? snapRawText.Substring(0, 200000) + "\n... [TRUNCATED FOR UI]"
                    : snapRawText ?? string.Empty;

                object boundsObj = snapDoc == null
                    ? (object)new { left = 0.0, top = 0.0, right = 100.0, bottom = 100.0, width = 100.0, height = 100.0, minZ = 0.0, maxZ = 0.0 }
                    : new
                    {
                        left   = snapDoc.Bounds.Left,
                        top    = snapDoc.Bounds.Top,
                        right  = snapDoc.Bounds.Right,
                        bottom = snapDoc.Bounds.Bottom,
                        width  = snapDoc.Bounds.Width,
                        height = snapDoc.Bounds.Height,
                        minZ   = snapDoc.Bounds.MinZ,
                        maxZ   = snapDoc.Bounds.MaxZ
                    };

                // Giới hạn primitives gửi lên UI: 2000 primitives, mỗi arc tối đa 12 điểm
                const int MaxPrimitives = 2000;
                const int MaxArcPoints  = 12; // đủ để vẽ mượt, không cần 36
                var primList = snapDoc == null
                    ? new System.Collections.Generic.List<object>()
                    : snapDoc.Primitives.Take(MaxPrimitives).Select(p =>
                    {
                        var pts = p.Points;
                        // Downsample arc points nếu quá nhiều
                        System.Collections.Generic.List<object> ptObjs;
                        if (pts.Count > MaxArcPoints)
                        {
                            ptObjs = new System.Collections.Generic.List<object>(MaxArcPoints + 1);
                            double step = (double)(pts.Count - 1) / (MaxArcPoints - 1);
                            for (int si = 0; si < MaxArcPoints; si++)
                            {
                                var pt = pts[(int)Math.Round(si * step)];
                                ptObjs.Add(new { x = Math.Round(pt.X, 3), y = Math.Round(pt.Y, 3), z = Math.Round(pt.Z, 3) });
                            }
                        }
                        else
                        {
                            ptObjs = pts.Select(pt => (object)new { x = Math.Round(pt.X, 3), y = Math.Round(pt.Y, 3), z = Math.Round(pt.Z, 3) }).ToList();
                        }
                        return (object)new
                        {
                            sourceType = p.SourceType,
                            points     = ptObjs,
                            center     = p.Center != null ? new { x = Math.Round(p.Center.X, 3), y = Math.Round(p.Center.Y, 3), z = Math.Round(p.Center.Z, 3) } : (object)null,
                            isCw       = p.IsCw,
                            isCircle   = p.IsCircle
                        };
                    }).ToList();

                // Giới hạn points: 2000
                var ptList = snapDoc == null
                    ? new System.Collections.Generic.List<object>()
                    : snapDoc.Points.Take(2000).Select(pt => (object)new
                    {
                        index    = pt.Index,
                        lineType = pt.LineType,
                        x        = Math.Round(pt.X, 3),
                        y        = Math.Round(pt.Y, 3),
                        z        = Math.Round(pt.Z, 3),
                        xDisplay = pt.XDisplay,
                        yDisplay = pt.YDisplay,
                        zDisplay = pt.ZDisplay,
                        key      = pt.Key
                    }).ToList();

                // Giới hạn processRows: 2000, tính offset sẵn
                var rowList = snapRows.Take(2000).Select(row =>
                {
                    string endWithOffset    = ApplyOffsetToCoord(row.EndCoordinate,    snapOx, snapOy);
                    string centerWithOffset = ApplyOffsetToCoord(row.CenterCoordinate, snapOx, snapOy);
                    return (object)new
                    {
                        key              = row.Key,
                        motionType       = row.MotionType,
                        mCodeValue       = row.MCodeValue       ?? string.Empty,
                        dwell            = row.Dwell            ?? string.Empty,
                        speed            = row.Speed            ?? string.Empty,
                        endCoordinate    = row.EndCoordinate    ?? string.Empty,
                        centerCoordinate = row.CenterCoordinate ?? string.Empty,
                        endCoordinateDisplay    = endWithOffset,
                        centerCoordinateDisplay = centerWithOffset
                    };
                }).ToList();

                var payload = new
                {
                    view     = snapView,
                    theme    = snapTheme,
                    fileKind = snapKind,
                    filePath = snapDoc?.FilePath ?? string.Empty,
                    fileName = snapDoc?.FileName ?? string.Empty,
                    rawText  = rawTrunc,
                    globalSpeed = snapSpeed,
                    offsetX  = snapOx,
                    offsetY  = snapOy,
                    bounds   = boundsObj,
                    primitives       = primList,
                    points           = ptList,
                    selectedPointKey = snapPointKey,
                    assignedPointKeys = snapAssigned,
                    processRows      = rowList
                };

                return serializer.Serialize(new { type = "dxfState", payload });
            });

            // Gửi JSON đã serialize lên WebView2 trên UI thread
            if (isClosing || !webReady) return;
            Action post = () =>
            {
                try
                {
                    if (!isClosing && webReady && webView != null && !webView.IsDisposed && webView.CoreWebView2 != null)
                        webView.CoreWebView2.PostWebMessageAsJson(json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("PushDxfStateAsync post error: " + ex.Message);
                }
            };
            if (webView.InvokeRequired) webView.BeginInvoke(post);
            else post();
        }

        /// <summary>
        /// Cộng offsetX / offsetY vào chuỗi toạ độ định dạng "X;Y".
        /// Trả về chuỗi gốc nếu không parse được.
        /// </summary>
        private static string ApplyOffsetToCoord(string coord, double ox, double oy)
        {
            if (string.IsNullOrWhiteSpace(coord)) return string.Empty;

            string[] parts = coord.Split(';');
            if (parts.Length < 2) return coord;

            double x, y;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x))
                return coord;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y))
                return coord;

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###};{1:0.###}", x + ox, y + oy);
        }

        // ── Telemetry state ──────────────────────────────────────────────────────
        private Task PushTelemetryStateAsync()
        {
            bool connected = plcComm != null && plcComm.IsConnected;
            var  dValues   = new List<object>();
            var  buffers   = new List<object>();

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
                    dValues.Add(new { register = reg, value = (int?)null, ok = false, error = "Disconnected" });
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
                    buffers.Add(new { path = buf.Path, values = new int[0], ok = false, error = "Disconnected" });
            }

            return PostToUiAsync("telemetry", new { view = currentView, theme = currentTheme, connected, dValues, buffers });
        }

        // ── Log state ────────────────────────────────────────────────────────────
        private Task PushLogsStateAsync()
        {
            var outLogs = logs.Select(l => new
            {
                timestamp = l.Timestamp.ToString("o"),
                direction = l.Direction,
                address   = l.Address,
                value     = l.Value,
                status    = l.Status,
                message   = l.Message
            }).ToList();

            return PostToUiAsync("logsState", new { view = currentView, theme = currentTheme, logs = outLogs });
        }

        // ── Notify ───────────────────────────────────────────────────────────────
        protected Task NotifyAsync(string kind, string title, string message)
            => PostToUiAsync("notify", new { kind, title, message });

        protected Task LogUIAsync(string title, string message)
            => PostToUiAsync("log", new { title, message });

        protected Task SendProgressAsync(bool visible, int percent = 0)
            => PostToUiAsync("progress", new { visible, percent });

        // ── Log helpers ───────────────────────────────────────────────────────────
        private void AddLogEntry(string address, string value,
            string direction = "Write", string status = "OK", string message = null)
        {
            try
            {
                logs.Insert(0, new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Direction = direction,
                    Address   = address,
                    Value     = value,
                    Status    = status,
                    Message   = message
                });

                if (logs.Count > 500) logs.RemoveRange(500, logs.Count - 500);
                _ = PushLogsStateAsync();
            }
            catch { /* ignore logging errors */ }
        }

        private Task HandleClearLogsAsync()
        {
            logs.Clear();
            return PushLogsStateAsync();
        }

        // ── PostToUiAsync ─────────────────────────────────────────────────────────
        private Task PostToUiAsync(string type, object payload)
        {
            if (isClosing || !webReady) return Task.CompletedTask;

            try
            {
                // Phải chạy trên UI thread — dùng BeginInvoke nếu đang ở thread khác
                if (webView == null || webView.IsDisposed) return Task.CompletedTask;

                Action post = () =>
                {
                    try
                    {
                        if (isClosing || webView == null || webView.IsDisposed || webView.CoreWebView2 == null)
                            return;
                        string json = serializer.Serialize(new { type, payload });
                        webView.CoreWebView2.PostWebMessageAsJson(json);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("PostToUiAsync (inner) error: " + ex.Message);
                    }
                };

                if (webView.InvokeRequired)
                    webView.BeginInvoke(post);
                else
                    post();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("PostToUiAsync error: " + ex.Message);
            }

            return Task.CompletedTask;
        }

        // ── JSON message helper getters ───────────────────────────────────────────
        private static Dictionary<string, object> GetMap(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value))
                return new Dictionary<string, object>();
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static string GetString(Dictionary<string, object> source, string key, string fallback = "")
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
                return fallback;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }

        private static int GetInt(Dictionary<string, object> source, string key, int fallback = 0)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            if (value is int)    return (int)value;
            if (value is long)   return Convert.ToInt32((long)value, CultureInfo.InvariantCulture);
            if (value is double) return Convert.ToInt32((double)value, CultureInfo.InvariantCulture);
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed : fallback;
        }

        private static double GetDouble(Dictionary<string, object> source, string key, double fallback = 0.0)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }
}
