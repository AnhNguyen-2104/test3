using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gcode.Utils;
using Gcode.Utils.Entity;

namespace DACDT_2026
{
    public sealed class GcodeCoordinateService
    {
        private const double Epsilon = 0.000001;

        public CadDocumentService.CadLoadResult LoadAsCad(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("G-code path is empty.", nameof(filePath));

            string fullPath = Path.GetFullPath(filePath);
            ParseResult parsed = ReadGcode(File.ReadLines(fullPath));

            return new CadDocumentService.CadLoadResult
            {
                FilePath = fullPath,
                DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
                FileName = Path.GetFileName(fullPath),
                Bounds = BuildBounds(parsed.Primitives, parsed.Points),
                Primitives = parsed.Primitives,
                Points = BuildPointRows(parsed.Points)
            };
        }

        public CadDocumentService.CadLoadResult LoadAsCadFromText(string text, string fallbackFilePath = null)
        {
            var lines = text?.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None) ?? Array.Empty<string>();
            ParseResult parsed = ReadGcode(lines);

            return new CadDocumentService.CadLoadResult
            {
                FilePath = fallbackFilePath ?? string.Empty,
                DirectoryPath = fallbackFilePath != null ? Path.GetDirectoryName(fallbackFilePath) : string.Empty,
                FileName = fallbackFilePath != null ? Path.GetFileName(fallbackFilePath) : "Untitled",
                Bounds = BuildBounds(parsed.Primitives, parsed.Points),
                Primitives = parsed.Primitives,
                Points = BuildPointRows(parsed.Points)
            };
        }

