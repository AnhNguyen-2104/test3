using System;
using System.Collections.Generic;
using System.Globalization;

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
        public const int OffsetMoveCode = 0;  // U0\G(base + (n-1)*10 + 0)
        public const int OffsetMCode = 1;     // U0\G(base + (n-1)*10 + 1)
        public const int OffsetDwell = 2;     // U0\G(base + (n-1)*10 + 2)
        public const int OffsetSpeed = 4;     // U0\G(base + (n-1)*10 + 4) -> 32-bit (L, H)
        public const int OffsetPosX = 6;      // U0\G(base + (n-1)*10 + 6) -> 32-bit (L, H)
        public const int OffsetCenterX = 8;   // U0\G(base + (n-1)*10 + 8) -> 32-bit (L, H)

        // Coordinate multiplier: DXF mm → QD75 0.1µm units
        public const double CoordinateMultiplier = 10000.0;
        // Speed multiplier: user mm/min → QD75 speed units
        public const int SpeedMultiplier = 100;

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
            public string MotionType       { get; set; }
            public string MCodeValue       { get; set; }
            public string Dwell            { get; set; }
            public string Speed            { get; set; }
            public string EndCoordinate    { get; set; }
            public string CenterCoordinate { get; set; }
            public double EndZ             { get; set; } // Z tọa độ đích (0 = không có Z / 2-axis)
        }

        /// <summary>
        /// Result of writing all positioning data rows.
        /// </summary>
        public class SendResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public int RowCount { get; set; }
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
            else if (s.Contains("linear3") || s.Contains("3-axis"))
                da2 = 0x0B;                                                                  // ABS_Linear3 (3-axis interpolation)
            else
                da2 = 0x0A;                                                                  // ABS_Linear2 (2-axis interpolation)

            // ── Da.5 Partner axis (b3–b2), Da.3 Acc (b5–b4), Da.4 Dec (b7–b6) ──
            int da5 = partnerAxis & 0x03;
            int da3 = accelTimeNo & 0x03;
            int da4 = decelTimeNo & 0x03;

            // Assemble: b1-b0=Da.1 | b3-b2=Da.5 | b5-b4=Da.3 | b7-b6=Da.4 | b15-b8=Da.2
            int wordValue = da1 | (da5 << 2) | (da3 << 4) | (da4 << 6) | (da2 << 8);

            return unchecked((short)wordValue);
        }


        /// <summary>
        /// Write a 16-bit value to a U0\G address via PLCCommunication.
        /// </summary>
        private static WriteResult Write16(PLCCommunication plcComm, int gAddress, short value, string label)
        {
            string devicePath = $"U0\\G{gAddress}";
            string usedMethod;
            int result = plcComm.WriteInt16ToDevicePath(devicePath, value, out usedMethod);
            return new WriteResult
            {
                Address = devicePath,
                Value = value.ToString(CultureInfo.InvariantCulture),
                Status = result == 0 ? "OK" : $"Error({result})",
                Message = $"{label}: {usedMethod}"
            };
        }

        /// <summary>
        /// Write a 32-bit value to two consecutive U0\G addresses (Low word, High word).
        /// Uses individual 16-bit writes to avoid COM marshaling issues with WriteBuffer.
        /// </summary>
        private static WriteResult Write32(PLCCommunication plcComm, int gAddress, int value, string label)
        {
            short lowWord = (short)(value & 0xFFFF);
            short highWord = (short)((value >> 16) & 0xFFFF);
            string deviceL = $"U0\\G{gAddress}";
            string deviceH = $"U0\\G{gAddress + 1}";

            string usedL, usedH;
            int rL = plcComm.WriteInt16ToDevicePath(deviceL, lowWord, out usedL);
            int rH = plcComm.WriteInt16ToDevicePath(deviceH, highWord, out usedH);

            bool ok = (rL == 0 && rH == 0);
            return new WriteResult
            {
                Address = deviceL,
                Value = $"{value} (L={lowWord},H={highWord})",
                Status = ok ? "OK" : $"Error(L={rL},H={rH})",
                Message = $"{label} 32-bit"
            };
        }

        /// <summary>
        /// Parse coordinate string "X;Y" and return X component multiplied by CoordinateMultiplier.
        /// </summary>
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

        /// <summary>
        /// Parse a string to int, return 0 if invalid.
        /// </summary>
        private static int ParseInt(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            int result;
            if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return result;
            return 0;
        }

        /// <summary>
        /// Send all positioning data rows to PLC buffer memory for a given axis.
        /// This is the core buffer write logic extracted from HandleSendCadXAsync.
        /// </summary>
        /// <param name="plcComm">Connected PLCCommunication instance.</param>
        /// <param name="axisIndex">Axis index (0=X, 1=Y, 2=Z, 3=A).</param>
        /// <param name="rows">List of positioning data rows to write.</param>
        /// <param name="writeStartNo">If true, write 1 to the Control Start No. register after all data.</param>
        /// <param name="progressCallback">Optional callback for progress reporting (0-100).</param>
        /// <returns>SendResult with all write results and overall success status.</returns>
        public static SendResult WritePositioningData(PLCCommunication plcComm, int axisIndex, List<PositioningDataRow> rows, bool writeStartNo = true, Action<int> progressCallback = null)
        {
            var result = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                result.Success = false;
                result.ErrorMessage = "PLC is not connected.";
                return result;
            }

            if (rows == null || rows.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No points to send.";
                return result;
            }

            int baseG = ProgramBaseG[axisIndex];
            int n = 1;

            foreach (var row in rows)
            {
                // Parse coordinates
                int endX = 0;
                int centerX = 0;
                bool hasEnd = TryParseCoordinateX(row.EndCoordinate, out endX);
                bool hasCenter = TryParseCoordinateX(row.CenterCoordinate, out centerX);

                // Parse M code, dwell, speed
                int mcodeVal = ParseInt(row.MCodeValue);
                int dwellVal = ParseInt(row.Dwell);
                int speedVal = ParseInt(row.Speed) * SpeedMultiplier;

                // Validate 16-bit ranges
                if (mcodeVal < short.MinValue || mcodeVal > short.MaxValue)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"Point {n}",
                        Value = mcodeVal.ToString(),
                        Status = "Error",
                        Message = $"M code out of 16-bit range: {mcodeVal}"
                    });
                    n++;
                    continue;
                }
                if (dwellVal < short.MinValue || dwellVal > short.MaxValue)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"Point {n}",
                        Value = dwellVal.ToString(),
                        Status = "Error",
                        Message = $"Dwell out of 16-bit range: {dwellVal}"
                    });
                    n++;
                    continue;
                }

                short moveCode = BuildPositioningIdentifierWord(row.MotionType);
                int blockBase = baseG + (n - 1) * Stride;

                try
                {
                    // Write Positioning identifier (16-bit)
                    var rMove = Write16(plcComm, blockBase + OffsetMoveCode, moveCode,
                        $"Posn identifier hex=0x{((ushort)moveCode):X4}");
                    result.WriteResults.Add(rMove);

                    // Write M code (16-bit)
                    var rM = Write16(plcComm, blockBase + OffsetMCode, (short)mcodeVal, "MCode");
                    result.WriteResults.Add(rM);

                    // Write Dwell (16-bit)
                    var rD = Write16(plcComm, blockBase + OffsetDwell, (short)dwellVal, "Dwell");
                    result.WriteResults.Add(rD);

                    // Write Speed (32-bit, two 16-bit words)
                    var rS = Write32(plcComm, blockBase + OffsetSpeed, speedVal, "Speed");
                    result.WriteResults.Add(rS);

                    // Write Position X (32-bit, two 16-bit words)
                    if (hasEnd)
                    {
                        var rP = Write32(plcComm, blockBase + OffsetPosX, endX, "PosX");
                        result.WriteResults.Add(rP);
                        if (!rP.Status.StartsWith("OK")) result.Success = false;
                    }

                    // Write Center X (32-bit, two 16-bit words)
                    if (hasCenter)
                    {
                        var rC = Write32(plcComm, blockBase + OffsetCenterX, centerX, "CenterX");
                        result.WriteResults.Add(rC);
                        if (!rC.Status.StartsWith("OK")) result.Success = false;
                    }
                }
                catch (Exception ex)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{blockBase}",
                        Value = string.Empty,
                        Status = "Error",
                        Message = ex.Message
                    });
                    result.Success = false;
                }

                if (progressCallback != null && n % 5 == 0)
                {
                    progressCallback((n * 100) / rows.Count);
                }

                n++;
            }

            // Write Start No. = 1 to Control base register
            if (writeStartNo)
            {
                try
                {
                    string startDevice = $"U0\\G{ControlBaseG[axisIndex]}";
                    string usedStart;
                    int rStart = plcComm.WriteInt16ToDevicePath(startDevice, 1, out usedStart);
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = startDevice,
                        Value = "1",
                        Status = rStart == 0 ? "OK" : $"Error({rStart})",
                        Message = "Start Axis " + (axisIndex + 1) + ": " + usedStart
                    });
                }
                catch (Exception ex)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{ControlBaseG[axisIndex]}",
                        Value = "1",
                        Status = "Error",
                        Message = ex.Message
                    });
                    result.Success = false;
                }
            }

            result.RowCount = rows.Count;
            return result;
        }

        /// <summary>
        /// Write positioning data for a SLAVE (interpolated follower) axis (Axis 2).
        /// Per QD75 manual: only Da.6 (positioning address) and Da.7 (arc address) are required.
        /// Da.1~Da.5 (identifier), Da.8 (speed), Da.9 (dwell) are all ignored by the module.
        /// Base address is fixed at G8000 (Axis 2 slave buffer), stride = 10 words/point.
        /// Same coordinate scaling as master axis (× CoordinateMultiplier = ×10000).
        /// </summary>
        /// <param name="plcComm">Connected PLCCommunication instance.</param>
        /// <param name="rows">List of positioning data rows. Only EndCoordinate (Y axis) and CenterCoordinate (Y) are used.</param>
        /// <param name="slaveBaseG">Buffer base address for slave axis. Default = 8000 (Axis 2).</param>
        /// <param name="progressCallback">Optional callback for progress reporting (0-100).</param>
        /// <returns>SendResult with all write results.</returns>
        public static SendResult WriteSlaveAxisData(PLCCommunication plcComm, List<PositioningDataRow> rows, int slaveBaseG = 8000, Action<int> progressCallback = null)
        {
            var result = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                result.Success = false;
                result.ErrorMessage = "PLC is not connected.";
                return result;
            }

            if (rows == null || rows.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No points to send.";
                return result;
            }

            int n = 1;
            foreach (var row in rows)
            {
                // Parse Y coordinate from EndCoordinate ("X;Y" format) → Da.6 position
                int endY = 0;
                bool hasEnd = TryParseCoordinateY(row.EndCoordinate, out endY);

                // Parse Y coordinate from CenterCoordinate → Da.7 arc address
                int centerY = 0;
                bool hasCenter = TryParseCoordinateY(row.CenterCoordinate, out centerY);

                int blockBase = slaveBaseG + (n - 1) * Stride;

                try
                {
                    // Trục 2 (Slave) chỉ quan tâm đến toạ độ đích và tâm cung tròn.
                    // Các thông số khác (Da.1~Da.5, Da.8, Da.9) KHÔNG ĐƯỢC GHI để tránh xung đột cấu hình.

                    // Da.6 Positioning address (32-bit) — U0\G(8000 + (n-1)*10 + 6)
                    if (hasEnd)
                    {
                        var rP = Write32(plcComm, blockBase + OffsetPosX, endY, "Slave PosY");
                        result.WriteResults.Add(rP);
                        if (!rP.Status.StartsWith("OK")) result.Success = false;
                    }

                    // Da.7 Arc address (32-bit) — U0\G(8000 + (n-1)*10 + 8)
                    if (hasCenter)
                    {
                        var rC = Write32(plcComm, blockBase + OffsetCenterX, centerY, "Slave CenterY");
                        result.WriteResults.Add(rC);
                        if (!rC.Status.StartsWith("OK")) result.Success = false;
                    }
                }
                catch (Exception ex)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{blockBase + OffsetPosX}",
                        Value   = string.Empty,
                        Status  = "Error",
                        Message = ex.Message
                    });
                    result.Success = false;
                }

                if (progressCallback != null && n % 5 == 0)
                {
                    progressCallback((n * 100) / rows.Count);
                }

                n++;
            }

            result.RowCount = rows.Count;
            return result;
        }

        /// <summary>
        /// Parse coordinate string "X;Y" and return Y component multiplied by CoordinateMultiplier.
        /// Used for slave axis where only the Y (second axis) coordinate matters.
        /// </summary>
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


        /// <summary>
        /// Write a single 32-bit value to a device path and return a WriteResult.
        /// Used for telemetry / manual buffer writes.
        /// </summary>
        public static WriteResult WriteBufferValue(PLCCommunication plcComm, string path, int value)
        {
            if (plcComm == null || !plcComm.IsConnected)
            {
                return new WriteResult
                {
                    Address = path,
                    Value = value.ToString(CultureInfo.InvariantCulture),
                    Status = "Error",
                    Message = "PLC is not connected."
                };
            }

            try
            {
                string used;
                int result = plcComm.WriteInt32ToDevicePath(path, value, out used);
                return new WriteResult
                {
                    Address = path,
                    Value = value.ToString(CultureInfo.InvariantCulture),
                    Status = result == 0 ? "OK" : $"Error({result})",
                    Message = used
                };
            }
            catch (Exception ex)
            {
                return new WriteResult
                {
                    Address = path,
                    Value = value.ToString(CultureInfo.InvariantCulture),
                    Status = "Error",
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// Convert raw buffer value to mm (Pr.1=0: raw × 10⁻¹ µm = raw/10000 mm)
        /// </summary>
        public static string FormatPositionMm(int rawValue)
        {
            double mm = rawValue / 10000.0;
            return mm.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Format raw speed value to mm/min (monitor speed: 0.01 mm/min)
        /// </summary>
        public static string FormatSpeedMm(int rawValue)
        {
            double mmMin = rawValue / 100.0;
            return mmMin.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Format axis status code to human-readable string.
        /// </summary>
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
        /// <summary>
        /// Send all positioning data rows to PLC using a single bulk WriteBuffer call.
        /// This is significantly faster than individual writes for large point sets.
        /// Rows with EndZ != 0 use Da.2=0x0B (ABS_Linear3) and also write Z to Axis 3 buffer (G14000+).
        /// </summary>
        public static SendResult WritePositioningDataBulk(PLCCommunication plcComm, int axisIndex, List<PositioningDataRow> rows, bool writeStartNo = true)
        {
            var result = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                result.Success = false;
                result.ErrorMessage = "PLC is not connected.";
                return result;
            }

            if (rows == null || rows.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No points to send.";
                return result;
            }

            int baseG = ProgramBaseG[axisIndex];
            int totalWords = rows.Count * Stride;
            short[] bulkData = new short[totalWords];

            // Axis 3 (Z) buffer — chỉ ghi nếu có ít nhất 1 dòng có Z
            bool hasAnyZ = false;
            short[] zBulkData = new short[totalWords]; // zero-init = không có Z

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int blockOffset = i * Stride;

                // 1. Positioning Identifier (16-bit)
                // Nếu có Z → dùng Linear3 (Da.2=0x0B), ngược lại dùng MotionType gốc
                string effectiveMotionType = row.MotionType;
                bool hasZ = Math.Abs(row.EndZ) > 1e-9;
                if (hasZ && (row.MotionType.Contains("Line") || row.MotionType.Contains("Rapid")))
                {
                    // Thay thế "Line" bằng "Linear3" để BuildPositioningIdentifierWord chọn Da.2=0x0B
                    effectiveMotionType = row.MotionType.Replace("Line", "Linear3");
                    hasAnyZ = true;
                }
                short moveCode = BuildPositioningIdentifierWord(effectiveMotionType);
                bulkData[blockOffset + OffsetMoveCode] = moveCode;

                // 2. M Code (16-bit)
                int mcodeVal = ParseInt(row.MCodeValue);
                bulkData[blockOffset + OffsetMCode] = (short)mcodeVal;

                // 3. Dwell Time (16-bit)
                int dwellVal = ParseInt(row.Dwell);
                bulkData[blockOffset + OffsetDwell] = (short)dwellVal;

                // 4. Command Speed (32-bit -> 2 words)
                int speedVal = ParseInt(row.Speed) * SpeedMultiplier;
                bulkData[blockOffset + OffsetSpeed]     = (short)(speedVal & 0xFFFF);
                bulkData[blockOffset + OffsetSpeed + 1] = (short)((speedVal >> 16) & 0xFFFF);

                // 5. Position X (32-bit -> 2 words)
                int endX = 0;
                if (TryParseCoordinateX(row.EndCoordinate, out endX))
                {
                    bulkData[blockOffset + OffsetPosX]     = (short)(endX & 0xFFFF);
                    bulkData[blockOffset + OffsetPosX + 1] = (short)((endX >> 16) & 0xFFFF);
                }

                // 6. Center X (32-bit -> 2 words)
                int centerX = 0;
                if (TryParseCoordinateX(row.CenterCoordinate, out centerX))
                {
                    bulkData[blockOffset + OffsetCenterX]     = (short)(centerX & 0xFFFF);
                    bulkData[blockOffset + OffsetCenterX + 1] = (short)((centerX >> 16) & 0xFFFF);
                }

                // 7. Axis 3 (Z) — Da.1+Da.2 + Da.6 position
                if (hasZ)
                {
                    // Da.1+Da.2 phải khớp với master: Linear3 (Da.2=0x0B)
                    short zMoveCode = BuildPositioningIdentifierWord(effectiveMotionType);
                    zBulkData[blockOffset + OffsetMoveCode] = zMoveCode;

                    // Da.6 Positioning address Z (32-bit)
                    int endZScaled = Convert.ToInt32(Math.Round(row.EndZ * CoordinateMultiplier));
                    zBulkData[blockOffset + OffsetPosX]     = (short)(endZScaled & 0xFFFF);
                    zBulkData[blockOffset + OffsetPosX + 1] = (short)((endZScaled >> 16) & 0xFFFF);
                }
            }

            try
            {
                int res = plcComm.WriteBuffer(0, baseG, bulkData);
                if (res != 0)
                {
                    result.Success = false;
                    result.ErrorMessage = $"WriteBuffer failed with error code: {res}";
                }
                else
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{baseG} to U0\\G{baseG + totalWords - 1}",
                        Value   = $"Bulk write {rows.Count} points",
                        Status  = "OK",
                        Message = "Bulk WriteBuffer (Axis X) successful"
                    });
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            // Ghi Axis 3 (Z) nếu có dòng nào có Z
            if (result.Success && hasAnyZ)
            {
                int zBaseG = ProgramBaseG[2]; // Axis 3 = G14000
                try
                {
                    int res = plcComm.WriteBuffer(0, zBaseG, zBulkData);
                    if (res != 0)
                    {
                        result.WriteResults.Add(new WriteResult
                        {
                            Address = $"U0\\G{zBaseG}",
                            Value   = "Z axis data",
                            Status  = $"Error({res})",
                            Message = "WriteBuffer Axis 3 (Z) failed"
                        });
                        // Không fail toàn bộ — X/Y đã ghi thành công
                    }
                    else
                    {
                        result.WriteResults.Add(new WriteResult
                        {
                            Address = $"U0\\G{zBaseG} to U0\\G{zBaseG + totalWords - 1}",
                            Value   = $"Bulk write {rows.Count} Z points",
                            Status  = "OK",
                            Message = "Bulk WriteBuffer (Axis Z) successful"
                        });
                    }
                }
                catch (Exception ex)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{zBaseG}",
                        Value   = "Z axis data",
                        Status  = "Error",
                        Message = ex.Message
                    });
                }
            }

            // Write Start No. = 1 to Control base register
            if (writeStartNo && result.Success)
            {
                try
                {
                    string startDevice = $"U0\\G{ControlBaseG[axisIndex]}";
                    string usedStart;
                    int rStart = plcComm.WriteInt16ToDevicePath(startDevice, 1, out usedStart);
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = startDevice,
                        Value = "1",
                        Status = rStart == 0 ? "OK" : $"Error({rStart})",
                        Message = "Start Axis " + (axisIndex + 1) + ": " + usedStart
                    });
                }
                catch (Exception ex)
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{ControlBaseG[axisIndex]}",
                        Value = "1",
                        Status = "Error",
                        Message = ex.Message
                    });
                    result.Success = false;
                }
            }

            result.RowCount = rows.Count;
            return result;
        }

        /// <summary>
        /// Write slave axis (Y) positioning data using a single bulk WriteBuffer call.
        /// Ghi Da.1 (operation pattern) và Da.2 (control system) đồng bộ với master axis.
        /// Với dòng có Z: Da.2 = 0x0B (ABS_Linear3). Không có Z: Da.2 = 0x0A (ABS_Linear2).
        /// </summary>
        public static SendResult WriteSlaveAxisDataBulk(PLCCommunication plcComm, List<PositioningDataRow> rows, int slaveBaseG = 8000)
        {
            var result = new SendResult { Success = true };

            if (plcComm == null || !plcComm.IsConnected)
            {
                result.Success = false;
                result.ErrorMessage = "PLC is not connected.";
                return result;
            }

            if (rows == null || rows.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "No points to send.";
                return result;
            }

            int totalWords = rows.Count * Stride;
            short[] bulkData = new short[totalWords];

            for (int i = 0; i < rows.Count; i++)
            {
                int blockOffset = i * Stride;
                var row = rows[i];

                // Da.1 + Da.2: phải khớp với master axis
                // Nếu dòng có Z → dùng Linear3 (Da.2=0x0B), ngược lại dùng MotionType gốc
                bool hasZ = Math.Abs(row.EndZ) > 1e-9;
                string effectiveMotionType = row.MotionType;
                if (hasZ && (row.MotionType.Contains("Line") || row.MotionType.Contains("Rapid")))
                    effectiveMotionType = row.MotionType.Replace("Line", "Linear3");

                short moveCode = BuildPositioningIdentifierWord(effectiveMotionType);
                bulkData[blockOffset + OffsetMoveCode] = moveCode;

                // Da.6 Positioning address Y (32-bit) — offset 6 & 7
                int endY = 0;
                if (TryParseCoordinateY(row.EndCoordinate, out endY))
                {
                    bulkData[blockOffset + OffsetPosX]     = (short)(endY & 0xFFFF);
                    bulkData[blockOffset + OffsetPosX + 1] = (short)((endY >> 16) & 0xFFFF);
                }

                // Da.7 Arc address Y (32-bit) — offset 8 & 9
                int centerY = 0;
                if (TryParseCoordinateY(row.CenterCoordinate, out centerY))
                {
                    bulkData[blockOffset + OffsetCenterX]     = (short)(centerY & 0xFFFF);
                    bulkData[blockOffset + OffsetCenterX + 1] = (short)((centerY >> 16) & 0xFFFF);
                }
            }

            try
            {
                int res = plcComm.WriteBuffer(0, slaveBaseG, bulkData);
                if (res != 0)
                {
                    result.Success = false;
                    result.ErrorMessage = $"WriteBuffer (slave Y) failed: {res}";
                }
                else
                {
                    result.WriteResults.Add(new WriteResult
                    {
                        Address = $"U0\\G{slaveBaseG} to U0\\G{slaveBaseG + totalWords - 1}",
                        Value   = $"Bulk write {rows.Count} slave points",
                        Status  = "OK",
                        Message = "Bulk WriteBuffer (slave Y) successful"
                    });
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            result.RowCount = rows.Count;
            return result;
        }

        /// <summary>
        /// Manual start axis
        /// </summary>
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

        /// <summary>
        /// Cấu trúc dữ liệu cho một điểm định vị (Point) của module QD75/LD75.
        /// C# sẽ tự động gộp các cấu hình rời rạc này thành chuẩn 10 Word của QD75.
        /// </summary>
        public class PositioningPoint
        {
            // Cấu hình (Sẽ được C# tự động gộp bit vào Offset 0)
            public short OperationPattern { get; set; }   // Da.1 (VD: 0, 1, 3)
            public short ControlSystem { get; set; }      // Da.2 (VD: 10, 15, 16)
            public short AccelerationTimeNo { get; set; } // Da.3 (0-3)
            public short DecelerationTimeNo { get; set; } // Da.4 (0-3)
            public short PartnerAxis { get; set; }        // Da.5 (Trục nội suy: 0=Trục 1, 1=Trục 2...)

            // Các thông số rời
            public short MCode { get; set; }              // Offset 1
            public short DwellTime { get; set; }          // Offset 2
            
            // Dữ liệu 32-bit (Bố trí chuẩn xác theo QD75 Manual)
            public int CommandSpeed { get; set; }         // Offset 4 & 5 (Tốc độ)
            public int PositioningAddress { get; set; }   // Offset 6 & 7 (Toạ độ)
            public int ArcAddress { get; set; }           // Offset 8 & 9 (Tâm cung tròn)
        }

        /// <summary>
        /// Hàm gộp (batch) dữ liệu và ghi cùng lúc xuống Buffer Memory.
        /// Đã fix: Tự động gộp Da.1 -> Da.5 vào Offset 0 và mapping đúng cấu trúc QD75.
        /// </summary>
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

                // 1. Tự động gộp Da.1 đến Da.5 vào Offset 0 theo chuẩn QD75
                int da1 = pt.OperationPattern & 0x03;
                int da5 = pt.PartnerAxis & 0x03;
                int da3 = pt.AccelerationTimeNo & 0x03;
                int da4 = pt.DecelerationTimeNo & 0x03;
                int da2 = pt.ControlSystem & 0xFF;
                sData[baseIndex + 0] = (short)(da1 | (da5 << 2) | (da3 << 4) | (da4 << 6) | (da2 << 8));
                
                // 2. Offset 1: M Code
                sData[baseIndex + 1] = pt.MCode;
                
                // 3. Offset 2: Dwell Time
                sData[baseIndex + 2] = pt.DwellTime;
                
                // 4. Offset 3: Dự phòng (thường là 0)
                sData[baseIndex + 3] = 0;

                // 5. Offset 4 & 5: Command Speed (Da.8)
                sData[baseIndex + 4] = (short)(pt.CommandSpeed & 0xFFFF);
                sData[baseIndex + 5] = (short)((pt.CommandSpeed >> 16) & 0xFFFF);

                // 6. Offset 6 & 7: Positioning Address (Da.6)
                sData[baseIndex + 6] = (short)(pt.PositioningAddress & 0xFFFF);
                sData[baseIndex + 7] = (short)((pt.PositioningAddress >> 16) & 0xFFFF);

                // 7. Offset 8 & 9: Arc Address (Da.7)
                sData[baseIndex + 8] = (short)(pt.ArcAddress & 0xFFFF);
                sData[baseIndex + 9] = (short)((pt.ArcAddress >> 16) & 0xFFFF);
            }

            // Địa chỉ Buffer Memory cho điểm số n bắt đầu từ 2000
            int iAddress = 2000 + (startPointNo - 1) * 10;

            // Ghi xuống PLC
            return plcComm.WriteBuffer(startIO, iAddress, sData);
        }
    }
}
