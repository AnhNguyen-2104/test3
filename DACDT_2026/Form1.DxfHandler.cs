using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — DXF / CAD handlers:
    ///   - Mở file DXF
    ///   - Assign point
    ///   - Import CAD sang process table
    ///   - Build connected paths
    ///   - Send positioning data to PLC
    ///   - Telemetry buffer read/write
    /// </summary>
    public partial class Form1
    {
        // ── Open DXF ────────────────────────────────────────────────────────────

        /// <summary>
        /// Hiển thị OpenFileDialog đồng bộ trên UI thread.
        /// Phải được gọi từ UI thread — không dùng async/await bên trong.
        /// </summary>
        private string ShowOpenFileDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter           = "CAD / G-code files (*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|DXF files (*.dxf)|*.dxf|G-code files (*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|All files (*.*)|*.*";
                dialog.Title            = "Open DXF or G-code file";
                dialog.CheckFileExists  = true;
                dialog.Multiselect      = false;
                dialog.RestoreDirectory = true;
                dialog.FileName         = string.Empty;

                string initialDir = activeCadDocument?.DirectoryPath;
                if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    dialog.InitialDirectory = initialDir;

                return dialog.ShowDialog(this) == DialogResult.OK
                    ? Path.GetFullPath(dialog.FileName)
                    : null;
            }
        }

        private async Task HandleOpenDxfAsync()
        {
            // Dialog phải chạy trên UI thread — gọi ShowOpenFileDialog() trực tiếp
            // vì CoreWebView2_WebMessageReceived đã chạy trên UI thread.
            // Nếu vì lý do nào đó không ở UI thread, marshal về UI thread trước.
            string selectedPath = null;
            try
            {
                if (this.InvokeRequired)
                {
                    // Marshal về UI thread và chờ kết quả
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
                    this.BeginInvoke(new Action(() =>
                    {
                        try { tcs.SetResult(ShowOpenFileDialog()); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    }));
                    selectedPath = await tcs.Task;
                }
                else
                {
                    selectedPath = ShowOpenFileDialog();
                }
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "DXF/G-code", "Lỗi mở dialog: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(selectedPath)) return;

            try
            {
                bool   isGcode    = IsGcodeFile(selectedPath);
                string sourceName = isGcode ? "GCODE" : "DXF";
                AddLogEntry(sourceName, selectedPath, "Read", "Selected", "OpenFileDialog");

                ClearLoadedFileState();

                // ── Parse file trên background thread để không block UI ──────────
                CadDocumentService.CadLoadResult loadedDoc = null;
                string loadedGcodeText = string.Empty;

                // Parse file trên thread với stack size lớn hơn (tránh StackOverflow với DXF phức tạp)
                var loadTask = new System.Threading.Tasks.TaskCompletionSource<bool>();
                var loadThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        if (isGcode)
                        {
                            loadedGcodeText = File.ReadAllText(selectedPath);
                            loadedDoc = gcodeCoordinateService.LoadAsCad(selectedPath);
                        }
                        else
                        {
                            loadedDoc = cadService.Load(selectedPath);
                        }

                        // Kết nối các đoạn thành path liên tục — chạy nền
                        if (loadedDoc?.Primitives != null && loadedDoc.Primitives.Count > 0)
                        {
                            var paths = GetConnectedPathsFromCad(loadedDoc.Primitives, isGcode);
                            loadedDoc.Primitives.Clear();
                            foreach (var path in paths)
                                loadedDoc.Primitives.AddRange(path);
                        }
                        loadTask.TrySetResult(true);
                    }
                    catch (StackOverflowException)
                    {
                        // Không thể catch StackOverflow trong .NET — thread sẽ bị kill
                        // Nhưng với 64MB stack, hầu hết file sẽ OK
                        loadTask.TrySetException(new Exception("File DXF quá phức tạp — vượt giới hạn bộ nhớ stack."));
                    }
                    catch (Exception ex)
                    {
                        loadTask.TrySetException(ex);
                    }
                }, 4 * 1024 * 1024); // 4MB stack
                loadThread.IsBackground = true;
                loadThread.Start();
                await loadTask.Task;

                // ── Cập nhật state trên UI thread ────────────────────────────────
                activeCadDocument    = loadedDoc;
                rawGcodeText         = loadedGcodeText;
                activeDocumentKind   = isGcode ? "GCODE" : "DXF";
                selectedCadPointKey  = activeCadDocument?.Points?.FirstOrDefault()?.Key;
                assignedPointKeys.Clear();

                if (isGcode)
                {
                    var firstSpeed = activeCadDocument?.Primitives?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Speed))?.Speed;
                    if (!string.IsNullOrEmpty(firstSpeed))
                        globalSpeed = firstSpeed;
                }

                currentView = "dxf";
                await PushDxfStateAsync();

                AddLogEntry(sourceName, activeCadDocument?.FilePath ?? selectedPath, "Read", "OK",
                    $"Loaded file: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");
                await NotifyAsync("success", sourceName,
                    $"Loaded: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");

                // Tự động Import và quét giới hạn
                await HandleImportCadToProcessAsync();
                await HandleScanLimitsAsync();
                await PushDxfStateAsync();
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "DXF/G-code", ex.Message);
            }
        }

        private void ClearLoadedFileState()
        {
            activeCadDocument = null;
            activeDocumentKind = string.Empty;
            selectedCadPointKey = null;
            assignedPointKeys.Clear();
            rawGcodeText = string.Empty;
            processRows.Clear();
            // Giải phóng bộ nhớ từ file trước
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private async Task HandlePreviewGcodeAsync(string text)
        {
            if (activeDocumentKind != "GCODE") return;

            try
            {
                rawGcodeText = text;
                string path = activeCadDocument?.FilePath;
                if (string.IsNullOrEmpty(path)) path = null;

                // Parse trên background thread
                CadDocumentService.CadLoadResult previewDoc = null;
                await Task.Run(() =>
                {
                    previewDoc = gcodeCoordinateService.LoadAsCadFromText(text, path);

                    if (previewDoc?.Primitives != null && previewDoc.Primitives.Count > 0)
                    {
                        var paths = GetConnectedPathsFromCad(previewDoc.Primitives, isGcode: true);
                        previewDoc.Primitives.Clear();
                        foreach (var pathList in paths)
                            previewDoc.Primitives.AddRange(pathList);
                    }
                });

                activeCadDocument = previewDoc;

                var firstSpeed = activeCadDocument?.Primitives?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Speed))?.Speed;
                if (!string.IsNullOrEmpty(firstSpeed))
                    globalSpeed = firstSpeed;

                await PushDxfStateAsync();
                await HandleImportCadToProcessAsync();
                await HandleScanLimitsAsync();
                await PushDxfStateAsync();
            }
            catch
            {
                // Ignore preview errors if typing incomplete
            }
        }

        private async Task HandleNewGcodeAsync()
        {
            ClearLoadedFileState();
            rawGcodeText     = string.Empty;
            activeDocumentKind = "GCODE";

            await Task.Run(() =>
            {
                activeCadDocument = gcodeCoordinateService.LoadAsCadFromText("");
            });

            await PushDxfStateAsync();
            await HandleImportCadToProcessAsync();
            await NotifyAsync("info", "G-code", "Đã tạo phiên bản G-code trống.");
        }

        private async Task HandleSaveGcodeAsync(string text)
        {
            if (activeDocumentKind != "GCODE" || activeCadDocument == null) return;

            try
            {
                string path = activeCadDocument.FilePath;
                if (string.IsNullOrEmpty(path) || path == "Untitled")
                {
                    // SaveFileDialog phải chạy trên UI thread
                    string selectedPath = null;

                    if (this.InvokeRequired)
                    {
                        this.Invoke(new Action(() =>
                        {
                            using (var sfd = new SaveFileDialog())
                            {
                                sfd.Filter   = "G-code files (*.gcode;*.nc;*.txt)|*.gcode;*.nc;*.txt|All files (*.*)|*.*";
                                sfd.Title    = "Save New G-code";
                                sfd.FileName = "New_GCode_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gcode";
                                if (sfd.ShowDialog(this) == DialogResult.OK)
                                    selectedPath = sfd.FileName;
                            }
                        }));
                    }
                    else
                    {
                        using (var sfd = new SaveFileDialog())
                        {
                            sfd.Filter   = "G-code files (*.gcode;*.nc;*.txt)|*.gcode;*.nc;*.txt|All files (*.*)|*.*";
                            sfd.Title    = "Save New G-code";
                            sfd.FileName = "New_GCode_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gcode";
                            if (sfd.ShowDialog(this) == DialogResult.OK)
                                selectedPath = sfd.FileName;
                        }
                    }

                    if (string.IsNullOrEmpty(selectedPath)) return;

                    path = selectedPath;
                    activeCadDocument.FilePath = path;
                    activeCadDocument.FileName = Path.GetFileName(path);
                }

                await Task.Run(() => File.WriteAllText(path, text));
                await HandlePreviewGcodeAsync(text);
                await NotifyAsync("success", "G-code", $"Lưu G-code thành công tại:\n{path}");
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "G-code", "Lỗi khi lưu G-code: " + ex.Message);
            }
        }

        private static bool IsGcodeFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return string.Equals(extension, ".gcode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".g", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".gc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".nc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ngc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cnc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tap", StringComparison.OrdinalIgnoreCase);
        }

        // ── Assign Point ─────────────────────────────────────────────────────────
        private async Task HandleAssignPointAsync(string slot, string pointKey)
        {
            if (string.IsNullOrEmpty(pointKey)) return;

            var point = activeCadDocument?.Points?
                .FirstOrDefault(p => string.Equals(p.Key, pointKey, StringComparison.OrdinalIgnoreCase));

            if (point == null)
            {
                await NotifyAsync("info", "DXF", "Please select a point before assigning.");
                return;
            }

            assignedPointKeys[slot] = point.Key;
            selectedCadPointKey    = point.Key;
            await PushDxfStateAsync();
        }

        // ── Process value (global speed / Z offsets) ─────────────────────────────
        private async Task HandleProcessValueAsync(string key, string value)
        {
            bool isZChange = false;

            if (string.Equals(key, "speed", StringComparison.OrdinalIgnoreCase))
            {
                globalSpeed = value;
                foreach (var row in processRows)
                    row.Speed = value;
            }
            else if (string.Equals(key, "dwell", StringComparison.OrdinalIgnoreCase))
            {
                // Không dùng nữa — dùng dwellM3/dwellM4 riêng
            }
            else if (string.Equals(key, "dwellM3", StringComparison.OrdinalIgnoreCase))
            {
                globalDwellM3 = value;
                foreach (var row in processRows)
                {
                    if (row.MCodeValue == "3")
                        row.Dwell = value;
                }
            }
            else if (string.Equals(key, "dwellM4", StringComparison.OrdinalIgnoreCase))
            {
                globalDwellM4 = value;
                foreach (var row in processRows)
                {
                    if (row.MCodeValue == "4")
                        row.Dwell = value;
                }
            }
            else if (string.Equals(key, "zDown", StringComparison.OrdinalIgnoreCase))
            {
                globalZDown = value;
                isZChange = true;
            }
            else if (string.Equals(key, "zSafe", StringComparison.OrdinalIgnoreCase))
            {
                globalZSafe = value;
                isZChange = true;
            }
            else if (string.Equals(key, "zStart", StringComparison.OrdinalIgnoreCase))
            {
                globalZStart = value;
                isZChange = true;
            }

            // Khi Z thay đổi cho file DXF → build lại process table để áp dụng quy tắc
            // Linear3 (3-axis) hay Line (2-axis) cho từng row, tránh lỗi nạp thiếu Axis 3.
            bool isDxfDoc = string.Equals(activeDocumentKind, "DXF", StringComparison.OrdinalIgnoreCase);
            if (isZChange && isDxfDoc && activeCadDocument?.Primitives != null && activeCadDocument.Primitives.Count > 0)
            {
                await HandleImportCadToProcessAsync();
            }

            UpdateGcodeFromProcessTable();
            await PushDxfStateAsync();
            SaveSettingsToFile();
            await NotifyAsync("success", "Configuration", $"Updated {key} = {value}");
        }

        // ── Edit a single process row field ─────────────────────────────────────
        private async Task HandleProcessRowValueAsync(int index, string field, string value)
        {
            if (index < 0 || index >= processRows.Count) return;
            var row = processRows[index];
            if (field == "mcode") row.MCodeValue = value;
            else if (field == "dwell") row.Dwell = value;
            else if (field == "speed") row.Speed = value;
            
            UpdateGcodeFromProcessTable();
            await PushDxfStateAsync();
        }

        private void UpdateGcodeFromProcessTable()
        {
            if (activeDocumentKind != "GCODE" || processRows == null || processRows.Count == 0) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; Generated from Process Table");
            sb.AppendLine("G90");
            sb.AppendLine("G21");

            double currentX = 0;
            double currentY = 0;

            foreach (var row in processRows)
            {
                if (string.IsNullOrEmpty(row.EndCoordinate)) continue;
                string[] endCoords = row.EndCoordinate.Split(';');
                if (endCoords.Length < 2) continue;

                double nextX = 0, nextY = 0;
                double.TryParse(endCoords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out nextX);
                double.TryParse(endCoords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out nextY);

                string g = "G1";
                if (row.MotionType.StartsWith("Arc CW")) g = "G2";
                else if (row.MotionType.StartsWith("Arc CCW")) g = "G3";
                else if (row.MotionType.StartsWith("Circle CW")) g = "G2";
                else if (row.MotionType.StartsWith("Circle CCW")) g = "G3";
                else if (row.MotionType.StartsWith("Circle")) g = "G2";
                else if (row.MotionType.Contains("Rapid") || row.MotionType.Contains("G0")) g = "G0";

                string f = !string.IsNullOrEmpty(row.Speed) ? $" F{row.Speed}" : "";

                if (!string.IsNullOrEmpty(row.MCodeValue))
                {
                    if (row.MCodeValue == "1") sb.AppendLine("M1");
                    else if (row.MCodeValue == "2") sb.AppendLine("M2");
                    else sb.AppendLine($"M{row.MCodeValue}");
                }

                if (g == "G1" || g == "G0")
                {
                    sb.AppendLine($"{g} X{nextX.ToString("0.###", CultureInfo.InvariantCulture)} Y{nextY.ToString("0.###", CultureInfo.InvariantCulture)}{f}");
                }
                else if (g == "G2" || g == "G3")
                {
                    if (!string.IsNullOrEmpty(row.CenterCoordinate))
                    {
                        string[] centers = row.CenterCoordinate.Split(';');
                        if (centers.Length >= 2)
                        {
                            double cx = 0, cy = 0;
                            double.TryParse(centers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out cx);
                            double.TryParse(centers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cy);
                            
                            double i = cx - currentX;
                            double j = cy - currentY;
                            
                            sb.AppendLine($"{g} X{nextX.ToString("0.###", CultureInfo.InvariantCulture)} Y{nextY.ToString("0.###", CultureInfo.InvariantCulture)} I{i.ToString("0.###", CultureInfo.InvariantCulture)} J{j.ToString("0.###", CultureInfo.InvariantCulture)}{f}");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{g} X{nextX.ToString("0.###", CultureInfo.InvariantCulture)} Y{nextY.ToString("0.###", CultureInfo.InvariantCulture)}{f}");
                    }
                }

                currentX = nextX;
                currentY = nextY;
            }

            rawGcodeText = sb.ToString();
        }

        // ── Import CAD → Process table ───────────────────────────────────────────
        private async Task HandleImportCadToProcessAsync()
        {
            if (activeCadDocument?.Primitives == null || activeCadDocument.Primitives.Count == 0)
            {
                await NotifyAsync("info", "DXF", "No CAD data available.");
                return;
            }

            // Snapshot các giá trị cần dùng trong background thread
            string glueStartCoord = null;
            string glueEndCoord   = null;
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

            bool isGcodeDoc  = string.Equals(activeDocumentKind, "GCODE", StringComparison.OrdinalIgnoreCase);
            string snapSpeed = globalSpeed;

            // Build rows trên background thread
            List<ProcessRow> rows = null;
            await Task.Run(() => { rows = BuildConnectedPathsFromCad(); });

            if (rows == null || rows.Count == 0)
            {
                await NotifyAsync("info", "DXF", "No valid paths found.");
                return;
            }

            foreach (var row in rows)
            {
                if (glueStartCoord != null && string.Equals(row.EndCoordinate, glueStartCoord))
                    row.MCodeValue = "1";
                if (glueEndCoord != null && string.Equals(row.EndCoordinate, glueEndCoord))
                    row.MCodeValue = "2";

                if (string.IsNullOrEmpty(row.Speed))
                {
                    // DXF: dùng globalSpeed làm fallback
                    // GCODE Rapid3: đã được gán rapidSpeed trong BuildConnectedPathsFromCad
                    // GCODE G1/G2/G3 không có F: không gán fallback — speed = 0 là hợp lệ
                    //   (PLC sẽ dùng speed từ lệnh trước theo modal của module)
                    if (!isGcodeDoc)
                        row.Speed = snapSpeed;
                }
            }

            processRows.Clear();
            processRows.AddRange(rows);

            // Không gọi PushDxfStateAsync ở đây — caller sẽ gọi sau khi cần
            await LogUIAsync("DXF", $"Compiled {rows.Count} movement commands into the process table.");
        }

        // ── Send CAD X axis data to PLC ──────────────────────────────────────────
        private async Task HandleSendCadXAsync()
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                await NotifyAsync("error", "Telemetry", "PLC is not connected.");
                return;
            }

            if (processRows.Count == 0)
            {
                await NotifyAsync("info", "Telemetry", "No points to send.");
                return;
            }

            // Map ProcessRow → QD75BufferWriter.PositioningDataRow (tọa độ đã cộng offset)
            var dataRows = new List<QD75BufferWriter.PositioningDataRow>();
            string lastSpeed = globalSpeed; // fallback to globalSpeed if no F at all
            string snapRapid = rapidSpeed;  // snapshot rapidSpeed cho G0

            // Xác định offset áp dụng:
            // - G-code: dùng WCS offset (G54-G59) per-row — mỗi row có WcsIndex riêng
            // - DXF: dùng offset X/Y từ Settings
            bool isGcodeFile = string.Equals(activeDocumentKind, "GCODE", StringComparison.OrdinalIgnoreCase);

            foreach (var row in processRows)
            {
                // Xác định offset cho row này
                double sendOffsetX, sendOffsetY;
                if (isGcodeFile)
                {
                    int wcsIdx = Math.Max(0, Math.Min(5, row.WcsIndex));
                    sendOffsetX = wcsOffsetX[wcsIdx];
                    sendOffsetY = wcsOffsetY[wcsIdx];
                }
                else
                {
                    sendOffsetX = offsetX;
                    sendOffsetY = offsetY;
                }

                // Xác định speed thực sự gửi xuống PLC
                string sendSpeed;
                bool rowIsRapid = row.MotionType.Contains("Rapid3") || row.MotionType.Contains("Rapid");

                if (rowIsRapid)
                {
                    // G0: luôn dùng rapidSpeed, không phụ thuộc vào F trong file
                    sendSpeed = snapRapid;
                }
                else
                {
                    // G1/G2/G3: dùng speed từ row (đã là modal F đúng), fallback globalSpeed
                    if (!string.IsNullOrEmpty(row.Speed) && int.TryParse(row.Speed, out int s) && s > 0)
                    {
                        lastSpeed = row.Speed;
                    }
                    sendSpeed = lastSpeed;
                }

                dataRows.Add(new QD75BufferWriter.PositioningDataRow
                {
                    MotionType       = row.MotionType,
                    MCodeValue       = row.MCodeValue,
                    Dwell            = row.Dwell,
                    Speed            = sendSpeed,
                    EndCoordinate    = ApplyOffsetToCoordSend(row.EndCoordinate,    sendOffsetX, sendOffsetY),
                    CenterCoordinate = ApplyOffsetToCoordSend(row.CenterCoordinate, sendOffsetX, sendOffsetY),
                    EndZ             = row.EndZ
                });
            }

            // ── Tạm dừng poll timer để tránh ContextSwitchDeadlock ──────────────────
            plcPollTimer.Stop();

            try
            {
                _ = SendProgressAsync(true, 0);

                // ── BƯỚC 0: Xóa toàn bộ buffer về 0 trước khi ghi dữ liệu mới ──────────
                await LogUIAsync("PLC", "Đang xóa buffer cũ...");
                var clearTask = Task.Run(() => QD75BufferWriter.ClearAllBuffers(plcComm, maxPoints: 600));
                var clearResult = await clearTask;

                foreach (var wr in clearResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Clear", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("warning", "Clear Buffer", $"{wr.Address}: {wr.Message}");
                }

                if (!clearResult.Success)
                {
                    await NotifyAsync("error", "Clear Buffer", "Không thể xóa buffer cũ. Tiếp tục ghi dữ liệu mới...");
                    // Không return — vẫn tiếp tục ghi dữ liệu mới
                }
                else
                {
                    await LogUIAsync("PLC", "Đã xóa buffer cũ thành công.");
                }

                // ── BƯỚC 1: Master axis (Axis 1 / X): bulk write toàn bộ buffer G2000+ ──
                // Chạy animation 0→45% song song với bulk write để progress bar mượt
                var axisXTask = Task.Run(() =>
                    QD75BufferWriter.WritePositioningDataBulk(plcComm, 0, dataRows, writeStartNo: false));

                await AnimateProgressAsync(from: 0, to: 45, durationMs: 800, completionTask: axisXTask);
                var sendResult = await axisXTask;

                foreach (var wr in sendResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Write", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("error", "Telemetry [Axis1]", $"{wr.Address}: {wr.Message}");
                }

                _ = SendProgressAsync(true, 50);

                if (!sendResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis1]", "Failed to load Axis 1 buffer.");
                    return;
                }

                // ── BƯỚC 2: Slave axis (Axis 2 / Y): bulk write G8000+ ──────────────────
                // Chạy animation 50→95% song song với bulk write
                var axisYTask = Task.Run(() =>
                    QD75BufferWriter.WriteSlaveAxisDataBulk(plcComm, dataRows, slaveBaseG: 8000));

                await AnimateProgressAsync(from: 50, to: 95, durationMs: 600, completionTask: axisYTask);
                var slaveResult = await axisYTask;

                foreach (var wr in slaveResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Write", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("error", "Telemetry [Axis2]", $"{wr.Address}: {wr.Message}");
                }

                _ = SendProgressAsync(true, 100);

                if (!slaveResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis2]", "Failed to load Axis 2 buffer.");
                    return;
                }

                bool hasZAxis = dataRows.Exists(r =>
                    r.MotionType != null &&
                    (r.MotionType.Contains("Linear3") || r.MotionType.Contains("Rapid3") || r.MotionType.Contains("Helical")));
                string axisInfo = hasZAxis
                    ? "Axis 1 (G2000+) & Axis 2 (G8000+) & Axis 3/Z (G14000+)"
                    : "Axis 1 (G2000+) & Axis 2 (G8000+)";
                await NotifyAsync("success", "PLC",
                    $"Đã nạp {sendResult.RowCount} dòng lệnh → {axisInfo}. Nhấn START ACTION để chạy.");
            }
            finally
            {
                _ = SendProgressAsync(false, 0);
                if (plcComm != null && plcComm.IsConnected && !isClosing)
                    plcPollTimer.Start();
            }
        }

        // ── Clear PLC Buffer ──────────────────────────────────────────────────────
        private async Task HandleClearBufferAsync()
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                await NotifyAsync("error", "Clear Buffer", "PLC is not connected.");
                return;
            }

            // Tạm dừng poll timer
            plcPollTimer.Stop();

            try
            {
                await LogUIAsync("Clear Buffer", "Đang xóa toàn bộ buffer PLC...");

                var clearTask = Task.Run(() => QD75BufferWriter.ClearAllBuffers(plcComm, maxPoints: 600));
                var clearResult = await clearTask;

                foreach (var wr in clearResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Clear", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("warning", "Clear Buffer", $"{wr.Address}: {wr.Message}");
                }

                if (clearResult.Success)
                {
                    await NotifyAsync("success", "Clear Buffer", 
                        "Đã xóa toàn bộ buffer PLC (Axis 1, 2, 3). Tất cả dữ liệu cũ đã bị xóa.");
                }
                else
                {
                    await NotifyAsync("error", "Clear Buffer", 
                        "Không thể xóa hoàn toàn buffer. Kiểm tra log để biết chi tiết.");
                }
            }
            catch (Exception ex)
            {
                await NotifyAsync("error", "Clear Buffer", $"Lỗi: {ex.Message}");
            }
            finally
            {
                _ = SendProgressAsync(false, 0);
                if (plcComm != null && plcComm.IsConnected && !isClosing)
                    plcPollTimer.Start();
            }
        }

        /// <summary>
        /// Animate progress bar từ <paramref name="from"/> đến <paramref name="to"/> trong
        /// <paramref name="durationMs"/> ms, nhưng dừng sớm nếu <paramref name="completionTask"/>
        /// hoàn thành trước. Đảm bảo không vượt quá <paramref name="to"/> khi task xong.
        /// </summary>
        private async Task AnimateProgressAsync(int from, int to, int durationMs, Task completionTask)
        {
            const int stepMs = 30; // ~33 fps
            int steps   = Math.Max(1, durationMs / stepMs);
            int range   = to - from;

            for (int i = 1; i <= steps; i++)
            {
                if (isClosing) return;
                if (completionTask.IsCompleted) break;

                // Easing: ease-out cubic — nhanh lúc đầu, chậm dần cuối
                double t   = (double)i / steps;
                double ease = 1.0 - Math.Pow(1.0 - t, 3);
                int pct = from + (int)(range * ease);

                _ = SendProgressAsync(true, pct);
                await Task.Delay(stepMs);
            }
        }

        // ── Build ProcessRow list from connected CAD paths ───────────────────────
        private List<ProcessRow> BuildConnectedPathsFromCad()
        {
            bool isGcodeDocument = string.Equals(activeDocumentKind, "GCODE", StringComparison.OrdinalIgnoreCase);
            
            if (isGcodeDocument)
                return BuildGcodeProcessRows();
            else
                return BuildDxfProcessRows();
        }

        // ── Build ProcessRow list for DXF files ──────────────────────────────────
        /// <summary>
        /// Build process rows cho file DXF.
        /// Quy tắc Z (giống GCODE):
        ///   - Z Start: độ cao Z khi bắt đầu một quỹ đạo (path) mới — trục Z chưa hạ
        ///   - Z Down : độ cao Z khi đang gia công (đầu phun đã hạ)
        ///   - Z Safe : độ cao Z an toàn khi nhấc lên giữa 2 path
        /// Quy tắc mã lệnh (giống GCODE):
        ///   - File có bất kỳ Z ≠ 0 → toàn bộ là Linear3 (3-axis), Arc convert thành chuỗi Linear3
        ///   - File không Z → Line/Arc 2-axis như bình thường
        ///   - Linear3/Rapid3 luôn Continuous Positioning (mã 5377/5376)
        ///   - Line liên tiếp cùng Da.2 → Continuous Path
        ///   - Dòng cuối chương trình → END
        /// </summary>
        private List<ProcessRow> BuildDxfProcessRows()
        {
            var result = new List<ProcessRow>();
            if (activeCadDocument?.Primitives == null) return result;

            // Parse globalZStart/Down/Safe
            double zStart = 0.0;
            double zDown  = 0.0;
            double zSafe  = 0.0;
            double.TryParse(globalZStart, NumberStyles.Float, CultureInfo.InvariantCulture, out zStart);
            double.TryParse(globalZDown,  NumberStyles.Float, CultureInfo.InvariantCulture, out zDown);
            double.TryParse(globalZSafe,  NumberStyles.Float, CultureInfo.InvariantCulture, out zSafe);

            bool hasZ = Math.Abs(zStart) > 1e-9 || Math.Abs(zDown) > 1e-9 || Math.Abs(zSafe) > 1e-9;
            // Chiến lược: nếu file có Z → toàn bộ file dùng Linear3 (3-axis), Arc cũng convert thành chuỗi Linear3.
            // Nếu không Z → dùng Line/Arc 2-axis như bình thường.
            // Lý do: tránh chuyển 2↔3 axis trong cùng chuỗi (Lỗi 524 theo manual SH-080058).

            var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives, isGcode: false);

            for (int pathIdx = 0; pathIdx < paths.Count; pathIdx++)
            {
                var path = paths[pathIdx];
                bool isLastPath = (pathIdx == paths.Count - 1);
                bool isFirstPath = (pathIdx == 0);

                for (int pIdx = 0; pIdx < path.Count; pIdx++)
                {
                    var prim = path[pIdx];
                    if (prim.Points == null || prim.Points.Count < 2) continue;

                    bool isLastInPath = (pIdx == path.Count - 1);
                    bool isFirstInPath = (pIdx == 0);

                    // suffix cho điểm cuối của primitive này trong path:
                    //   - Điểm cuối của path cuối cùng      → (End)                   [Da.1 = 00]
                    //   - Điểm cuối của path trung gian      → (Continuous Positioning) [Da.1 = 01] dừng có tăng/giảm tốc trước khi nhảy sang path kế
                    //   - Điểm giữa trong cùng một path      → (Continuous Path)        [Da.1 = 11] chạy không dừng
                    string suffix = isLastInPath
                        ? (isLastPath ? " (End)" : " (Continuous Positioning)")
                        : " (Continuous Path)";

                    // Điểm đầu tiên của path: di chuyển đến điểm start
                    // Theo quy tắc Z mới:
                    //   - Trục Z di chuyển ở độ cao zStart (chưa hạ đầu phun) khi đến điểm đầu path
                    //   - Đến điểm đầu path → tiếp theo sẽ hạ xuống zDown để gia công
                    if (isFirstInPath)
                    {
                        var startPt = prim.Points.First();
                        bool onlyPath = (paths.Count == 1);

                        string startSuffix = (isFirstPath && onlyPath)
                            ? " (Continuous Path)"
                            : " (Continuous Positioning)";

                        // Z điểm bắt đầu path = zStart (di chuyển trên cao trước khi hạ đầu phun)
                        double startZ = zStart;
                        string motionType = hasZ ? ("Linear3" + startSuffix) : ("Line" + startSuffix);
                        
                        var startRow = new ProcessRow
                        {
                            MotionType       = motionType,
                            EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", startPt.X, startPt.Y),
                            CenterCoordinate = string.Empty,
                            MCodeValue       = "3", // M3 = bắt đầu quỹ đạo (dispensing ON)
                            EndZ             = startZ
                        };
                        ApplyPrimitiveExtraData(startRow, prim, isGcode: false);
                        result.Add(startRow);
                    }

                    // Xử lý Line/Polyline
                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        for (int i = 1; i < prim.Points.Count; i++)
                        {
                            bool isLastInPrim = (i == prim.Points.Count - 1);
                            string currentSuffix = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";
                            var pt = prim.Points[i];

                            double endZ;
                            if (isLastInPrim && isLastInPath && !isLastPath)
                                endZ = zSafe;
                            else
                                endZ = zDown;

                            string motionPrefix = hasZ ? "Linear3" : "Line";

                            var row = new ProcessRow
                            {
                                MotionType       = motionPrefix + currentSuffix,
                                EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                    "{0:0.###};{1:0.###}", pt.X, pt.Y),
                                CenterCoordinate = string.Empty,
                                MCodeValue       = string.Empty,
                                EndZ             = endZ
                            };
                            ApplyPrimitiveExtraData(row, prim, isGcode: false);

                            // M4 = kết thúc quỹ đạo (dispensing OFF) tại điểm cuối path
                            if (isLastInPrim && isLastInPath)
                                row.MCodeValue = "4";

                            result.Add(row);
                        }
                    }
                    // Xử lý Arc/Circle
                    else if (prim.SourceType.Contains("Arc") || prim.SourceType.Contains("Circle"))
                    {
                        string arcType;
                        if (hasZ)
                            arcType = prim.IsCw ? "Helical CW" : "Helical CCW";
                        else
                        {
                            arcType = prim.IsCw ? "Arc CW" : "Arc CCW";
                            if (prim.SourceType.Contains("Circle")) arcType = "Circle";
                        }

                        var endPt = prim.Points.Last();
                        double endZ = (isLastInPath && !isLastPath) ? zSafe : zDown;

                        var row = new ProcessRow
                        {
                            MotionType    = arcType + suffix,
                            EndCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", endPt.X, endPt.Y),
                            MCodeValue    = string.Empty,
                            EndZ          = endZ
                        };
                        if (prim.Center != null)
                            row.CenterCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", prim.Center.X, prim.Center.Y);
                        ApplyPrimitiveExtraData(row, prim, isGcode: false);

                        // M4 = kết thúc quỹ đạo (dispensing OFF) tại điểm cuối path
                        if (isLastInPath)
                            row.MCodeValue = "4";

                        result.Add(row);
                    }
                }
            }

            // ── Post-process: đảm bảo mã lệnh chạy đúng (theo manual SH-080058) ──
            // Sau pre-scan, toàn bộ file đã thống nhất: hoặc Linear3 toàn bộ, hoặc Line/Arc 2-axis toàn bộ.
            // Chỉ cần xử lý:
            //   1. Dòng cuối → END
            //   2. 3-axis (Linear3 hoặc Helical) → Continuous Positioning (không Continuous Path)
            //   3. Chuyển Line↔Arc trong file 2-axis → Continuous Positioning
            for (int i = 0; i < result.Count; i++)
            {
                string mtLower = result[i].MotionType.ToLowerInvariant();
                // 3-axis bao gồm Linear3 và Helical (Arc 3-axis)
                bool curr3Axis = mtLower.Contains("linear3") || mtLower.Contains("helical");
                bool currIsArc = mtLower.Contains("arc") || mtLower.Contains("circle") || mtLower.Contains("helical");

                // Dòng cuối → END
                if (i == result.Count - 1)
                {
                    if (!result[i].MotionType.Contains("(End)") && !result[i].MotionType.Contains(" (End)"))
                    {
                        result[i].MotionType = result[i].MotionType
                            .Replace("(Continuous Path)", " (End)")
                            .Replace("(Continuous Positioning)", " (End)");
                    }
                    continue;
                }

                // 3-axis → Continuous Positioning (manual cấm Continuous Path cho Helical/Linear3 mixed)
                if (curr3Axis && result[i].MotionType.Contains("(Continuous Path)"))
                {
                    result[i].MotionType = result[i].MotionType
                        .Replace("(Continuous Path)", "(Continuous Positioning)");
                }

                // File 3-axis: chuyển Linear3 ↔ Helical → Continuous Positioning (tránh đổi Da.2 trong Cont.Path)
                if (curr3Axis)
                {
                    string nextLower = result[i + 1].MotionType.ToLowerInvariant();
                    bool nextIsHelical = nextLower.Contains("helical");
                    bool currIsHelical = mtLower.Contains("helical");
                    if (currIsHelical != nextIsHelical &&
                        result[i].MotionType.Contains("(Continuous Path)"))
                    {
                        result[i].MotionType = result[i].MotionType
                            .Replace("(Continuous Path)", "(Continuous Positioning)");
                    }
                }

                // Chuyển Line↔Arc trong file 2-axis → Continuous Positioning
                if (!curr3Axis)
                {
                    string nextLower = result[i + 1].MotionType.ToLowerInvariant();
                    bool nextIsArc   = nextLower.Contains("arc") || nextLower.Contains("circle");
                    if (currIsArc != nextIsArc &&
                        result[i].MotionType.Contains("(Continuous Path)"))
                    {
                        result[i].MotionType = result[i].MotionType
                            .Replace("(Continuous Path)", "(Continuous Positioning)");
                    }
                }
            }

            // ── Điểm có M code → Continuous Positioning + Dwell ──
            // M3 (bắt đầu dispensing) và M4 (kết thúc dispensing) có dwell riêng.
            string snapDwellM3 = globalDwellM3;
            string snapDwellM4 = globalDwellM4;
            for (int i = 0; i < result.Count; i++)
            {
                if (!string.IsNullOrEmpty(result[i].MCodeValue))
                {
                    // Đổi thành Continuous Positioning (dừng có tăng/giảm tốc)
                    if (result[i].MotionType.Contains("(Continuous Path)"))
                    {
                        result[i].MotionType = result[i].MotionType
                            .Replace("(Continuous Path)", "(Continuous Positioning)");
                    }
                    // Gán dwell riêng theo M code
                    if (string.IsNullOrEmpty(result[i].Dwell))
                    {
                        if (result[i].MCodeValue == "3")
                            result[i].Dwell = snapDwellM3;
                        else if (result[i].MCodeValue == "4")
                            result[i].Dwell = snapDwellM4;
                    }
                }
            }

            return result;
        }

        // ── Build ProcessRow list for GCODE files ────────────────────────────────
        /// <summary>
        /// Build process rows cho file GCODE.
        /// Logic phức tạp:
        ///   - G0 Rapid: dùng rapidSpeed, Linear3 (3-axis), Continuous Positioning
        ///   - G1 có Z: Linear3 (3-axis)
        ///   - G1 không Z: Linear2 (2-axis)
        ///   - Post-processing: đảm bảo Continuous Positioning đúng chỗ (trước/sau G0, chuyển 3↔2 axis)
        /// </summary>
        private List<ProcessRow> BuildGcodeProcessRows()
        {
            var result = new List<ProcessRow>();
            if (activeCadDocument?.Primitives == null) return result;

            // Snapshot rapidSpeed để dùng trong background thread
            string snapRapidSpeed = rapidSpeed;

            // ── Per-line Z detection ──
            // Mỗi lệnh G0/G1 tự quyết định 2-axis hay 3-axis dựa trên Z của chính lệnh đó:
            //   - Lệnh có Z (Z thay đổi so với điểm trước, hoặc primitive mang Z khác 0) → 3-axis
            //   - Lệnh không có Z → 2-axis (Line)
            //   - G02/G03: LUÔN 2-axis (Arc CW/CCW), bất kể có Z hay không
            // Khi chuyển 2↔3 axis sẽ được post-process thành Continuous Positioning.

            var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives, isGcode: true);

            for (int pathIdx = 0; pathIdx < paths.Count; pathIdx++)
            {
                var path        = paths[pathIdx];
                bool isLastPath = (pathIdx == paths.Count - 1);
                bool isFirstPath = (pathIdx == 0);
                bool pathClosed = IsClosedPath(path, isGcode: true);

                for (int pIdx = 0; pIdx < path.Count; pIdx++)
                {
                    var prim = path[pIdx];
                    if (prim.Points == null || prim.Points.Count < 2) continue;

                    bool isLastInPath = (pIdx == path.Count - 1);

                    // suffix cho điểm cuối của primitive này trong path:
                    //   - Điểm cuối của path cuối cùng      → (End)                   [Da.1 = 00]
                    //   - Điểm cuối của path trung gian      → (Continuous Positioning) [Da.1 = 01] dừng có tăng/giảm tốc trước khi nhảy sang path kế
                    //   - Điểm giữa trong cùng một path      → (Continuous Path)        [Da.1 = 11] chạy không dừng
                    string suffix = isLastInPath
                        ? (isLastPath ? " (End)" : " (Continuous Positioning)")
                        : " (Continuous Path)";

                    // Nếu đây là primitive đầu tiên trong path, tạo lệnh di chuyển đến điểm Start.
                    //
                    // ▶ Chế độ di chuyển "lên đầu path":
                    //   - Path đầu tiên (pathIdx=0) và chỉ có 1 path: Continuous Path (không cần dừng).
                    //   - Path đầu tiên (pathIdx=0) nhưng có nhiều path: Continuous Positioning
                    //     (dừng có tăng/giảm tốc tại điểm start trước khi bắt đầu gia công).
                    //   - Path có đoạn đứt quãng (pathIdx>0): bắt buộc Continuous Positioning
                    //     để PLC dừng, giảm tốc tại điểm start mới, tránh bị kéo qua đoạn không gia công.
                    //
                    // Quy tắc: chỉ dùng Continuous Path nếu đây là path đầu tiên VÀ chỉ có đúng 1 path.
                    // KHÔNG tạo start row — end points trong vòng lặp for(i=1...) đã đủ.
                    // Start row trước đây tạo dòng thừa (tọa độ start point) gây lệch buffer.

                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        bool primIsRapid = prim.SourceType.Contains("G0") || prim.SourceType.Contains("Rapid");

                        for (int i = 1; i < prim.Points.Count; i++)
                        {
                            bool   isLastInPrim  = (i == prim.Points.Count - 1);
                            string currentSuffix = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";
                            var    pt            = prim.Points[i];
                            var    prev          = prim.Points[i - 1];

                            // Tất cả G0/G1 đều dùng 2-axis (Line). Z bị bỏ qua.
                            string motionPrefix = "Line";

                            var row = new ProcessRow
                            {
                                MotionType       = motionPrefix + currentSuffix,
                                EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                    "{0:0.###};{1:0.###}", pt.X, pt.Y),
                                CenterCoordinate = string.Empty,
                                EndZ             = 0
                            };
                            ApplyPrimitiveExtraData(row, prim, isGcode: true);
                            if (primIsRapid && !string.IsNullOrEmpty(snapRapidSpeed))
                                row.Speed = snapRapidSpeed;
                            result.Add(row);
                        }
                    }
                    else if (prim.SourceType.Contains("Arc") || prim.SourceType.Contains("Circle"))
                    {
                        // G02/G03: LUÔN dùng Arc CW/CCW 2-axis (Da.2=0x0F/0x10).
                        // Nội suy cung tròn 2 trục X-Y với tâm cung. Z giữ nguyên.
                        string arcType = prim.IsCw ? "Arc CW" : "Arc CCW";
                        if (prim.SourceType.Contains("Circle")) arcType = "Circle";

                        var endPt = prim.Points.Last();
                        var row   = new ProcessRow
                        {
                            MotionType    = arcType + suffix,
                            EndCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", endPt.X, endPt.Y),
                            EndZ          = endPt.Z
                        };
                        if (prim.Center != null)
                            row.CenterCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", prim.Center.X, prim.Center.Y);
                        ApplyPrimitiveExtraData(row, prim, isGcode: true);
                        result.Add(row);
                    }
                }
            }

            // ── Post-process: đảm bảo mã lệnh chạy đúng (theo manual SH-080058) ────
            //
            // Tất cả G0/G1/G2/G3 đều là 2-axis. Không có 3-axis → không có lỗi 524.
            // Quy tắc:
            //   1. Dòng cuối chương trình → END (Da.1=0)
            //   2. Chuyển Da.2 (Line↔Arc) → Continuous Positioning (Da.1=1)
            //   3. Cùng Da.2 liên tiếp → Continuous Path (Da.1=3)

            for (int i = 0; i < result.Count; i++)
            {
                string mtLower = result[i].MotionType.ToLowerInvariant();

                // ── Quy tắc 1: Dòng cuối chương trình → END ──
                if (i == result.Count - 1)
                {
                    if (!result[i].MotionType.Contains("(End)") && !result[i].MotionType.Contains(" (End)"))
                    {
                        result[i].MotionType = result[i].MotionType
                            .Replace("(Continuous Path)", " (End)")
                            .Replace("(Continuous Positioning)", " (End)");
                    }
                    continue;
                }

                // ── Quy tắc 2: Chuyển Line↔Arc → Continuous Positioning ──
                string nextLower = result[i + 1].MotionType.ToLowerInvariant();
                bool currIsArc = mtLower.Contains("arc") || mtLower.Contains("circle");
                bool nextIsArc = nextLower.Contains("arc") || nextLower.Contains("circle");

                if (currIsArc != nextIsArc && result[i].MotionType.Contains("(Continuous Path)"))
                {
                    result[i].MotionType = result[i].MotionType
                        .Replace("(Continuous Path)", "(Continuous Positioning)");
                }
            }

            return result;
        }

        // ── Connect CAD primitives into chains ───────────────────────────────────
        /// <summary>
        /// Nối các primitive thành chuỗi path liên tiếp.
        /// Tối ưu O(n) bằng spatial hash dictionary thay vì O(n²) so sánh từng cặp.
        /// Với 5000 primitive: ~5000 lookup thay vì ~25 triệu phép so sánh.
        /// </summary>
        private List<List<CadDocumentService.CadPrimitiveData>> GetConnectedPathsFromCad(
            List<CadDocumentService.CadPrimitiveData> primitives, bool isGcode = false)
        {
            var paths = new List<List<CadDocumentService.CadPrimitiveData>>();
            if (primitives == null || primitives.Count == 0) return paths;

            // Spatial hash: round tọa độ về độ chính xác AreClose (epsilon 0.001) → key string
            // Một key = (xMm, yMm, zMm) làm tròn 3 chữ số thập phân
            string KeyOf(CadDocumentService.CadCoordinate p) => isGcode
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.000}|{1:0.000}|{2:0.000}", p.X, p.Y, p.Z)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.000}|{1:0.000}", p.X, p.Y);

            // Index endpoints: key → list of (primitive, isStartPoint)
            // Một key có thể có nhiều primitive trùng đầu/cuối → list
            var startMap = new Dictionary<string, List<int>>(primitives.Count);
            var endMap   = new Dictionary<string, List<int>>(primitives.Count);
            var assigned = new bool[primitives.Count];

            for (int i = 0; i < primitives.Count; i++)
            {
                var p = primitives[i];
                if (p.Points == null || p.Points.Count == 0) { assigned[i] = true; continue; }

                string sk = KeyOf(p.Points[0]);
                string ek = KeyOf(p.Points[p.Points.Count - 1]);

                if (!startMap.TryGetValue(sk, out var sList)) { sList = new List<int>(); startMap[sk] = sList; }
                sList.Add(i);

                if (!endMap.TryGetValue(ek, out var eList)) { eList = new List<int>(); endMap[ek] = eList; }
                eList.Add(i);
            }

            // Tìm primitive chưa assign tiếp theo bằng cách duyệt tuần tự
            int searchFrom = 0;
            while (true)
            {
                // Tìm primitive đầu chuỗi mới
                int seed = -1;
                for (int i = searchFrom; i < primitives.Count; i++)
                {
                    if (!assigned[i]) { seed = i; searchFrom = i + 1; break; }
                }
                if (seed < 0) break;

                var currentPath = new List<CadDocumentService.CadPrimitiveData>();
                currentPath.Add(primitives[seed]);
                assigned[seed] = true;

                // Mở rộng tail
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    var tail = currentPath[currentPath.Count - 1];
                    if (tail.Points == null || tail.Points.Count == 0) break;
                    string tailKey = KeyOf(tail.Points[tail.Points.Count - 1]);

                    // Tìm cand có start trùng tail
                    if (startMap.TryGetValue(tailKey, out var candStarts))
                    {
                        foreach (int ci in candStarts)
                        {
                            if (assigned[ci]) continue;
                            currentPath.Add(primitives[ci]);
                            assigned[ci] = true;
                            grew = true;
                            break;
                        }
                    }
                    if (grew) continue;

                    // Tìm cand có end trùng tail → reverse
                    if (endMap.TryGetValue(tailKey, out var candEnds))
                    {
                        foreach (int ci in candEnds)
                        {
                            if (assigned[ci]) continue;
                            var cand = primitives[ci];
                            cand.Points.Reverse();
                            if (cand.SourceType != null && cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Add(cand);
                            assigned[ci] = true;
                            grew = true;
                            break;
                        }
                    }
                }

                // Mở rộng head
                grew = true;
                while (grew)
                {
                    grew = false;
                    var head = currentPath[0];
                    if (head.Points == null || head.Points.Count == 0) break;
                    string headKey = KeyOf(head.Points[0]);

                    // Tìm cand có end trùng head
                    if (endMap.TryGetValue(headKey, out var candEnds))
                    {
                        foreach (int ci in candEnds)
                        {
                            if (assigned[ci]) continue;
                            currentPath.Insert(0, primitives[ci]);
                            assigned[ci] = true;
                            grew = true;
                            break;
                        }
                    }
                    if (grew) continue;

                    // Tìm cand có start trùng head → reverse
                    if (startMap.TryGetValue(headKey, out var candStarts))
                    {
                        foreach (int ci in candStarts)
                        {
                            if (assigned[ci]) continue;
                            var cand = primitives[ci];
                            cand.Points.Reverse();
                            if (cand.SourceType != null && cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Insert(0, cand);
                            assigned[ci] = true;
                            grew = true;
                            break;
                        }
                    }
                }

                paths.Add(currentPath);
            }

            return paths;
        }

        private bool IsClosedPath(List<CadDocumentService.CadPrimitiveData> path, bool isGcode = false)
        {
            if (path == null || path.Count == 0) return false;

            var first = path.FirstOrDefault(p => p.Points != null && p.Points.Count > 0);
            var last = path.LastOrDefault(p => p.Points != null && p.Points.Count > 0);
            if (first == null || last == null) return false;

            return AreClose(first.Points.First(), last.Points.Last(), isGcode);
        }

        private static void ApplyPrimitiveExtraData(ProcessRow row, CadDocumentService.CadPrimitiveData primitive, bool isGcode = false)
        {
            // M code áp dụng cho cả DXF và GCODE: gửi xuống PLC qua Da.10 (M code register).
            // Hỗ trợ M00/M02/M03/M04/M05/M06/M30... — PLC ladder sẽ xử lý từng mã.
            if (!string.IsNullOrWhiteSpace(primitive?.MCodeValue))
                row.MCodeValue = primitive.MCodeValue;
            if (!string.IsNullOrWhiteSpace(primitive?.Speed))
                row.Speed = primitive.Speed;
            if (!string.IsNullOrWhiteSpace(primitive?.Dwell))
                row.Dwell = primitive.Dwell;
            if (primitive != null)
                row.WcsIndex = primitive.WcsIndex;
        }

        /// <summary>
        /// So sánh 2 điểm có gần nhau không.
        /// - DXF (isGcode=false): chỉ check X,Y (Z luôn = 0)
        /// - GCODE (isGcode=true): check cả X,Y,Z
        /// </summary>
        private bool AreClose(CadDocumentService.CadCoordinate a, CadDocumentService.CadCoordinate b, bool isGcode = false)
        {
            bool xyClose = Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
            if (!isGcode) return xyClose;
            return xyClose && Math.Abs(a.Z - b.Z) < 0.001;
        }

        private static string FormatPoint(CadDocumentService.CadPointData point)
            => string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}", point.X, point.Y);

        // ── Telemetry buffer handlers ─────────────────────────────────────────────
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
            var wr = QD75BufferWriter.WriteBufferValue(plcComm, path, value);
            AddLogEntry(wr.Address, wr.Value, "Write", wr.Status, wr.Message);
            if (wr.Status.StartsWith("OK"))
                await NotifyAsync("success", "Telemetry", $"Successfully wrote to {path} using {wr.Message}.");
            else
                await NotifyAsync("error", "Telemetry", wr.Message);
        }

        // ── Process row initialization ────────────────────────────────────────────
        private void InitializeProcessRows()
        {
            processRows.Clear();
        }

        private ProcessRow GetProcessRow(string key)
            => processRows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase));

        // ── Scan Limits ──────────────────────────────────────────────────────────
        /// <summary>
        /// Quét toàn bộ toạ độ X/Y từ file DXF/G-code đang load,
        /// so sánh với giới hạn hành trình máy:
        ///   Trục 1 (X) = 170 mm, Trục 2 (Y) = 170 mm, Trục 3 (Z) = 50 mm.
        /// Kết quả push lên UI dưới dạng message "scanResult".
        /// </summary>
        private async Task HandleScanLimitsAsync()
        {
            const double LimitX = 170.0;   // Trục 1 – X
            const double LimitY = 170.0;   // Trục 2 – Y
            // LimitZ = 50.0 mm — chưa dùng trong scan hiện tại

            if (activeCadDocument == null)
            {
                await NotifyAsync("info", "Scan Limits", "Chưa load file DXF / G-code.");
                return;
            }

            // ── Thu thập tất cả điểm từ primitives ──────────────────────────────
            var allX = new List<double>();
            var allY = new List<double>();

            if (activeCadDocument.Primitives != null)
            {
                foreach (var prim in activeCadDocument.Primitives)
                {
                    if (prim.Points == null) continue;
                    foreach (var pt in prim.Points)
                    {
                        allX.Add(pt.X);
                        allY.Add(pt.Y);
                    }
                }
            }

            // Nếu primitives không có điểm thì lấy từ danh sách Points
            if (allX.Count == 0 && activeCadDocument.Points != null)
            {
                foreach (var pt in activeCadDocument.Points)
                {
                    allX.Add(pt.X);
                    allY.Add(pt.Y);
                }
            }

            if (allX.Count == 0)
            {
                await NotifyAsync("info", "Scan Limits", "Không tìm thấy toạ độ trong file.");
                return;
            }

            // ── Tính min / max thô (từ file) ────────────────────────────────────
            double rawMinX = allX.Min(),   rawMaxX = allX.Max();
            double rawMinY = allY.Min(),   rawMaxY = allY.Max();
            double rawRangeX = rawMaxX - rawMinX;
            double rawRangeY = rawMaxY - rawMinY;

            // ── Áp dụng offset → toạ độ máy ─────────────────────────────────────
            double adjMinX = rawMinX + offsetX,  adjMaxX = rawMaxX + offsetX;
            double adjMinY = rawMinY + offsetY,  adjMaxY = rawMaxY + offsetY;
            double adjRangeX = adjMaxX - adjMinX;
            double adjRangeY = adjMaxY - adjMinY;

            // ── Kiểm tra giới hạn ────────────────────────────────────────────────
            bool xUnder  = adjMinX < 0.0;
            bool xOver   = adjMaxX > LimitX;
            bool yUnder  = adjMinY < 0.0;
            bool yOver   = adjMaxY > LimitY;
            bool xExceed = xUnder || xOver;
            bool yExceed = yUnder || yOver;
            bool anyExceed = xExceed || yExceed;

            // Phần trăm dùng so với giới hạn (dựa trên range + vị trí)
            double xPct = Math.Min(100.0, adjRangeX / LimitX * 100.0);
            double yPct = Math.Min(100.0, adjRangeY / LimitY * 100.0);

            string summary = anyExceed
                ? $"VƯỢT GIỚI HẠN! X:[{adjMinX:0.###}→{adjMaxX:0.###}/{LimitX}mm]  Y:[{adjMinY:0.###}→{adjMaxY:0.###}/{LimitY}mm]"
                : $"TRONG GIỚI HẠN – X:[{adjMinX:0.###}→{adjMaxX:0.###}/{LimitX}mm]  Y:[{adjMinY:0.###}→{adjMaxY:0.###}/{LimitY}mm]";
            if (anyExceed)
                await NotifyAsync("error", "Scan Limits", summary);
            else
                await LogUIAsync("Scan Limits", summary);
        }

        /// <summary>
        /// Cộng offsetX / offsetY vào chuỗi toạ độ "X;Y" trước khi gửi PLC.
        /// Nếu chuỗi rỗng hoặc offset = 0 thì trả về giá trị gốc.
        /// </summary>
        private static string ApplyOffsetToCoordSend(string coord, double ox, double oy)
        {
            if (string.IsNullOrWhiteSpace(coord)) return coord ?? string.Empty;
            if (Math.Abs(ox) < 1e-9 && Math.Abs(oy) < 1e-9) return coord;

            string[] parts = coord.Split(';');
            if (parts.Length < 2) return coord;

            double x, y;
            if (!double.TryParse(parts[0].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x))
                return coord;
            if (!double.TryParse(parts[1].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y))
                return coord;

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###};{1:0.###}", x + ox, y + oy);
        }
    }
}