        private static ParseResult ReadGcode(IEnumerable<string> lines)
        {
            var result = new ParseResult();
            double currentX = 0.0;
            double currentY = 0.0;
            double currentZ = 0.0;
            double unitScale = 1.0;
            bool absoluteMode = true;
            bool hasCurrentPoint = false;
            int modalMotion = 1;
            double? modalF = null;      // Modal F cho G1/G2/G3 — chỉ cập nhật khi không phải G0
            // M code KHÔNG modal: chỉ áp dụng cho dòng có M, không lan sang dòng kế tiếp.

            foreach (string rawLine in lines)
            {
                string normalized = NormalizeLine(rawLine);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                GcodeCommandFrame frame;
                try
                {
                    frame = GcodeParser.ToGCode(normalized);
                }
                catch
                {
                    continue;
                }

                if (frame == null)
                    continue;

                if (frame.G.HasValue)
                {
                    if (frame.G.Value == 20)
                    {
                        unitScale = 25.4;
                        continue;
                    }

                    if (frame.G.Value == 21)
                    {
                        unitScale = 1.0;
                        continue;
                    }

                    if (frame.G.Value == 90)
                    {
                        absoluteMode = true;
                        continue;
                    }

                    if (frame.G.Value == 91)
                    {
                        absoluteMode = false;
                        continue;
                    }

                    // G54–G59: chọn hệ tọa độ phôi (work coordinate system).
                    // Hiện tại offset gốc phôi được set qua UI (offsetX/offsetY), nên G54–G59
                    // chỉ skip an toàn để tránh parser hiểu nhầm là motion modal.
                    if (frame.G.Value >= 54 && frame.G.Value <= 59)
                        continue;

                    if (frame.G.Value >= 0 && frame.G.Value <= 3)
                        modalMotion = frame.G.Value;
                }

                int motion = frame.G.HasValue && frame.G.Value >= 0 && frame.G.Value <= 3
                    ? frame.G.Value
                    : modalMotion;

                double? currentF = frame.F.HasValue ? frame.F.Value : (double?)null;
                // modalF chỉ cập nhật khi motion là G1/G2/G3 — G0 không ảnh hưởng modal feed
                if (frame.F.HasValue && motion != 0) modalF = frame.F.Value;
                // M code: KHÔNG modal — chỉ dùng cho dòng hiện tại
                int? lineM = frame.M.HasValue ? (int?)frame.M.Value : null;

                bool hasCoordinate = frame.X.HasValue || frame.Y.HasValue || frame.Z.HasValue;
                bool isArc = motion == 2 || motion == 3;
                bool hasArcCenterData = frame.I.HasValue || frame.J.HasValue || frame.R.HasValue;
                bool hasOtherData = frame.M.HasValue || frame.F.HasValue || frame.P.HasValue || frame.S.HasValue;

                if (!hasCoordinate && !(isArc && hasArcCenterData) && !hasOtherData)
                    continue;

                double nextX = ResolveAxis(frame.X, currentX, unitScale, absoluteMode);
                double nextY = ResolveAxis(frame.Y, currentY, unitScale, absoluteMode);
                double nextZ = ResolveAxis(frame.Z, currentZ, unitScale, absoluteMode);
                var nextPoint = new CadDocumentService.CadCoordinate(nextX, nextY, nextZ);

                if (!hasCurrentPoint)
                {
                    // Lệnh đầu tiên: tạo primitive di chuyển từ gốc (0,0,0) đến điểm đầu.
                    // Không skip — cần ghi nhận lệnh G00 đầu tiên để process table có đủ thông tin.
                    var originPoint = new CadDocumentService.CadCoordinate(0, 0, 0);
                    bool isFirstRapid = motion == 0;
                    
                    // Chỉ tạo primitive nếu điểm đích khác gốc (0,0,0)
                    if (Math.Abs(nextX) > Epsilon || Math.Abs(nextY) > Epsilon || Math.Abs(nextZ) > Epsilon)
                    {
                        result.Primitives.Add(new CadDocumentService.CadPrimitiveData
                        {
                            SourceType = isFirstRapid ? "Line (G0 Rapid)" : "Line (G1)",
                            Points = new List<CadDocumentService.CadCoordinate>
                            {
                                new CadDocumentService.CadCoordinate(0, 0, 0),
                                new CadDocumentService.CadCoordinate(nextX, nextY, nextZ)
                            },
                            Center = null,
                            IsCw = false,
                            IsCircle = false,
                            Speed = (isFirstRapid || !modalF.HasValue) ? null : modalF.Value.ToString(CultureInfo.InvariantCulture),
                            MCodeValue = lineM?.ToString(CultureInfo.InvariantCulture),
                            Dwell = null
                        });
                        AddPoint(result.Points, originPoint);
                        AddPoint(result.Points, nextPoint);
                    }

                    currentX = nextX;
                    currentY = nextY;
                    currentZ = nextZ;
                    hasCurrentPoint = true;
                    continue;
                }

                var startPoint = new CadDocumentService.CadCoordinate(currentX, currentY, currentZ);

                if (isArc)
                    AddArcPrimitive(result, frame, startPoint, nextPoint, motion == 2, unitScale, modalF, lineM, frame.P);
                else if (!AreClose(startPoint, nextPoint))
                {
                    bool isRapid = motion == 0;
                    // G0 Rapid: không lưu speed từ file — speed sẽ được gán từ rapidSpeed khi build ProcessRow
                    double? lineSpeed = isRapid ? (double?)null : modalF;
                    AddLinePrimitive(result, startPoint, nextPoint, isRapid, lineSpeed, lineM, frame.P);
                }
                else if (lineM.HasValue && result.Primitives.Count > 0)
                {
                    // Dòng M code đứng riêng (M00/M02/M05/M30...) không có chuyển động.
                    // Gán M code vào primitive cuối cùng để vẫn được gửi xuống PLC (Da.10).
                    var lastPrim = result.Primitives[result.Primitives.Count - 1];
                    lastPrim.MCodeValue = lineM.Value.ToString(CultureInfo.InvariantCulture);
                }

                currentX = nextX;
                currentY = nextY;
                currentZ = nextZ;
            }

            return result;
        }

        private static void AddLinePrimitive(
            ParseResult result,
            CadDocumentService.CadCoordinate start,
            CadDocumentService.CadCoordinate end,
            bool isRapid,
            double? speed = null,
            int? mcode = null,
            double? dwell = null)
        {
            result.Primitives.Add(new CadDocumentService.CadPrimitiveData
            {
                SourceType = isRapid ? "Line (G0 Rapid)" : "Line (G1)",
                Points = new List<CadDocumentService.CadCoordinate>
                {
                    new CadDocumentService.CadCoordinate(start.X, start.Y, start.Z),
                    new CadDocumentService.CadCoordinate(end.X, end.Y, end.Z)
                },
                Center = null,
                IsCw = false,
                IsCircle = false,
                Speed = speed?.ToString(CultureInfo.InvariantCulture),
                MCodeValue = mcode?.ToString(CultureInfo.InvariantCulture),
                Dwell = dwell?.ToString(CultureInfo.InvariantCulture)
            });

            AddPoint(result.Points, start);
            AddPoint(result.Points, end);
        }

