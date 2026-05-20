using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DACDT_2026
{
    /// <summary>
    /// Ring Buffer runner cho QD75 — chạy quỹ đạo dài >600 điểm bằng kỹ thuật ghi đè cuốn chiếu.
    ///
    /// Nguyên lý:
    ///   - QD75 chỉ chứa tối đa 600 điểm/trục (No.1 → No.600)
    ///   - Đặt JUMP tại No.600 → nhảy về No.1 (cài qua Da.2=0x82, Da.9=1)
    ///   - PC liên tục đọc Md.44 (G835 cho Axis 1) → biết máy đang ở điểm nào
    ///   - Khi máy qua nửa buffer đầu → ghi đè 300 điểm tiếp theo lên No.1-300
    ///   - Khi máy qua nửa buffer sau → ghi đè 300 điểm tiếp theo lên No.301-600
    /// </summary>
    public class QD75RingBufferRunner
    {
        private const int BUFFER_SIZE = 600; // Max points per axis
        private const int HALF_SIZE = 300;   // Half buffer for swap point
        private const int Md44_Axis1 = 835;  // U0\G835 — current data No. axis 1
        private const int POLL_INTERVAL_MS = 50;

        private readonly PLCCommunication plcComm;
        private readonly List<QD75BufferWriter.PositioningDataRow> allRows;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly object lockObj = new object();

        private int nextRowIndex;
        private int totalPointsLoaded;

        public event Action<int, int> OnProgress; // (loaded, total)
        public event Action<string> OnLog;
        public event Action OnComplete;
        public event Action<string> OnError;

        public bool IsRunning { get; private set; }

        public QD75RingBufferRunner(PLCCommunication plcComm, List<QD75BufferWriter.PositioningDataRow> allRows)
        {
            this.plcComm = plcComm;
            this.allRows = allRows;
        }

        /// <summary>
        /// Bắt đầu ring buffer: nạp 600 điểm đầu + JUMP, sau đó monitor Md.44 và ghi đè.
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning) return;
            IsRunning = true;

            try
            {
                // Bước 1: Nạp 600 điểm đầu tiên (hoặc ít hơn nếu file <600)
                int initialCount = Math.Min(allRows.Count, BUFFER_SIZE);
                var initialRows = allRows.GetRange(0, initialCount);

                // Nếu file >600 điểm, đặt JUMP tại điểm No.600 (index 599) để nhảy về No.1
                bool useRingBuffer = allRows.Count > BUFFER_SIZE;
                if (useRingBuffer)
                {
                    // Điểm No.599: đổi thành Continuous Positioning (dừng trước JUMP)
                    if (initialRows.Count >= BUFFER_SIZE)
                    {
                        var beforeJump = initialRows[BUFFER_SIZE - 2];
                        if (beforeJump.MotionType != null && beforeJump.MotionType.Contains("(Continuous Path)"))
                        {
                            beforeJump.MotionType = beforeJump.MotionType
                                .Replace("(Continuous Path)", "(Continuous Positioning)");
                        }
                    }
                    // Điểm No.600: JUMP về No.1
                    var jumpRow = new QD75BufferWriter.PositioningDataRow
                    {
                        MotionType = "JUMP_TO_1",
                        EndCoordinate = "0;0",
                        CenterCoordinate = string.Empty,
                        Speed = "0",
                        MCodeValue = "0",
                        Dwell = "1"
                    };
                    initialRows[BUFFER_SIZE - 1] = jumpRow;
                    nextRowIndex = BUFFER_SIZE - 1; // Tiếp theo nạp từ điểm 599 trở đi
                    Log($"Ring buffer mode: {allRows.Count} points total. Loading first {BUFFER_SIZE - 1} points + JUMP.");
                }
                else
                {
                    nextRowIndex = allRows.Count;
                    Log($"Standard mode: {allRows.Count} points (no ring buffer needed).");
                }

                // Ghi 600 điểm đầu xuống PLC
                await Task.Run(() => WriteBufferRange(initialRows, 0));
                totalPointsLoaded = initialCount;
                OnProgress?.Invoke(totalPointsLoaded, allRows.Count);

                if (!useRingBuffer)
                {
                    Log("All points loaded. No ring buffer monitoring needed.");
                    return;
                }

                // Bước 2: Monitor Md.44 và ghi đè cuốn chiếu
                await MonitorAndRefillAsync(cts.Token);

                Log("Ring buffer complete — all points loaded.");
                OnComplete?.Invoke();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public void Stop()
        {
            cts.Cancel();
        }

        /// <summary>
        /// Monitor Md.44 → khi máy chạy qua nửa buffer, ghi đè 300 điểm tiếp theo.
        /// </summary>
        private async Task MonitorAndRefillAsync(CancellationToken ct)
        {
            int lastMd44 = 0;
            bool passedFirstHalf = false;  // Máy đã chạy qua nửa đầu lần đầu tiên chưa?
            bool passedSecondHalf = false; // Máy đã chạy qua nửa sau lần đầu tiên chưa?

            while (!ct.IsCancellationRequested && nextRowIndex < allRows.Count)
            {
                int md44 = ReadMd44();
                if (md44 != lastMd44)
                {
                    lastMd44 = md44;
                }

                // Máy đang ở nửa sau (301-600) → nửa đầu (1-300) đã chạy xong → ghi đè nửa đầu
                if (md44 > HALF_SIZE && md44 <= BUFFER_SIZE)
                {
                    if (!passedFirstHalf)
                    {
                        passedFirstHalf = true; // Lần đầu máy vào nửa sau
                    }

                    if (passedFirstHalf && !passedSecondHalf)
                    {
                        // Ghi đè nửa đầu (No.1-300) với data mới
                        int countToWrite = Math.Min(HALF_SIZE, allRows.Count - nextRowIndex);
                        if (countToWrite > 0)
                        {
                            var batch = allRows.GetRange(nextRowIndex, countToWrite);
                            await Task.Run(() => WriteBufferRange(batch, 0));
                            nextRowIndex += countToWrite;
                            totalPointsLoaded += countToWrite;
                            Log($"Refilled first half (1-{countToWrite}) — {totalPointsLoaded}/{allRows.Count}");
                            OnProgress?.Invoke(totalPointsLoaded, allRows.Count);
                        }
                        passedSecondHalf = true; // Đợi máy quay lại nửa đầu mới ghi đè nửa sau
                    }
                }

                // Máy đang ở nửa đầu (1-300) → nửa sau (301-600) đã chạy xong → ghi đè nửa sau
                if (md44 > 0 && md44 <= HALF_SIZE)
                {
                    if (passedSecondHalf)
                    {
                        // Ghi đè nửa sau (No.301-599) + JUMP tại No.600
                        int countToWrite = Math.Min(HALF_SIZE - 1, allRows.Count - nextRowIndex); // -1 cho JUMP
                        if (countToWrite > 0)
                        {
                            var batch = allRows.GetRange(nextRowIndex, countToWrite);

                            // Kiểm tra: đây là batch cuối?
                            bool isLastBatch = (nextRowIndex + countToWrite >= allRows.Count);
                            if (isLastBatch && batch.Count > 0)
                            {
                                // Batch cuối: đặt END thay vì JUMP
                                var last = batch[batch.Count - 1];
                                if (last.MotionType != null && !last.MotionType.Contains("(End)"))
                                {
                                    last.MotionType = last.MotionType
                                        .Replace("(Continuous Path)", " (End)")
                                        .Replace("(Continuous Positioning)", " (End)");
                                }
                                await Task.Run(() => WriteBufferRange(batch, HALF_SIZE));
                            }
                            else
                            {
                                // Thêm JUMP ở cuối batch
                                batch.Add(new QD75BufferWriter.PositioningDataRow
                                {
                                    MotionType = "JUMP_TO_1",
                                    EndCoordinate = "0;0",
                                    CenterCoordinate = string.Empty,
                                    Speed = "0",
                                    MCodeValue = "0",
                                    Dwell = "1"
                                });
                                // Điểm trước JUMP cần Continuous Positioning
                                if (batch.Count >= 2)
                                {
                                    var beforeJump = batch[batch.Count - 2];
                                    if (beforeJump.MotionType != null && beforeJump.MotionType.Contains("(Continuous Path)"))
                                        beforeJump.MotionType = beforeJump.MotionType.Replace("(Continuous Path)", "(Continuous Positioning)");
                                }
                                await Task.Run(() => WriteBufferRange(batch, HALF_SIZE));
                            }

                            nextRowIndex += countToWrite;
                            totalPointsLoaded += countToWrite;
                            Log($"Refilled second half ({HALF_SIZE + 1}-{HALF_SIZE + countToWrite}) — {totalPointsLoaded}/{allRows.Count}");
                            OnProgress?.Invoke(totalPointsLoaded, allRows.Count);
                        }
                        passedFirstHalf = false;
                        passedSecondHalf = false;
                    }
                }

                await Task.Delay(POLL_INTERVAL_MS, ct);
            }
        }

        /// <summary>
        /// Ghi một range các point vào buffer PLC bắt đầu từ offset (0 = No.1, 300 = No.301).
        /// </summary>
        private void WriteBufferRange(List<QD75BufferWriter.PositioningDataRow> rows, int pointOffset)
        {
            int baseG = QD75BufferWriter.ProgramBaseG[0]; // Axis 1 = G2000
            int slaveBaseG = QD75BufferWriter.ProgramBaseG[1]; // Axis 2 = G8000
            int gOffset = pointOffset * QD75BufferWriter.Stride;

            // Build bulk data cho master (X) và slave (Y)
            int totalWords = rows.Count * QD75BufferWriter.Stride;
            short[] masterData = new short[totalWords];
            short[] slaveData = new short[totalWords];

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int blockOffset = i * QD75BufferWriter.Stride;

                // Special: JUMP command
                if (row.MotionType == "JUMP_TO_1")
                {
                    // Da.2 = 0x82 (JUMP), Da.1 = 0 (END pattern), Da.5 = Axis2
                    int jumpVal = (0x82 << 8) | (1 << 2); // = 33284
                    short jumpId = (short)(jumpVal & 0xFFFF);
                    masterData[blockOffset + 0] = jumpId; // Identifier
                    masterData[blockOffset + 2] = 1;      // Da.9 Dwell = JUMP target = No.1
                    // Slave cũng cần JUMP đồng bộ
                    slaveData[blockOffset + 0] = jumpId;
                    slaveData[blockOffset + 2] = 1;
                    continue;
                }

                short moveCode = QD75BufferWriter.BuildPositioningIdentifierWord(row.MotionType);
                masterData[blockOffset + 0] = moveCode; // Identifier
                masterData[blockOffset + 1] = (short)ParseInt(row.MCodeValue);
                masterData[blockOffset + 2] = (short)ParseInt(row.Dwell);
                int speedVal = ParseInt(row.Speed) * QD75BufferWriter.SpeedMultiplier;
                masterData[blockOffset + 4] = (short)(speedVal & 0xFFFF);
                masterData[blockOffset + 5] = (short)((speedVal >> 16) & 0xFFFF);
                int endX = ParseCoordX(row.EndCoordinate);
                masterData[blockOffset + 6] = (short)(endX & 0xFFFF);
                masterData[blockOffset + 7] = (short)((endX >> 16) & 0xFFFF);
                int centerX = ParseCoordX(row.CenterCoordinate);
                masterData[blockOffset + 8] = (short)(centerX & 0xFFFF);
                masterData[blockOffset + 9] = (short)((centerX >> 16) & 0xFFFF);

                // Slave Y
                slaveData[blockOffset + 0] = moveCode;
                int endY = ParseCoordY(row.EndCoordinate);
                slaveData[blockOffset + 6] = (short)(endY & 0xFFFF);
                slaveData[blockOffset + 7] = (short)((endY >> 16) & 0xFFFF);
                int centerY = ParseCoordY(row.CenterCoordinate);
                slaveData[blockOffset + 8] = (short)(centerY & 0xFFFF);
                slaveData[blockOffset + 9] = (short)((centerY >> 16) & 0xFFFF);
            }

            int masterRes = plcComm.WriteBuffer(0, baseG + gOffset, masterData);
            int slaveRes = plcComm.WriteBuffer(0, slaveBaseG + gOffset, slaveData);

            if (masterRes != 0 || slaveRes != 0)
            {
                throw new Exception($"WriteBuffer failed: master={masterRes}, slave={slaveRes}");
            }
        }

        private int ReadMd44()
        {
            try
            {
                string device = $"U0\\G{Md44_Axis1}";
                return plcComm.ReadDeviceValue(device);
            }
            catch
            {
                return 0;
            }
        }

        private static int ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            int.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int v);
            return v;
        }

        private static int ParseCoordX(string coord)
        {
            if (string.IsNullOrWhiteSpace(coord)) return 0;
            var parts = coord.Split(';');
            if (parts.Length >= 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
                return Convert.ToInt32(Math.Round(v * QD75BufferWriter.CoordinateMultiplier));
            return 0;
        }

        private static int ParseCoordY(string coord)
        {
            if (string.IsNullOrWhiteSpace(coord)) return 0;
            var parts = coord.Split(';');
            if (parts.Length >= 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
                return Convert.ToInt32(Math.Round(v * QD75BufferWriter.CoordinateMultiplier));
            return 0;
        }

        private void Log(string msg) => OnLog?.Invoke(msg);
    }
}
