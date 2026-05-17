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
        private async Task HandleOpenDxfAsync()
        {
            // ShowDialog phải chạy trên UI thread
            // CoreWebView2_WebMessageReceived đã chạy trên UI thread nên không cần Invoke
            // Nhưng nếu vì lý do nào đó bị gọi từ thread khác thì dùng BeginInvoke
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(async () => await HandleOpenDxfAsync()));
                return;
            }

            string selectedPath = null;
            string initialDir   = activeCadDocument?.DirectoryPath;

            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter           = "CAD / G-code files (*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|DXF files (*.dxf)|*.dxf|G-code files (*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|All files (*.*)|*.*";
                dialog.Title            = "Open DXF or G-code file";
                dialog.CheckFileExists  = true;
                dialog.Multiselect      = false;
                dialog.RestoreDirectory = true;
                dialog.FileName         = string.Empty;

                if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    dialog.InitialDirectory = initialDir;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                    selectedPath = Path.GetFullPath(dialog.FileName);
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

                await Task.Run(() =>
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

                    // Kết nối các đoạn thành path liên tục (O(n²)) — chạy nền
                    if (loadedDoc?.Primitives != null && loadedDoc.Primitives.Count > 0)
                    {
                        var paths = GetConnectedPathsFromCad(loadedDoc.Primitives);
                        loadedDoc.Primitives.Clear();
                        foreach (var path in paths)
                            loadedDoc.Primitives.AddRange(path);
                    }
                });

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
                        var paths = GetConnectedPathsFromCad(previewDoc.Primitives);
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
            if (string.Equals(key, "speed", StringComparison.OrdinalIgnoreCase))
            {
                globalSpeed = value;
                foreach (var row in processRows)
                    row.Speed = value;
            }
            else if (string.Equals(key, "zDown", StringComparison.OrdinalIgnoreCase))
                globalZDown = value;
            else if (string.Equals(key, "zSafe", StringComparison.OrdinalIgnoreCase))
                globalZSafe = value;

            UpdateGcodeFromProcessTable();
            await PushDxfStateAsync();
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

            foreach (var row in processRows)
            {
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
                    EndCoordinate    = ApplyOffsetToCoordSend(row.EndCoordinate,    offsetX, offsetY),
                    CenterCoordinate = ApplyOffsetToCoordSend(row.CenterCoordinate, offsetX, offsetY),
                    EndZ             = row.EndZ
                });
            }

            // ── Tạm dừng poll timer để tránh ContextSwitchDeadlock ──────────────────
            plcPollTimer.Stop();

            try
            {
                _ = SendProgressAsync(true, 0);

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

                bool hasZAxis = dataRows.Exists(r => Math.Abs(r.EndZ) > 1e-9);
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
            var result = new List<ProcessRow>();
            if (activeCadDocument?.Primitives == null) return result;

            // Snapshot rapidSpeed để dùng trong background thread
            string snapRapidSpeed = rapidSpeed;

            var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);
            bool isGcodeDocument = string.Equals(activeDocumentKind, "GCODE", StringComparison.OrdinalIgnoreCase);

            for (int pathIdx = 0; pathIdx < paths.Count; pathIdx++)
            {
                var path        = paths[pathIdx];
                bool isLastPath = (pathIdx == paths.Count - 1);
                bool isFirstPath = (pathIdx == 0);
                bool pathClosed = IsClosedPath(path);

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
                    if (pIdx == 0)
                    {
                        var startPt   = prim.Points.First();
                        bool onlyPath = (paths.Count == 1);
                        double startZ = isGcodeDocument ? startPt.Z : 0.0;

                        // G0 Rapid luôn dùng "Rapid3" → Linear3 (Da.2=0x15), tốc độ rapidSpeed
                        bool startIsRapid = isGcodeDocument &&
                            (prim.SourceType.Contains("G0") || prim.SourceType.Contains("Rapid"));

                        string startMotion = startIsRapid
                            ? "Rapid3 (Continuous Positioning)"
                            : ((isFirstPath && onlyPath)
                                ? "Line (Continuous Path)"
                                : "Line (Continuous Positioning)");

                        var startRow = new ProcessRow
                        {
                            MotionType       = startMotion,
                            EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", startPt.X, startPt.Y),
                            CenterCoordinate = string.Empty,
                            MCodeValue       = (!isGcodeDocument && pathClosed) ? "1" : string.Empty,
                            EndZ             = startZ
                        };
                        ApplyPrimitiveExtraData(startRow, prim, isGcodeDocument);
                        if (startIsRapid && !string.IsNullOrEmpty(snapRapidSpeed))
                            startRow.Speed = snapRapidSpeed;
                        result.Add(startRow);
                    }

                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        bool primIsRapid = isGcodeDocument &&
                            (prim.SourceType.Contains("G0") || prim.SourceType.Contains("Rapid"));

                        for (int i = 1; i < prim.Points.Count; i++)
                        {
                            bool   isLastInPrim  = (i == prim.Points.Count - 1);
                            string currentSuffix = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";
                            var    pt            = prim.Points[i];
                            double endZ          = isGcodeDocument ? pt.Z : 0.0;

                            // G0 Rapid: prefix "Rapid3" → Linear3 (Da.2=0x15)
                            string motionPrefix = primIsRapid ? "Rapid3" : "Line";

                            var row = new ProcessRow
                            {
                                MotionType       = motionPrefix + currentSuffix,
                                EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                    "{0:0.###};{1:0.###}", pt.X, pt.Y),
                                CenterCoordinate = string.Empty,
                                EndZ             = endZ
                            };
                            ApplyPrimitiveExtraData(row, prim, isGcodeDocument);
                            // G0: luôn dùng rapidSpeed, bỏ qua F từ file
                            if (primIsRapid && !string.IsNullOrEmpty(snapRapidSpeed))
                                row.Speed = snapRapidSpeed;
                            if (!isGcodeDocument && pathClosed && isLastInPath && isLastInPrim)
                                row.MCodeValue = "2";
                            result.Add(row);
                        }
                    }
                    else if (prim.SourceType.Contains("Arc") || prim.SourceType.Contains("Circle"))
                    {
                        string arcType = prim.IsCw ? "Arc CW" : "Arc CCW";
                        if (prim.SourceType.Contains("Circle")) arcType = "Circle";

                        var endPt = prim.Points.Last();
                        var row   = new ProcessRow
                        {
                            MotionType    = arcType + suffix,
                            EndCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", endPt.X, endPt.Y)
                        };
                        if (prim.Center != null)
                            row.CenterCoordinate = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", prim.Center.X, prim.Center.Y);
                        ApplyPrimitiveExtraData(row, prim, isGcodeDocument);
                        if (!isGcodeDocument && pathClosed && isLastInPath)
                            row.MCodeValue = "2";
                        result.Add(row);
                    }
                }
            }

            return result;
        }

        // ── Connect CAD primitives into chains ───────────────────────────────────
        private List<List<CadDocumentService.CadPrimitiveData>> GetConnectedPathsFromCad(
            List<CadDocumentService.CadPrimitiveData> primitives)
        {
            var unassigned = new List<CadDocumentService.CadPrimitiveData>(primitives);
            var paths      = new List<List<CadDocumentService.CadPrimitiveData>>();

            while (unassigned.Count > 0)
            {
                var currentPath = new List<CadDocumentService.CadPrimitiveData>();
                var current     = unassigned[0];
                unassigned.RemoveAt(0);
                currentPath.Add(current);

                bool added = true;
                while (added)
                {
                    added = false;
                    var tailPrim = currentPath.Last();
                    var headPrim = currentPath.First();

                    if (tailPrim.Points == null || tailPrim.Points.Count == 0
                        || headPrim.Points == null || headPrim.Points.Count == 0) break;

                    var tailPt = tailPrim.Points.Last();
                    var headPt = headPrim.Points.First();

                    for (int i = 0; i < unassigned.Count; i++)
                    {
                        var cand = unassigned[i];
                        if (cand.Points == null || cand.Points.Count == 0) continue;

                        var candStart = cand.Points.First();
                        var candEnd   = cand.Points.Last();

                        if (AreClose(tailPt, candStart))
                        {
                            currentPath.Add(cand); unassigned.RemoveAt(i); added = true; break;
                        }
                        else if (AreClose(tailPt, candEnd))
                        {
                            cand.Points.Reverse();
                            if (cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Add(cand); unassigned.RemoveAt(i); added = true; break;
                        }
                        else if (AreClose(headPt, candEnd))
                        {
                            currentPath.Insert(0, cand); unassigned.RemoveAt(i); added = true; break;
                        }
                        else if (AreClose(headPt, candStart))
                        {
                            cand.Points.Reverse();
                            if (cand.SourceType.Contains("Arc")) cand.IsCw = !cand.IsCw;
                            currentPath.Insert(0, cand); unassigned.RemoveAt(i); added = true; break;
                        }
                    }
                }

                paths.Add(currentPath);
            }

            return paths;
        }

        private bool IsClosedPath(List<CadDocumentService.CadPrimitiveData> path)
        {
            if (path == null || path.Count == 0) return false;

            var first = path.FirstOrDefault(p => p.Points != null && p.Points.Count > 0);
            var last = path.LastOrDefault(p => p.Points != null && p.Points.Count > 0);
            if (first == null || last == null) return false;

            return AreClose(first.Points.First(), last.Points.Last());
        }

        private static void ApplyPrimitiveExtraData(ProcessRow row, CadDocumentService.CadPrimitiveData primitive, bool isGcode = false)
        {
            // M code từ G-code file không gửi xuống PLC — chỉ áp dụng cho DXF
            if (!isGcode && !string.IsNullOrWhiteSpace(primitive?.MCodeValue))
                row.MCodeValue = primitive.MCodeValue;
            if (!string.IsNullOrWhiteSpace(primitive?.Speed))
                row.Speed = primitive.Speed;
            if (!string.IsNullOrWhiteSpace(primitive?.Dwell))
                row.Dwell = primitive.Dwell;
        }

        private bool AreClose(CadDocumentService.CadCoordinate a, CadDocumentService.CadCoordinate b)
            => Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;

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
            processRows.Add(new ProcessRow { Key = "start",      MotionType = "Điểm bắt đầu" });
            processRows.Add(new ProcessRow { Key = "glueStart",  MotionType = "Điểm bắt đầu bơm", MCodeValue = "Bật keo" });
            processRows.Add(new ProcessRow { Key = "glueEnd",    MotionType = "Điểm kết thúc bơm", MCodeValue = "Tắt keo" });
            processRows.Add(new ProcessRow { Key = "zDown",      MotionType = "Độ cao Z hạ" });
            processRows.Add(new ProcessRow { Key = "zSafe",      MotionType = "Độ cao Z an toàn" });
            processRows.Add(new ProcessRow { Key = "speed",      MotionType = "Tốc độ" });
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
