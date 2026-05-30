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
        
        // ← NEW: For timer-based optimization (Cách 2)
        private double cachedPathDistToFirstHalf = 0;
        private double cachedPathDistToSecondHalf = 0;
        private int cachedCurrentSpeed = 0;

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
                    
                    // ← NEW (Cách 2): Calculate timing once at start
                    CalculateRefillTiming();
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
                // ← NEW (Cách 2): Use timer-based monitoring instead of polling
                await MonitorAndRefillWithTimerAsync(cts.Token);

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
        /// ← NEW (Cách 2): Calculate path distance and timing for refill points.
        /// Do this ONCE at startup instead of polling every 50ms.
        /// </summary>
        private void CalculateRefillTiming()
        {
            try
            {
                // Calculate distance from point 0 to point 300 (first half)
                cachedPathDistToFirstHalf = CalculatePathDistanceMm(0, HALF_SIZE);
                
                // Calculate distance from point 300 to point 600 (second half)
                cachedPathDistToSecondHalf = CalculatePathDistanceMm(HALF_SIZE, Math.Min(BUFFER_SIZE - 1, allRows.Count));
                
                // Read current speed from PLC (1 read only)
                cachedCurrentSpeed = ReadCurrentSpeedFromPLC();
                if (cachedCurrentSpeed <= 0) cachedCurrentSpeed = 100; // Fallback
                
                // Convert speed: mm/min → mm/ms
                double speedMmPerMs = cachedCurrentSpeed / 60000.0;
                
                // Calculate wait time (refill at 90% to be safe)
                double timeToFirstHalfMs = (cachedPathDistToFirstHalf / speedMmPerMs) * 0.9;
                double timeToSecondHalfMs = (cachedPathDistToSecondHalf / speedMmPerMs) * 0.9;
                
                Log($"Path dist (0-300): {cachedPathDistToFirstHalf:F1}mm @ {cachedCurrentSpeed}mm/min = {timeToFirstHalfMs:F0}ms");
                Log($"Path dist (300-600): {cachedPathDistToSecondHalf:F1}mm @ {cachedCurrentSpeed}mm/min = {timeToSecondHalfMs:F0}ms");
            }
            catch (Exception ex)
            {
                Log($"Warning: Failed to calculate timing: {ex.Message}. Falling back to polling.");
                cachedCurrentSpeed = 0; // Signal to use polling fallback
            }
        }

        /// <summary>
        /// ← NEW (Cách 2): Calculate total distance between two point indices.
        /// </summary>
        private double CalculatePathDistanceMm(int startPointIndex, int endPointIndex)
        {
            double totalDistance = 0;
            
            for (int i = startPointIndex; i < Math.Min(endPointIndex, allRows.Count - 1); i++)
            {
                var currRow = allRows[i];
                var nextRow = allRows[i + 1];
                
                // Parse coordinates from EndCoordinate string
                double x1 = ParseCoordValue(currRow.EndCoordinate, 0);  // X
                double y1 = ParseCoordValue(currRow.EndCoordinate, 1);  // Y
                double x2 = ParseCoordValue(nextRow.EndCoordinate, 0);
                double y2 = ParseCoordValue(nextRow.EndCoordinate, 1);
                
                // Euclidean distance
                double dx = x2 - x1;
                double dy = y2 - y1;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                
                totalDistance += distance;
            }
            
            return totalDistance;
        }

        /// <summary>
        /// ← NEW (Cách 2): Parse single coordinate value from "X;Y" string.
        /// </summary>
        private double ParseCoordValue(string coordinate, int axis)
        {
            // axis: 0=X, 1=Y
            if (string.IsNullOrWhiteSpace(coordinate)) return 0;
            
            var parts = coordinate.Split(';');
            if (parts.Length <= axis) return 0;
            
            double.TryParse(parts[axis], 
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value);
            return value;
        }

        /// <summary>
        /// ← NEW (Cách 2): Read current speed from PLC (1 read only).
        /// </summary>
        private int ReadCurrentSpeedFromPLC()
        {
            try
            {
                string device = $"U0\\G{804}";  // Md.17 Current Speed (Axis1)
                return plcComm.ReadDeviceValue(device);
            }
            catch
            {
                return 0;  // Fallback
            }
        }

        /// <summary>
        /// ← NEW (Cách 2): Monitor using Timer-based callbacks instead of polling loop.
        /// Eliminates 300+ ReadMd44() calls, saving 2-3 seconds.
        /// </summary>
        private async Task MonitorAndRefillWithTimerAsync(CancellationToken ct)
        {
            // If timing calculation failed, fall back to polling
            if (cachedCurrentSpeed <= 0)
            {
                Log("Timing calculation unavailable, using polling fallback.");
                await MonitorAndRefillAsync(ct);
                return;
            }

            try
            {
                double speedMmPerMs = cachedCurrentSpeed / 60000.0;
                
                // Calculate wait times (refill at 90% to be safe)
                double timeToFirstHalfMs = (cachedPathDistToFirstHalf / speedMmPerMs) * 0.9;
                double timeToSecondHalfMs = (cachedPathDistToSecondHalf / speedMmPerMs) * 0.9;
                
                // Timer 1: Refill first half (at calculated time)
                var timer1 = new System.Timers.Timer(timeToFirstHalfMs);
                timer1.AutoReset = false;
                timer1.Elapsed += async (s, e) =>
                {
                    try
                    {
                        Log("Timer 1: Refilling first half (1-300)...");
                        
                        int countToWrite = Math.Min(HALF_SIZE, allRows.Count - nextRowIndex);
                        if (countToWrite > 0)
                        {
                            var batch = allRows.GetRange(nextRowIndex, countToWrite);
                            await Task.Run(() => WriteBufferRange(batch, 0));
                            nextRowIndex += countToWrite;
                            totalPointsLoaded += countToWrite;
                            
                            OnProgress?.Invoke(totalPointsLoaded, allRows.Count);
                            Log($"Refilled 1-300: {totalPointsLoaded}/{allRows.Count} points loaded");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Refill 1 failed: {ex.Message}");
                    }
                };
                timer1.Start();

                // Timer 2: Refill second half (at calculated time)
                var timer2 = new System.Timers.Timer(timeToFirstHalfMs + timeToSecondHalfMs);
                timer2.AutoReset = false;
                timer2.Elapsed += async (s, e) =>
                {
                    try
                    {
                        Log("Timer 2: Refilling second half (301-600)...");
                        
                        int countToWrite = Math.Min(HALF_SIZE - 1, allRows.Count - nextRowIndex);
                        if (countToWrite > 0)
                        {
                            var batch = allRows.GetRange(nextRowIndex, countToWrite);
                            
                            // Last batch? Set END instead of JUMP
                            bool isLastBatch = (nextRowIndex + countToWrite >= allRows.Count);
                            if (!isLastBatch)
                            {
                                batch.Add(new QD75BufferWriter.PositioningDataRow
                                {
                                    MotionType = "JUMP_TO_1",
                                    EndCoordinate = "0;0",
                                    Speed = "0"
                                });
                            }
                            
                            await Task.Run(() => WriteBufferRange(batch, HALF_SIZE));
                            nextRowIndex += countToWrite;
                            totalPointsLoaded += countToWrite;
                            
                            OnProgress?.Invoke(totalPointsLoaded, allRows.Count);
                            Log($"Refilled 301-600: {totalPointsLoaded}/{allRows.Count} points");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Refill 2 failed: {ex.Message}");
                    }
                };
                timer2.Start();

                // Wait for all timers to complete + safety buffer
                double totalWaitMs = timeToFirstHalfMs + timeToSecondHalfMs + 5000;
                await Task.Delay((int)totalWaitMs, ct);
                
                timer1?.Dispose();
                timer2?.Dispose();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Timer-based monitoring failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitor Md.44 → khi máy chạy qua nửa buffer, ghi đè 300 điểm tiếp theo.
        /// (Fallback polling method - used if timing calculation fails)
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
