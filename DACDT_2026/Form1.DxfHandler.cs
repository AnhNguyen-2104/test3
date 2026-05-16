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
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter        = "CAD / G-code files (*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.dxf;*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|DXF files (*.dxf)|*.dxf|G-code files (*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap)|*.gcode;*.g;*.gc;*.nc;*.ngc;*.cnc;*.tap|All files (*.*)|*.*";
                dialog.Title         = "Open DXF or G-code file";
                dialog.CheckFileExists = true;
                dialog.Multiselect   = false;
                dialog.RestoreDirectory = true;
                dialog.FileName      = string.Empty;

                if (!string.IsNullOrWhiteSpace(activeCadDocument?.DirectoryPath)
                    && Directory.Exists(activeCadDocument.DirectoryPath))
                    dialog.InitialDirectory = activeCadDocument.DirectoryPath;

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string selectedPath = Path.GetFullPath(dialog.FileName);
                    bool isGcode = IsGcodeFile(selectedPath);
                    string sourceName = isGcode ? "GCODE" : "DXF";
                    AddLogEntry(sourceName, selectedPath, "Read", "Selected", "OpenFileDialog");

                    ClearLoadedFileState();

                    if (isGcode)
                    {
                        rawGcodeText = File.ReadAllText(selectedPath);
                        LoadGcodeCoordinatesAsCad(selectedPath);
                    }
                    else
                    {
                        rawGcodeText = string.Empty;
                        LoadCadDocument(selectedPath);
                    }

                    if (activeCadDocument?.Primitives != null)
                    {
                        var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);
                        activeCadDocument.Primitives.Clear();
                        foreach (var path in paths)
                            activeCadDocument.Primitives.AddRange(path);
                    }

                    currentView = "dxf";
                    await PushDxfStateAsync();
                    AddLogEntry(sourceName, activeCadDocument?.FilePath ?? selectedPath, "Read", "OK",
                        $"Loaded file: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");
                    await NotifyAsync("success", sourceName,
                        $"Loaded: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");

                    // Bỏ qua nhấn Import: Tự động chạy Import ngay sau khi load xong DXF
                    await HandleImportCadToProcessAsync();
                    
                    // Tự động quét toạ độ kiểm tra hành trình
                    await HandleScanLimitsAsync();
                }
                catch (Exception ex)
                {
                    await NotifyAsync("error", "DXF/G-code", ex.Message);
                }
            }
        }

        private void LoadCadDocument(string filePath)
        {
            activeCadDocument = cadService.Load(filePath);
            activeDocumentKind = "DXF";
            selectedCadPointKey = activeCadDocument.Points.FirstOrDefault()?.Key;
            assignedPointKeys.Clear();
        }

        private void LoadGcodeCoordinatesAsCad(string filePath)
        {
            activeCadDocument = gcodeCoordinateService.LoadAsCad(filePath);
            activeDocumentKind = "GCODE";
            selectedCadPointKey = activeCadDocument.Points.FirstOrDefault()?.Key;
            assignedPointKeys.Clear();

            var firstSpeed = activeCadDocument.Primitives?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Speed))?.Speed;
            if (!string.IsNullOrEmpty(firstSpeed))
                globalSpeed = firstSpeed;
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
            if (activeDocumentKind == "GCODE")
            {
                try
                {
                    rawGcodeText = text;
                    string path = activeCadDocument?.FilePath;
                    if (string.IsNullOrEmpty(path)) path = null;
                    activeCadDocument = gcodeCoordinateService.LoadAsCadFromText(text, path);
                    
                    var firstSpeed = activeCadDocument.Primitives?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Speed))?.Speed;
                    if (!string.IsNullOrEmpty(firstSpeed))
                        globalSpeed = firstSpeed;

                    if (activeCadDocument?.Primitives != null)
                    {
                        var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);
                        activeCadDocument.Primitives.Clear();
                        foreach (var pathList in paths)
                            activeCadDocument.Primitives.AddRange(pathList);
                    }

                    await PushDxfStateAsync();
                    await HandleImportCadToProcessAsync();
                    await HandleScanLimitsAsync();
                }
                catch(Exception ex)
                {
                    // Ignore preview errors if typing incomplete
                }
            }
        }

        private async Task HandleNewGcodeAsync()
        {
            ClearLoadedFileState();
            rawGcodeText = string.Empty;
            activeDocumentKind = "GCODE";
            activeCadDocument = gcodeCoordinateService.LoadAsCadFromText("");
            
            await PushDxfStateAsync();
            await HandleImportCadToProcessAsync();
            await NotifyAsync("info", "G-code", "Đã tạo phiên bản G-code trống.");
        }

        private async Task HandleSaveGcodeAsync(string text)
        {
            if (activeDocumentKind == "GCODE" && activeCadDocument != null)
            {
                try
                {
                    string path = activeCadDocument.FilePath;
                    if (string.IsNullOrEmpty(path) || path == "Untitled")
                    {
                        string selectedPath = null;
                        this.Invoke(new Action(() =>
                        {
                            using (var sfd = new SaveFileDialog())
                            {
                                sfd.Filter = "G-code files (*.gcode;*.nc;*.txt)|*.gcode;*.nc;*.txt|All files (*.*)|*.*";
                                sfd.Title = "Save New G-code";
                                sfd.FileName = "New_GCode_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".gcode";
                                if (sfd.ShowDialog() == DialogResult.OK)
                                {
                                    selectedPath = sfd.FileName;
                                }
                            }
                        }));

                        if (string.IsNullOrEmpty(selectedPath))
                            return; // User cancelled

                        path = selectedPath;
                        activeCadDocument.FilePath = path;
                        activeCadDocument.FileName = Path.GetFileName(path);
                    }
                    
                    File.WriteAllText(path, text);
                    await HandlePreviewGcodeAsync(text);
                    await NotifyAsync("success", "G-code", $"Lưu G-code thành công tại:\n{path}");
                }
                catch(Exception ex)
                {
                    await NotifyAsync("error", "G-code", "Lỗi khi lưu G-code: " + ex.Message);
                }
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

            var rows = BuildConnectedPathsFromCad();
            if (rows.Count == 0)
            {
                await NotifyAsync("info", "DXF", "No valid paths found.");
                return;
            }

            // Assign M-code for glue on/off points
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

            foreach (var row in rows)
            {
                if (glueStartCoord != null && string.Equals(row.EndCoordinate, glueStartCoord))
                    row.MCodeValue = "1";
                if (glueEndCoord != null && string.Equals(row.EndCoordinate, glueEndCoord))
                    row.MCodeValue = "2";

                // Lấy giá trị speed mặc định từ ô mới (chỉ áp dụng cho DXF)
                if (string.IsNullOrEmpty(row.Speed) && activeDocumentKind != "GCODE")
                    row.Speed = globalSpeed;
            }

            processRows.Clear();
            processRows.AddRange(rows);

            await PushDxfStateAsync();
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

            if (isSendingToPlc)
            {
                await NotifyAsync("info", "PLC", "PLC data transfer is already running.");
                return;
            }

            // Map ProcessRow → QD75BufferWriter.PositioningDataRow (tọa độ đã cộng offset)
            var dataRows = new List<QD75BufferWriter.PositioningDataRow>();
            string lastSpeed = globalSpeed; // fallback to globalSpeed if no F at all

            foreach (var row in processRows)
            {
                string effectiveSpeed = lastSpeed;
                if (!string.IsNullOrEmpty(row.Speed))
                {
                    if (int.TryParse(row.Speed, out int s) && s > 0)
                    {
                        lastSpeed = row.Speed;
                        effectiveSpeed = row.Speed;
                    }
                }

                dataRows.Add(new QD75BufferWriter.PositioningDataRow
                {
                    MotionType       = row.MotionType,
                    MCodeValue       = row.MCodeValue,
                    Dwell            = row.Dwell,
                    Speed            = effectiveSpeed,
                    EndCoordinate    = ApplyOffsetToCoordSend(row.EndCoordinate,    offsetX, offsetY),
                    CenterCoordinate = ApplyOffsetToCoordSend(row.CenterCoordinate, offsetX, offsetY)
                });
            }

            // ── Tạm dừng poll timer để tránh ContextSwitchDeadlock ──────────────────
            isSendingToPlc = true;
            plcPollTimer.Stop();

            try
            {
                // ── BƯỚC 1: Master axis (Axis 1 / X): nạp dữ liệu vào bộ đệm (G2000+) ────
                Action<int, int> progressCbX = (current, total) => 
                {
                    _ = PostToUiAsync("updateSendProgress", new { axis = "X", current, total });
                };

                var sendResult = await QD75BufferWriter.WritePositioningDataStableAsync(plcComm, 0, dataRows, progressCbX);

                if (!sendResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis1]", $"Failed to load Axis 1 buffer: {sendResult.ErrorMessage}");
                    return;
                }

                // ── BƯỚC 2: Slave axis (Axis 2 / Y): nạp toạ độ vào bộ đệm (G8006+) ──────
                Action<int, int> progressCbY = (current, total) => 
                {
                    _ = PostToUiAsync("updateSendProgress", new { axis = "Y", current, total });
                };

                var slaveResult = await QD75BufferWriter.WriteSlaveAxisDataStableAsync(plcComm, dataRows, 8000, progressCbY);

                if (!slaveResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis2]", $"Failed to load Axis 2 buffer: {slaveResult.ErrorMessage}");
                    return;
                }

                // Không tự động ghi Start No. vào G1500 nữa.
                // Người dùng sẽ nhấn nút "START ACTION (M2000)" trên giao diện để kích hoạt chạy máy.
                _ = PostToUiAsync("updateSendProgress", new { done = true });
                await NotifyAsync("success", "PLC", $"CAD data loaded: {dataRows.Count} points → Axis 1 & Axis 2. Press START ACTION to run.");
            }
            finally
            {
                // ── Bật lại poll timer sau khi ghi xong (hoặc lỗi) ──────────────────
                isSendingToPlc = false;
                if (plcComm != null && plcComm.IsConnected && !isClosing)
                    plcPollTimer.Start();
            }
        }

        // ── Build ProcessRow list from connected CAD paths ───────────────────────
        private List<ProcessRow> BuildConnectedPathsFromCad()
        {
            var result = new List<ProcessRow>();
            if (activeCadDocument?.Primitives == null) return result;

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

                        // Điểm nhảy sang start của path mới (đứt quãng) → phải dừng (Continuous Positioning)
                        // Điểm start của path duy nhất → Continuous Path (chạy thẳng vào path)
                        string startMotion = (isFirstPath && onlyPath)
                            ? "Line (Continuous Path)"
                            : "Line (Continuous Positioning)";

                        var startRow = new ProcessRow
                        {
                            MotionType       = startMotion,
                            EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", startPt.X, startPt.Y),
                            CenterCoordinate = string.Empty,
                            MCodeValue       = (!isGcodeDocument && pathClosed) ? "1" : string.Empty
                        };
                        ApplyPrimitiveExtraData(startRow, prim);
                        result.Add(startRow);
                    }

                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        for (int i = 1; i < prim.Points.Count; i++)
                        {
                            bool   isLastInPrim   = (i == prim.Points.Count - 1);
                            string currentSuffix  = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";

                            var row = new ProcessRow
                            {
                                MotionType       = "Line" + currentSuffix,
                                EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                    "{0:0.###};{1:0.###}", prim.Points[i].X, prim.Points[i].Y),
                                CenterCoordinate = string.Empty
                            };
                            ApplyPrimitiveExtraData(row, prim);
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
                        ApplyPrimitiveExtraData(row, prim);
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

        private static void ApplyPrimitiveExtraData(ProcessRow row, CadDocumentService.CadPrimitiveData primitive)
        {
            if (!string.IsNullOrWhiteSpace(primitive?.MCodeValue))
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
            const double LimitZ = 50.0;    // Trục 3 – Z (thông tin, chưa lấy từ file)

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
