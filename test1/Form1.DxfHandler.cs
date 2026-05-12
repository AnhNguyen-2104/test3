using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace test1
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
                dialog.Filter        = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*";
                dialog.Title         = "Open DXF file";
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
                    AddLogEntry("DXF", selectedPath, "Read", "Selected", "OpenFileDialog");

                    LoadCadDocument(selectedPath);

                    if (activeCadDocument?.Primitives != null)
                    {
                        var paths = GetConnectedPathsFromCad(activeCadDocument.Primitives);
                        activeCadDocument.Primitives.Clear();
                        foreach (var path in paths)
                            activeCadDocument.Primitives.AddRange(path);
                    }

                    currentView = "dxf";
                    await PushDxfStateAsync();
                    AddLogEntry("DXF", activeCadDocument?.FilePath ?? selectedPath, "Read", "OK",
                        $"Loaded file: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");
                    await NotifyAsync("success", "DXF",
                        $"Loaded: {activeCadDocument?.FileName ?? Path.GetFileName(selectedPath)}");

                    // Bỏ qua nhấn Import: Tự động chạy Import ngay sau khi load xong DXF
                    await HandleImportCadToProcessAsync();
                }
                catch (Exception ex)
                {
                    await NotifyAsync("error", "DXF", ex.Message);
                }
            }
        }

        private void LoadCadDocument(string filePath)
        {
            activeCadDocument = cadService.Load(filePath);
            selectedCadPointKey = activeCadDocument.Points.FirstOrDefault()?.Key;
            assignedPointKeys.Clear();
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
            await PushDxfStateAsync();
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

                // Lấy giá trị speed mặc định từ ô mới
                if (string.IsNullOrEmpty(row.Speed))
                    row.Speed = globalSpeed;
            }

            processRows.Clear();
            processRows.AddRange(rows);

            await PushDxfStateAsync();
            await NotifyAsync("success", "DXF", $"Compiled {rows.Count} movement commands into the process table.");
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

            // Map ProcessRow → QD75BufferWriter.PositioningDataRow
            var dataRows = new List<QD75BufferWriter.PositioningDataRow>();
            foreach (var row in processRows)
            {
                dataRows.Add(new QD75BufferWriter.PositioningDataRow
                {
                    MotionType       = row.MotionType,
                    MCodeValue       = row.MCodeValue,
                    Dwell            = row.Dwell,
                    Speed            = row.Speed,
                    EndCoordinate    = row.EndCoordinate,
                    CenterCoordinate = row.CenterCoordinate
                });
            }

            // ── Tạm dừng poll timer để tránh ContextSwitchDeadlock ──────────────────
            // Poll timer chạy Task.Run với COM calls từ background thread.
            // Nếu UI thread đang bận ghi batch data, COM không thể marshal
            // từ background thread về STA thread → deadlock sau 60 giây.
            plcPollTimer.Stop();

            try
            {
                // ── BƯỚC 1: Master axis (Axis 1 / X): nạp dữ liệu vào bộ đệm (G2000+) ────
                // Tắt writeStartNo để máy KHÔNG chạy ngay lập tức.
                var sendResult = QD75BufferWriter.WritePositioningData(plcComm, 0, dataRows, writeStartNo: false);

                foreach (var wr in sendResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Write", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("error", "Telemetry [Axis1]", $"{wr.Address}: {wr.Message}");
                }

                if (!sendResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis1]", "Failed to load Axis 1 buffer.");
                    return;
                }

                // ── BƯỚC 2: Slave axis (Axis 2 / Y): nạp toạ độ vào bộ đệm (G8006+) ──────
                var slaveResult = QD75BufferWriter.WriteSlaveAxisData(plcComm, dataRows, slaveBaseG: 8000);

                foreach (var wr in slaveResult.WriteResults)
                {
                    AddLogEntry(wr.Address, wr.Value, "Write", wr.Status, wr.Message);
                    if (!wr.Status.StartsWith("OK"))
                        await NotifyAsync("error", "Telemetry [Axis2]", $"{wr.Address}: {wr.Message}");
                }

                if (!slaveResult.Success)
                {
                    await NotifyAsync("error", "Telemetry [Axis2]", "Failed to load Axis 2 buffer.");
                    return;
                }

                // Không tự động ghi Start No. vào G1500 nữa.
                // Người dùng sẽ nhấn nút "START ACTION (M2000)" trên giao diện để kích hoạt chạy máy.
                await NotifyAsync("success", "PLC", $"CAD data loaded: {dataRows.Count} points → Axis 1 (G2000+) & Axis 2 (G8006+). Press START ACTION to run.");
            }
            finally
            {
                // ── Bật lại poll timer sau khi ghi xong (hoặc lỗi) ──────────────────
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

            for (int pathIdx = 0; pathIdx < paths.Count; pathIdx++)
            {
                var path        = paths[pathIdx];
                bool isLastPath = (pathIdx == paths.Count - 1);
                bool isFirstPath = (pathIdx == 0);

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

                        result.Add(new ProcessRow
                        {
                            MotionType       = startMotion,
                            EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                "{0:0.###};{1:0.###}", startPt.X, startPt.Y),
                            CenterCoordinate = string.Empty
                        });
                    }

                    if (prim.SourceType.Contains("Line") || prim.SourceType.Contains("Polyline"))
                    {
                        for (int i = 1; i < prim.Points.Count; i++)
                        {
                            bool   isLastInPrim   = (i == prim.Points.Count - 1);
                            string currentSuffix  = (isLastInPrim && isLastInPath) ? suffix : " (Continuous Path)";

                            result.Add(new ProcessRow
                            {
                                MotionType       = "Line" + currentSuffix,
                                EndCoordinate    = string.Format(CultureInfo.InvariantCulture,
                                    "{0:0.###};{1:0.###}", prim.Points[i].X, prim.Points[i].Y),
                                CenterCoordinate = string.Empty
                            });
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
    }
}
