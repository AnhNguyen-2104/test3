using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace DACDT_2026
{
    /// <summary>
    /// Handles all QD75 Positioning Module buffer memory (Un\Gx) read/write operations.
    /// Extracted from Form1 to keep the main form code clean.
    /// </summary>
    public class QD75BufferWriter
    {
        // QD75 Positioning Module Buffer Memory Addresses
        // Monitor base: Axis1=G800, Axis2=G900, Axis3=G1000, Axis4=G1100
        public static readonly int[] MonitorBaseG = { 800, 900, 1000, 1100 };
        // Control base: Axis1=G1500, Axis2=G1600, Axis3=G1700, Axis4=G1800
        public static readonly int[] ControlBaseG = { 1500, 1600, 1700, 1800 };
        // Program data base (Move/Mcode/Dwell/Speed/Position/Center): Axis1=G2000, Axis2=G8000, Axis3=G14000, Axis4=G20000
        public static readonly int[] ProgramBaseG = { 2000, 8000, 14000, 20000 };

        // Monitor offsets (from MonitorBaseG)
        public const int OffCurrentPos = 0;      // Current Feed Value (32-bit)
        public const int OffCurrentSpeed = 4;     // Current Speed (32-bit)
        public const int OffErrorCode = 6;        // Error Code (16-bit)
        public const int OffWarningCode = 7;      // Warning Code (16-bit)
        public const int OffAxisStatus = 9;       // Axis Status Md.26 (16-bit)

        // Control offsets (from ControlBaseG)
        public const int OffStartNo = 0;          // Start No. (16-bit)
        public const int OffErrorReset = 2;       // Error Reset (16-bit)
        public const int OffJogSpeed = 4;         // JOG Speed (32-bit)
        public const int OffNewSpeed = 18;        // New Speed Value (32-bit)

        // Program data offsets within each positioning data block (stride = 10 words)
        public const int Stride = 10;
        public const int OffsetMoveCode = 0;  // U0\G(base + (n-1)*10 + 0) Positioning Identifier
        public const int OffsetMCode = 1;     // U0\G(base + (n-1)*10 + 1)
        public const int OffsetDwell = 2;     // U0\G(base + (n-1)*10 + 2)
        public const int OffsetReserved = 3;  // U0\G(base + (n-1)*10 + 3) reserved / 0
        public const int OffsetSpeed = 4;     // U0\G(base + (n-1)*10 + 4) -> 32-bit (L, H)
        public const int OffsetPosX = 6;      // U0\G(base + (n-1)*10 + 6) -> 32-bit (L, H) — toạ độ trục này
        public const int OffsetCenterX = 8;   // U0\G(base + (n-1)*10 + 8) -> 32-bit (L, H) — tâm cung trục này

        // Coordinate multiplier: DXF mm → QD75 0.1µm units
        public const double CoordinateMultiplier = 10000.0;
        // Speed multiplier: user mm/min → QD75 speed units
        public const int SpeedMultiplier = 100;
        private const int MaxWordsPerPlcWrite = 900;
        private const int SlaveProgressStep = 10;

        /// <summary>
        /// Result of a single buffer write operation.
        /// </summary>
        public class WriteResult
        {
            public string Address { get; set; }
            public string Value { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Data structure for one positioning data row to write to PLC.
        /// </summary>
        public class PositioningDataRow
        {
            public string MotionType { get; set; }
            public string MCodeValue { get; set; }
            public string Dwell { get; set; }
            public string Speed { get; set; }
            public string EndCoordinate { get; set; }
            public string CenterCoordinate { get; set; }
        }

        /// <summary>
        /// Result of writing all positioning data rows.
        /// </summary>
        public class SendResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public List<WriteResult> WriteResults { get; set; } = new List<WriteResult>();
        }

        /// <summary>
        /// Build QD75 Positioning Identifier word (Da.1 ~ Da.5) from motion type string.
        ///
        /// Correct bit layout (per QD75 manual):
        ///   b1 – b0  : Da.1 Operation pattern  (00=END, 01=Cont.Pos, 11=Cont.Path)
        ///   b3 – b2  : Da.5 Axis to interpolate (00=Axis1, 01=Axis2, 10=Axis3, 11=Axis4)
        ///   b5 – b4  : Da.3 Acceleration time No. (0–3)
        ///   b7 – b6  : Da.4 Deceleration time No. (0–3)
        ///   b15– b8  : Da.2 Control system
        /// </summary>
        public static short BuildPositioningIdentifierWord(
            string motionType,
            int partnerAxis = 1,    // 0=Axis1, 1=Axis2, 2=Axis3, 3=Axis4
            int accelTimeNo = 0,
            int decelTimeNo = 0)
        {
            string s = (motionType ?? string.Empty).Trim().ToLowerInvariant();

            // ── Da.1 Operation pattern ────────────────────────────────────────────
            // b1–b0: 00=PositioningComplete(END), 01=ContinuousPositioning, 11=ContinuousPath
            int da1 = 0x00; // default: Positioning Complete (END)
            bool isEnd = s.Contains("end") || s.Contains("hoàn thành");

            if (!isEnd)
            {
                bool isContinuousPositioning = s.Contains("continuous positioning") || s.Contains("điểm kế tiếp");
                bool isContinuousPath        = s.Contains("continuous path")        || s.Contains("liên tục");
                if (isContinuousPositioning)       da1 = 0x01;
                else if (isContinuousPath)         da1 = 0x03;
                else                               da1 = 0x03; // default to continuous path for mid-points
            }

            // ── Da.2 Control system ───────────────────────────────────────────────
            // b15–b8
            int da2;
            if      (s.Contains("arc cw")  || s.Contains("arc circular right")) da2 = 0x0F; // ABS_CircularRight
            else if (s.Contains("arc ccw") || s.Contains("arc circular left"))  da2 = 0x10; // ABS_CircularLeft
            else if (s.Contains("circle"))
                da2 = s.Contains("ccw") ? 0x10 : 0x0F;                                      // circle default CW
            else
                da2 = 0x0A;                                                                  // ABS_Linear2

            // ── Da.5 Partner axis (b3–b2), Da.3 Acc (b5–b4), Da.4 Dec (b7–b6) ──
            int da5 = partnerAxis & 0x03;
            int da3 = accelTimeNo & 0x03;
            int da4 = decelTimeNo & 0x03;

            // Assemble: b1-b0=Da.1 | b3-b2=Da.5 | b5-b4=Da.3 | b7-b6=Da.4 | b15-b8=Da.2
            int wordValue = da1 | (da5 << 2) | (da3 << 4) | (da4 << 6) | (da2 << 8);

            return unchecked((short)wordValue);
        }

        private static int ParseInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
                return result;
            return 0;
        }

        private static int ParseSpeedPlcUnits(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out int i))
                return i * SpeedMultiplier;
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                return Convert.ToInt32(Math.Round(d * SpeedMultiplier));
            return 0;
        }

        /// <summary>Chỉ ghi dòng có toạ độ X;Y hợp lệ (bỏ dòng cấu hình / trống).</summary>
        public static bool IsValidPositioningRow(PositioningDataRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.EndCoordinate)) return false;
            int x, y;
            return TryParseCoordinateX(row.EndCoordinate, out x)
                && TryParseCoordinateY(row.EndCoordinate, out y);
        }

        public static List<PositioningDataRow> FilterPositioningRows(IEnumerable<PositioningDataRow> rows)
        {
            var list = new List<PositioningDataRow>();
            if (rows == null) return list;
            foreach (var row in rows)
            {
                if (IsValidPositioningRow(row))
                    list.Add(row);
            }
            return list;
        }

        /// <summary>Chuỗi hex 10 word để đối chiếu GX Works (địa chỉ G tuyệt đối).</summary>
        public static string FormatPointWordsHex(short[] words, int pointIndex, int baseG)
        {
            if (words == null || words.Length < (pointIndex + 1) * Stride) return string.Empty;
            int baseIndex = pointIndex * Stride;
            var parts = new string[Stride];
            for (int w = 0; w < Stride; w++)
                parts[w] = $"G{baseG + baseIndex + w}:{(ushort)words[baseIndex + w]:X4}";
            return string.Join(" ", parts);
        }

        private static bool TryParseCoordinateX(string coordinate, out int scaledX)
        {
            scaledX = 0;
            if (string.IsNullOrWhiteSpace(coordinate)) return false;
            var parts = coordinate.Split(';');
            if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            {
                scaledX = Convert.ToInt32(Math.Round(val * CoordinateMultiplier));
                return true;
            }
            return false;
        }

        private static bool TryParseCoordinateY(string coordinate, out int scaledY)
        {
            scaledY = 0;
            if (string.IsNullOrWhiteSpace(coordinate)) return false;
            var parts = coordinate.Split(';');
            if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            {
                scaledY = Convert.ToInt32(Math.Round(val * CoordinateMultiplier));
                return true;
            }
            return false;
        }

        public static async Task<SendResult> WritePositioningDataBulkAsync(PLCCommunication plcComm, int axisIndex, List<PositioningDataRow> rows, bool writeStartNo = true, Action<int, int> progressCallback = null)
        {
            var finalResult = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                finalResult.Success = false;
                finalResult.ErrorMessage = "PLC is not connected.";
                return finalResult;
            }

            if (rows == null || rows.Count == 0)
            {
                finalResult.Success = false;
                finalResult.ErrorMessage = "No points to send.";
                return finalResult;
            }

            int baseG = ProgramBaseG[axisIndex];
            int numThreads = 4;
            int chunkSize = (int)Math.Ceiling((double)rows.Count / numThreads);
            var tasks = new List<Task<SendResult>>();
            int completedPoints = 0;
            object progressLock = new object();

            for (int t = 0; t < numThreads; t++)
            {
                int startIdx = t * chunkSize;
                if (startIdx >= rows.Count) break;

                int count = Math.Min(chunkSize, rows.Count - startIdx);
                var chunkRows = rows.GetRange(startIdx, count);

                tasks.Add(Task.Run(() =>
                {
                    var result = new SendResult { Success = true };
                    using (var localPlc = new PLCCommunication(plcComm.IPAddress, plcComm.Port, plcComm.LogicalStationNumber))
                    {
                        if (!localPlc.Connect())
                        {
                            result.Success = false;
                            result.ErrorMessage = "Một luồng không thể kết nối tới PLC.";
                            return result;
                        }

                        for (int i = 0; i < chunkRows.Count; i++)
                        {
                            var row = chunkRows[i];
                            short[] pointData = new short[Stride];

                            pointData[OffsetMoveCode] = BuildPositioningIdentifierWord(row.MotionType, partnerAxis: 1);
                            pointData[OffsetMCode] = (short)ParseInt(row.MCodeValue);
                            pointData[OffsetDwell] = (short)ParseInt(row.Dwell);
                            pointData[OffsetReserved] = 0;

                            int speedVal = ParseSpeedPlcUnits(row.Speed);
                            pointData[OffsetSpeed] = (short)(speedVal & 0xFFFF);
                            pointData[OffsetSpeed + 1] = (short)((speedVal >> 16) & 0xFFFF);

                            int endX = 0;
                            if (TryParseCoordinateX(row.EndCoordinate, out endX))
                            {
                                pointData[OffsetPosX] = (short)(endX & 0xFFFF);
                                pointData[OffsetPosX + 1] = (short)((endX >> 16) & 0xFFFF);
                            }

                            int centerX = 0;
                            if (TryParseCoordinateX(row.CenterCoordinate, out centerX))
                            {
                                pointData[OffsetCenterX] = (short)(centerX & 0xFFFF);
                                pointData[OffsetCenterX + 1] = (short)((centerX >> 16) & 0xFFFF);
                            }

                            try
                            {
                                int pointAddress = baseG + (startIdx + i) * Stride;
                                int res = localPlc.WriteBuffer(0, pointAddress, pointData);
                                if (res != 0)
                                {
                                    result.Success = false;
                                    result.ErrorMessage = $"Lỗi ghi điểm {startIdx + i + 1} tại G{pointAddress}: {res}";
                                    return result;
                                }
                            }
                            catch (Exception ex)
                            {
                                result.Success = false;
                                result.ErrorMessage = ex.Message;
                                return result;
                            }

                            if (progressCallback != null)
                            {
                                lock (progressLock)
                                {
                                    completedPoints++;
                                    if (completedPoints % 5 == 0 || completedPoints == rows.Count)
                                    {
                                        progressCallback(completedPoints, rows.Count);
                                    }
                                }
                            }
                        }
                    }
                    return result;
                }));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var res in results)
            {
                if (!res.Success)
                {
                    finalResult.Success = false;
                    finalResult.ErrorMessage = res.ErrorMessage;
                    break;
                }
            }

            return finalResult;
        }

        public static async Task<SendResult> WriteSlaveAxisDataBulkAsync(PLCCommunication plcComm, List<PositioningDataRow> rows, int slaveBaseG = 8000, Action<int, int> progressCallback = null)
        {
            var finalResult = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                finalResult.Success = false;
                finalResult.ErrorMessage = "PLC is not connected.";
                return finalResult;
            }

            if (rows == null || rows.Count == 0)
            {
                finalResult.Success = false;
                finalResult.ErrorMessage = "No points to send.";
                return finalResult;
            }

            int numThreads = 4;
            int chunkSize = (int)Math.Ceiling((double)rows.Count / numThreads);
            var tasks = new List<Task<SendResult>>();
            int completedPoints = 0;
            object progressLock = new object();

            for (int t = 0; t < numThreads; t++)
            {
                int startIdx = t * chunkSize;
                if (startIdx >= rows.Count) break;

                int count = Math.Min(chunkSize, rows.Count - startIdx);
                var chunkRows = rows.GetRange(startIdx, count);

                tasks.Add(Task.Run(() =>
                {
                    var result = new SendResult { Success = true };
                    using (var localPlc = new PLCCommunication(plcComm.IPAddress, plcComm.Port, plcComm.LogicalStationNumber))
                    {
                        if (!localPlc.Connect())
                        {
                            result.Success = false;
                            result.ErrorMessage = "Một luồng không thể kết nối tới PLC.";
                            return result;
                        }

                        for (int i = 0; i < chunkRows.Count; i++)
                        {
                            var row = chunkRows[i];
                            int pointAddress = slaveBaseG + (startIdx + i) * Stride;
                            short[] pointData = new short[Stride];
                            FillPositioningPointWords(pointData, 0, row, partnerAxis: 0, useYAxis: true);

                            try
                            {
                                int res = localPlc.WriteBuffer(0, pointAddress, pointData);
                                if (res != 0)
                                {
                                    result.Success = false;
                                    result.ErrorMessage = $"Lỗi ghi trục 2 điểm {startIdx + i + 1} tại G{pointAddress}: {res}";
                                    return result;
                                }
                            }
                            catch (Exception ex)
                            {
                                result.Success = false;
                                result.ErrorMessage = ex.Message;
                                return result;
                            }

                            if (progressCallback != null)
                            {
                                lock (progressLock)
                                {
                                    completedPoints++;
                                    if (completedPoints % 5 == 0 || completedPoints == rows.Count)
                                    {
                                        progressCallback(completedPoints, rows.Count);
                                    }
                                }
                            }
                        }
                    }
                    return result;
                }));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var res in results)
            {
                if (!res.Success)
                {
                    finalResult.Success = false;
                    finalResult.ErrorMessage = res.ErrorMessage;
                    break;
                }
            }

            return finalResult;
        }

        public static async Task<SendResult> WritePositioningDataStableAsync(PLCCommunication plcComm, int axisIndex, List<PositioningDataRow> rows, Action<int, int> progressCallback = null)
        {
            var validation = ValidateBulkRequest(plcComm, rows);
            if (!validation.Success) return validation;
            if (axisIndex < 0 || axisIndex >= ProgramBaseG.Length)
                return new SendResult { Success = false, ErrorMessage = "Invalid axis index." };

            return await Task.Run(() =>
            {
                short[] allWords = BuildMasterAxisWords(rows);
                return WriteProgramBufferBlock(plcComm, ProgramBaseG[axisIndex], allWords, rows.Count, axisIndex + 1, progressCallback);
            });
        }

        public static async Task<SendResult> WriteSlaveAxisDataStableAsync(PLCCommunication plcComm, List<PositioningDataRow> rows, int slaveBaseG = 8000, Action<int, int> progressCallback = null)
        {
            var validation = ValidateBulkRequest(plcComm, rows);
            if (!validation.Success) return validation;

            return await Task.Run(() =>
            {
                short[] allWords = BuildSlaveAxisWords(rows);
                return WriteProgramBufferBlock(plcComm, slaveBaseG, allWords, rows.Count, 2, progressCallback);
            });
        }

        /// <summary>
        /// Ghi khối program buffer: điểm i → G(base + i*10) … G(base + i*10 + 9), tối đa 900 word/lần WriteBuffer.
        /// </summary>
        private static SendResult WriteProgramBufferBlock(
            PLCCommunication plcComm,
            int baseG,
            short[] allWords,
            int pointCount,
            int axisNumber,
            Action<int, int> progressCallback)
        {
            var result = new SendResult { Success = true };

            try
            {
                if (pointCount > 0)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{baseG}",
                        Status = "Debug",
                        Message = "Pt0 " + FormatPointWordsHex(allWords, 0, baseG)
                            + (pointCount > 1 ? " | Pt1 " + FormatPointWordsHex(allWords, 1, baseG) : string.Empty)
                    });
                }

                int maxRowsPerWrite = Math.Max(1, MaxWordsPerPlcWrite / Stride);

                for (int rowOffset = 0; rowOffset < pointCount; rowOffset += maxRowsPerWrite)
                {
                    int rowsInBlock = Math.Min(maxRowsPerWrite, pointCount - rowOffset);
                    int wordOffset = rowOffset * Stride;
                    int wordCount = rowsInBlock * Stride;
                    int address = baseG + wordOffset;

                    int res = plcComm.WriteBufferBlock(0, address, allWords, wordOffset, wordCount);
                    if (res != 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Axis {axisNumber}: WriteBufferBlock failed at U0\\G{address} ({wordCount} words), code {res}. "
                            + FormatPointWordsHex(allWords, rowOffset, baseG);
                        return result;
                    }

                    progressCallback?.Invoke(rowOffset + rowsInBlock, pointCount);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static SendResult ValidateBulkRequest(PLCCommunication plcComm, List<PositioningDataRow> rows)
        {
            if (plcComm == null || !plcComm.IsConnected)
                return new SendResult { Success = false, ErrorMessage = "PLC is not connected." };

            if (rows == null || rows.Count == 0)
                return new SendResult { Success = false, ErrorMessage = "No points to send." };

            return new SendResult { Success = true };
        }

        private static short[] BuildMasterAxisWords(List<PositioningDataRow> rows)
        {
            short[] words = new short[rows.Count * Stride];
            for (int i = 0; i < rows.Count; i++)
                FillPositioningPointWords(words, i * Stride, rows[i], partnerAxis: 1, useYAxis: false);
            return words;
        }

        /// <summary>Trục 2 (G8000+): full 10 word/điểm, toạ độ Y tại offset +6/+7, trục ghép Da.5 = trục 1.</summary>
        private static short[] BuildSlaveAxisWords(List<PositioningDataRow> rows)
        {
            short[] words = new short[rows.Count * Stride];
            for (int i = 0; i < rows.Count; i++)
                FillPositioningPointWords(words, i * Stride, rows[i], partnerAxis: 0, useYAxis: true);
            return words;
        }

        private static void FillPositioningPointWords(
            short[] words,
            int baseIndex,
            PositioningDataRow row,
            int partnerAxis,
            bool useYAxis)
        {
            Array.Clear(words, baseIndex, Stride);

            words[baseIndex + OffsetMoveCode] = BuildPositioningIdentifierWord(row.MotionType, partnerAxis);
            words[baseIndex + OffsetMCode] = (short)ParseInt(row.MCodeValue);
            words[baseIndex + OffsetDwell] = (short)ParseInt(row.Dwell);
            words[baseIndex + OffsetReserved] = 0;

            int speedVal = ParseSpeedPlcUnits(row.Speed);
            WriteInt32Words(words, baseIndex + OffsetSpeed, speedVal);

            if (useYAxis)
            {
                int endY;
                if (TryParseCoordinateY(row.EndCoordinate, out endY))
                    WriteInt32Words(words, baseIndex + OffsetPosX, endY);

                int centerY;
                if (TryParseCoordinateY(row.CenterCoordinate, out centerY))
                    WriteInt32Words(words, baseIndex + OffsetCenterX, centerY);
            }
            else
            {
                int endX;
                if (TryParseCoordinateX(row.EndCoordinate, out endX))
                    WriteInt32Words(words, baseIndex + OffsetPosX, endX);

                int centerX;
                if (TryParseCoordinateX(row.CenterCoordinate, out centerX))
                    WriteInt32Words(words, baseIndex + OffsetCenterX, centerX);
            }
        }

        private static void WriteInt32Words(short[] words, int index, int value)
        {
            words[index] = (short)(value & 0xFFFF);
            words[index + 1] = (short)((value >> 16) & 0xFFFF);
        }

        public static WriteResult StartAxis(PLCCommunication plcComm, int axisIndex, int startNo)
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                return new WriteResult { Status = "Error", Message = "PLC not connected" };
            }
            try
            {
                string startDevice = $"U0\\G{ControlBaseG[axisIndex]}";
                string used;
                int result = plcComm.WriteInt16ToDevicePath(startDevice, (short)startNo, out used);
                return new WriteResult
                {
                    Address = startDevice,
                    Value   = startNo.ToString(),
                    Status  = result == 0 ? "OK" : $"Error({result})",
                    Message = $"Manual Start Axis {axisIndex + 1}: {used}"
                }; 
            }
            catch (Exception ex)
            {
                return new WriteResult { Address = $"U0\\G{ControlBaseG[axisIndex]}", Status = "Error", Message = ex.Message };
            }
        }

        public static WriteResult WriteBufferValue(PLCCommunication plcComm, string path, int value)
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                return new WriteResult { Address = path, Value = value.ToString(), Status = "Error", Message = "PLC not connected" };
            }
            try
            {
                string used;
                int result = plcComm.WriteInt32ToDevicePath(path, value, out used);
                return new WriteResult { Address = path, Value = value.ToString(), Status = result == 0 ? "OK" : "Error", Message = used };
            }
            catch (Exception ex)
            {
                return new WriteResult { Address = path, Status = "Error", Message = ex.Message };
            }
        }

        public static string FormatPositionMm(int rawValue) => (rawValue / 10000.0).ToString("0.0000", CultureInfo.InvariantCulture);
        public static string FormatSpeedMm(int rawValue) => (rawValue / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        public static string FormatAxisStatus(int status)
        {
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

        public class PositioningPoint
        {
            public short OperationPattern { get; set; }
            public short ControlSystem { get; set; }
            public short AccelerationTimeNo { get; set; }
            public short DecelerationTimeNo { get; set; }
            public short PartnerAxis { get; set; }
            public short MCode { get; set; }
            public short DwellTime { get; set; }
            public int CommandSpeed { get; set; }
            public int PositioningAddress { get; set; }
            public int ArcAddress { get; set; }
        }

        public static int WritePoints(PLCCommunication plcComm, int startIO, int startPointNo, List<PositioningPoint> points)
        {
            if (plcComm == null || !plcComm.IsConnected) return -1;
            if (points == null || points.Count == 0) return -1;

            int totalWords = points.Count * 10;
            short[] sData = new short[totalWords];

            for (int i = 0; i < points.Count; i++)
            {
                int baseIndex = i * 10;
                PositioningPoint pt = points[i];

                int da1 = pt.OperationPattern & 0x03;
                int da5 = pt.PartnerAxis & 0x03;
                int da3 = pt.AccelerationTimeNo & 0x03;
                int da4 = pt.DecelerationTimeNo & 0x03;
                int da2 = pt.ControlSystem & 0xFF;
                sData[baseIndex + 0] = (short)(da1 | (da5 << 2) | (da3 << 4) | (da4 << 6) | (da2 << 8));
                
                sData[baseIndex + 1] = pt.MCode;
                sData[baseIndex + 2] = pt.DwellTime;
                sData[baseIndex + 3] = 0;
                sData[baseIndex + 4] = (short)(pt.CommandSpeed & 0xFFFF);
                sData[baseIndex + 5] = (short)((pt.CommandSpeed >> 16) & 0xFFFF);
                sData[baseIndex + 6] = (short)(pt.PositioningAddress & 0xFFFF);
                sData[baseIndex + 7] = (short)((pt.PositioningAddress >> 16) & 0xFFFF);
                sData[baseIndex + 8] = (short)(pt.ArcAddress & 0xFFFF);
                sData[baseIndex + 9] = (short)((pt.ArcAddress >> 16) & 0xFFFF);
            }

            int iAddress = 2000 + (startPointNo - 1) * 10;
            return plcComm.WriteBufferBlock(startIO, iAddress, sData, 0, sData.Length);
        }
    }
}