        private static void AddArcPrimitive(
            ParseResult result,
            GcodeCommandFrame frame,
            CadDocumentService.CadCoordinate start,
            CadDocumentService.CadCoordinate end,
            bool isCw,
            double unitScale,
            double? speed = null,
            int? mcode = null,
            double? dwell = null)
        {
            double centerX;
            double centerY;
            if (!TryGetArcCenter(frame, start.X, start.Y, end.X, end.Y, unitScale, isCw, out centerX, out centerY))
                return;

            var center = new CadDocumentService.CadCoordinate(centerX, centerY, start.Z);
            List<CadDocumentService.CadCoordinate> arcPoints =
                SampleArc(start.X, start.Y, end.X, end.Y, centerX, centerY, isCw, start.Z, end.Z);

            result.Primitives.Add(new CadDocumentService.CadPrimitiveData
            {
                SourceType = "Arc",
                Points = arcPoints,
                Center = center,
                IsCw = isCw,
                IsCircle = AreClose(start, end),
                Speed = speed?.ToString(CultureInfo.InvariantCulture),
                MCodeValue = mcode?.ToString(CultureInfo.InvariantCulture),
                Dwell = dwell?.ToString(CultureInfo.InvariantCulture)
            });

            AddPoint(result.Points, start);
            AddPoint(result.Points, end);
        }

        private static bool TryGetArcCenter(
            GcodeCommandFrame frame,
            double startX,
            double startY,
            double endX,
            double endY,
            double unitScale,
            bool isCw,
            out double centerX,
            out double centerY)
        {
            if (frame.I.HasValue || frame.J.HasValue)
            {
                centerX = startX + (frame.I ?? 0.0) * unitScale;
                centerY = startY + (frame.J ?? 0.0) * unitScale;
                return true;
            }

            if (frame.R.HasValue)
                return TryGetCenterFromRadius(startX, startY, endX, endY, frame.R.Value * unitScale, isCw, out centerX, out centerY);

            centerX = 0.0;
            centerY = 0.0;
            return false;
        }

        private static bool TryGetCenterFromRadius(
            double startX,
            double startY,
            double endX,
            double endY,
            double radiusValue,
            bool isCw,
            out double centerX,
            out double centerY)
        {
            double radius = Math.Abs(radiusValue);
            double dx = endX - startX;
            double dy = endY - startY;
            double chord = Math.Sqrt(dx * dx + dy * dy);

            if (radius < Epsilon || chord < Epsilon || chord > radius * 2.0 + Epsilon)
            {
                centerX = 0.0;
                centerY = 0.0;
                return false;
            }

            double midX = (startX + endX) / 2.0;
            double midY = (startY + endY) / 2.0;
            double height = Math.Sqrt(Math.Max(0.0, radius * radius - chord * chord / 4.0));
            double ux = -dy / chord;
            double uy = dx / chord;

            double c1x = midX + ux * height;
            double c1y = midY + uy * height;
            double c2x = midX - ux * height;
            double c2y = midY - uy * height;
            bool wantMajorArc = radiusValue < 0.0;
            bool c1IsMajorArc = GetArcSweep(startX, startY, endX, endY, c1x, c1y, isCw) > Math.PI + Epsilon;

            if (c1IsMajorArc == wantMajorArc)
            {
                centerX = c1x;
                centerY = c1y;
            }
            else
            {
                centerX = c2x;
                centerY = c2y;
            }

            return true;
        }

        private static List<CadDocumentService.CadCoordinate> SampleArc(
            double startX,
            double startY,
            double endX,
            double endY,
            double centerX,
            double centerY,
            bool isCw,
            double startZ = 0.0,
            double endZ = 0.0)
        {
            double startAngle = Math.Atan2(startY - centerY, startX - centerX);
            double endAngle = Math.Atan2(endY - centerY, endX - centerX);
            double sweep = isCw
                ? NormalizePositiveRadians(startAngle - endAngle)
                : NormalizePositiveRadians(endAngle - startAngle);

            if (sweep < Epsilon)
                sweep = Math.PI * 2.0;

            double radius = Math.Sqrt((startX - centerX) * (startX - centerX) + (startY - centerY) * (startY - centerY));
            int steps = Math.Max(72, (int)Math.Ceiling(sweep / (Math.PI / 90.0)));
            var points = new List<CadDocumentService.CadCoordinate>();

            for (int i = 0; i <= steps; i++)
            {
                double angle = isCw
                    ? startAngle - sweep * i / steps
                    : startAngle + sweep * i / steps;
                double currentZ = startZ + (endZ - startZ) * i / steps;
                points.Add(new CadDocumentService.CadCoordinate(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle),
                    currentZ));
            }

            return points;
        }

        private static double GetArcSweep(
            double startX,
            double startY,
            double endX,
            double endY,
            double centerX,
            double centerY,
            bool isCw)
        {
            double startAngle = Math.Atan2(startY - centerY, startX - centerX);
            double endAngle = Math.Atan2(endY - centerY, endX - centerX);
            return isCw
                ? NormalizePositiveRadians(startAngle - endAngle)
                : NormalizePositiveRadians(endAngle - startAngle);
        }

        private static double NormalizePositiveRadians(double value)
        {
            double full = Math.PI * 2.0;
            while (value < 0.0) value += full;
            while (value >= full) value -= full;
            return value;
        }

        private static List<CadDocumentService.CadPointData> BuildPointRows(
            List<CadDocumentService.CadCoordinate> points)
        {
            var rows = new List<CadDocumentService.CadPointData>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var point in points)
            {
                string key = MakePointKey(point.X, point.Y, point.Z);
                if (!seen.Add(key))
                    continue;

                rows.Add(new CadDocumentService.CadPointData
                {
                    Index = rows.Count + 1,
                    LineType = "G-code point",
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    XDisplay = FormatNumber(point.X),
                    YDisplay = FormatNumber(point.Y),
                    ZDisplay = FormatNumber(point.Z),
                    Key = key
                });
            }

            return rows;
        }

        private static CadDocumentService.CadBounds BuildBounds(
            List<CadDocumentService.CadPrimitiveData> primitives,
            List<CadDocumentService.CadCoordinate> points)
        {
            var allPoints = new List<CadDocumentService.CadCoordinate>();
            foreach (var primitive in primitives)
            {
                if (primitive.Points != null)
                    allPoints.AddRange(primitive.Points);
            }
            allPoints.AddRange(points);

            if (allPoints.Count == 0)
            {
                return new CadDocumentService.CadBounds
                {
                    Left = 0.0,
                    Top = 0.0,
                    Right = 100.0,
                    Bottom = 100.0,
                    Width = 100.0,
                    Height = 100.0,
                    MinZ = 0.0,
                    MaxZ = 0.0
                };
            }

            double left = allPoints.Min(p => p.X);
            double top = allPoints.Min(p => p.Y);
            double right = allPoints.Max(p => p.X);
            double bottom = allPoints.Max(p => p.Y);
            double minZ = allPoints.Min(p => p.Z);
            double maxZ = allPoints.Max(p => p.Z);

            return new CadDocumentService.CadBounds
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom,
                Width = Math.Max(right - left, 1.0),
                Height = Math.Max(bottom - top, 1.0),
                MinZ = minZ,
                MaxZ = maxZ
            };
        }

        private static double ResolveAxis(double? value, double current, double unitScale, bool absoluteMode)
        {
            if (!value.HasValue)
                return current;

            double scaled = value.Value * unitScale;
            return absoluteMode ? scaled : current + scaled;
        }

        private static void AddPoint(List<CadDocumentService.CadCoordinate> points, CadDocumentService.CadCoordinate point)
        {
            if (points.Count == 0 || !AreClose(points[points.Count - 1], point))
                points.Add(new CadDocumentService.CadCoordinate(point.X, point.Y, point.Z));
        }

        private static string NormalizeLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                return string.Empty;

            string line = rawLine;

            // Strip checksum (*nn ở cuối dòng)
            int checksumIndex = line.IndexOf('*');
            if (checksumIndex >= 0)
                line = line.Substring(0, checksumIndex);

            // Strip line comment (; ...) — phần sau dấu chấm phẩy là comment, bỏ qua
            // để tránh parser đọc nhầm "M03" trong comment thành M code thật.
            int semicolonIndex = line.IndexOf(';');
            if (semicolonIndex >= 0)
                line = line.Substring(0, semicolonIndex);

            return StripParentheses(line).Trim();
        }

        private static string StripParentheses(string value)
        {
            var builder = new StringBuilder(value.Length);
            int depth = 0;

            foreach (char ch in value)
            {
                if (ch == '(')
                {
                    depth++;
                    continue;
                }

                if (ch == ')' && depth > 0)
                {
                    depth--;
                    continue;
                }

                if (depth == 0)
                    builder.Append(ch);
            }

            return builder.ToString();
        }

        private static bool AreClose(
            CadDocumentService.CadCoordinate first,
            CadDocumentService.CadCoordinate second)
            => Math.Abs(first.X - second.X) < Epsilon
                && Math.Abs(first.Y - second.Y) < Epsilon
                && Math.Abs(first.Z - second.Z) < Epsilon;

        private static string MakePointKey(double x, double y, double z)
            => string.Format(CultureInfo.InvariantCulture, "{0:0.###}|{1:0.###}|{2:0.###}", x, y, z);

        private static string FormatNumber(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        private sealed class ParseResult
        {
            public List<CadDocumentService.CadPrimitiveData> Primitives { get; } =
                new List<CadDocumentService.CadPrimitiveData>();

            public List<CadDocumentService.CadCoordinate> Points { get; } =
                new List<CadDocumentService.CadCoordinate>();
        }
    }
}
