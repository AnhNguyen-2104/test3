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
    public sealed class GcodeDocumentService
    {
        private const double Epsilon = 0.000001;

        public GcodeLoadResult Load(string filePath, string defaultSpeed)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("G-code path is empty.", nameof(filePath));
            }

            string fullPath = Path.GetFullPath(filePath);
            var context = new GcodeExtractionContext(fullPath, defaultSpeed);

            foreach (string rawLine in File.ReadLines(fullPath))
            {
                context.ReadLine(rawLine);
            }

            context.FinalizeRows();
            return context.BuildResult();
        }

        public sealed class GcodeLoadResult
        {
            public CadDocumentService.CadLoadResult CadDocument { get; set; }
            public List<GcodeProcessRowData> ProcessRows { get; set; }
            public int ParsedLineCount { get; set; }
            public int MovementCount { get; set; }
            public List<string> Warnings { get; set; }
        }

        public sealed class GcodeProcessRowData
        {
            public string MotionType { get; set; }
            public string MCodeValue { get; set; }
            public string Dwell { get; set; }
            public string Speed { get; set; }
            public string EndCoordinate { get; set; }
            public string CenterCoordinate { get; set; }
        }

        private sealed class GcodeExtractionContext
        {
            private readonly string filePath;
            private readonly string fileName;
            private readonly string directoryPath;
            private readonly string defaultSpeed;
            private readonly List<CadDocumentService.CadPrimitiveData> primitives
                = new List<CadDocumentService.CadPrimitiveData>();
            private readonly List<CadDocumentService.CadPointData> points
                = new List<CadDocumentService.CadPointData>();
            private readonly Dictionary<string, CadDocumentService.CadPointData> pointMap
                = new Dictionary<string, CadDocumentService.CadPointData>(StringComparer.OrdinalIgnoreCase);
            private readonly List<GcodeMotionBuilder> motions = new List<GcodeMotionBuilder>();
            private readonly List<string> warnings = new List<string>();

            private int lineNumber;
            private int parsedLineCount;
            private double currentX;
            private double currentY;
            private double currentZ;
            private double unitScale = 1.0;
            private bool absoluteMode = true;
            private int modalMotionG = 1;
            private string currentSpeed;
            private string pendingMCode;
            private string pendingDwell;
            private double minX = double.MaxValue;
            private double minY = double.MaxValue;
            private double maxX = double.MinValue;
            private double maxY = double.MinValue;

            public GcodeExtractionContext(string filePath, string defaultSpeed)
            {
                this.filePath = filePath;
                fileName = Path.GetFileName(filePath);
                directoryPath = Path.GetDirectoryName(filePath) ?? string.Empty;
                this.defaultSpeed = string.IsNullOrWhiteSpace(defaultSpeed) ? "1000" : defaultSpeed;
                currentSpeed = this.defaultSpeed;
                IncludeBounds(0, 0);
            }

            public void ReadLine(string rawLine)
            {
                lineNumber++;
                string normalized = NormalizeRawLine(rawLine);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                GcodeCommandFrame frame;
                try
                {
                    frame = GcodeParser.ToGCode(normalized);
                }
                catch (Exception ex)
                {
                    warnings.Add("Line " + lineNumber.ToString(CultureInfo.InvariantCulture)
                        + ": " + ex.Message);
                    return;
                }

                if (frame == null || IsEmptyFrame(frame))
                {
                    return;
                }

                parsedLineCount++;

                if (frame.F.HasValue)
                {
                    currentSpeed = FormatRounded(frame.F.Value * unitScale);
                }

                if (frame.M.HasValue)
                {
                    pendingMCode = frame.M.Value.ToString(CultureInfo.InvariantCulture);
                }

                int? g = frame.G;
                if (g.HasValue)
                {
                    if (g.Value == 20)
                    {
                        unitScale = 25.4;
                        return;
                    }

                    if (g.Value == 21)
                    {
                        unitScale = 1.0;
                        return;
                    }

                    if (g.Value == 90)
                    {
                        absoluteMode = true;
                        return;
                    }

                    if (g.Value == 91)
                    {
                        absoluteMode = false;
                        return;
                    }

                    if (g.Value == 92)
                    {
                        ApplyCoordinateSet(frame);
                        return;
                    }

                    if (g.Value == 4)
                    {
                        AddDwell(frame);
                        return;
                    }

                    if (g.Value >= 0 && g.Value <= 3)
                    {
                        modalMotionG = g.Value;
                    }
                }

                int motionG = g.HasValue && g.Value >= 0 && g.Value <= 3
                    ? g.Value
                    : modalMotionG;

                if (HasAnyAxis(frame))
                {
                    AddMotion(frame, motionG);
                }
            }

            public void FinalizeRows()
            {
                for (int i = 0; i < motions.Count; i++)
                {
                    GcodeMotionBuilder motion = motions[i];
                    bool isLast = i == motions.Count - 1;
                    string suffix;
                    if (isLast)
                    {
                        suffix = " (End)";
                    }
                    else if (motion.IsRapid)
                    {
                        suffix = " (Continuous Positioning)";
                    }
                    else
                    {
                        suffix = " (Continuous Path)";
                    }

                    motion.Row.MotionType = motion.MotionBase + suffix;
                }
            }

            public GcodeLoadResult BuildResult()
            {
                var bounds = new CadDocumentService.CadBounds
                {
                    Left = minX == double.MaxValue ? 0.0 : minX,
                    Top = minY == double.MaxValue ? 0.0 : minY,
                    Right = maxX == double.MinValue ? 100.0 : maxX,
                    Bottom = maxY == double.MinValue ? 100.0 : maxY
                };
                bounds.Width = Math.Max(bounds.Right - bounds.Left, 1.0);
                bounds.Height = Math.Max(bounds.Bottom - bounds.Top, 1.0);

                return new GcodeLoadResult
                {
                    CadDocument = new CadDocumentService.CadLoadResult
                    {
                        FilePath = filePath,
                        DirectoryPath = directoryPath,
                        FileName = fileName,
                        Bounds = bounds,
                        Primitives = primitives,
                        Points = points
                    },
                    ProcessRows = motions.Select(m => m.Row).ToList(),
                    ParsedLineCount = parsedLineCount,
                    MovementCount = motions.Count,
                    Warnings = warnings
                };
            }

            private void AddMotion(GcodeCommandFrame frame, int motionG)
            {
                double startX = currentX;
                double startY = currentY;
                double targetX = ResolveAxis(frame.X, currentX);
                double targetY = ResolveAxis(frame.Y, currentY);
                double targetZ = ResolveAxis(frame.Z, currentZ);

                bool hasXyMove = Math.Abs(targetX - currentX) > Epsilon
                    || Math.Abs(targetY - currentY) > Epsilon;
                bool isArc = motionG == 2 || motionG == 3;

                currentX = targetX;
                currentY = targetY;
                currentZ = targetZ;

                if (!hasXyMove && !isArc)
                {
                    return;
                }

                AddPoint(startX, startY, "G-code point");
                AddPoint(targetX, targetY, "G-code point");

                CadDocumentService.CadCoordinate center = null;
                var primitivePoints = new List<CadDocumentService.CadCoordinate>
                {
                    new CadDocumentService.CadCoordinate(startX, startY),
                    new CadDocumentService.CadCoordinate(targetX, targetY)
                };

                string motionBase;
                string sourceType;
                bool isCw = motionG == 2;
                bool isRapid = motionG == 0;

                if (isArc)
                {
                    double centerX;
                    double centerY;
                    bool hasCenter = TryGetArcCenter(frame, startX, startY, targetX, targetY, isCw, out centerX, out centerY);
                    if (hasCenter)
                    {
                        center = new CadDocumentService.CadCoordinate(centerX, centerY);
                        primitivePoints = SampleArc(startX, startY, targetX, targetY, centerX, centerY, isCw);
                        IncludeBounds(centerX, centerY);
                    }
                    else
                    {
                        warnings.Add("Line " + lineNumber.ToString(CultureInfo.InvariantCulture)
                            + ": arc command is missing usable I/J or R center data.");
                    }

                    motionBase = isCw ? "Arc CW" : "Arc CCW";
                    sourceType = isCw ? "Arc CW (G2)" : "Arc CCW (G3)";
                }
                else
                {
                    motionBase = "Line";
                    sourceType = isRapid ? "Line (G0 Rapid)" : "Line (G1)";
                }

                primitives.Add(new CadDocumentService.CadPrimitiveData
                {
                    SourceType = sourceType,
                    Points = primitivePoints,
                    Center = center,
                    IsCw = isCw,
                    IsCircle = false
                });

                IncludeBounds(startX, startY);
                IncludeBounds(targetX, targetY);

                var row = new GcodeProcessRowData
                {
                    MotionType = motionBase,
                    MCodeValue = pendingMCode ?? string.Empty,
                    Dwell = pendingDwell ?? string.Empty,
                    Speed = currentSpeed,
                    EndCoordinate = FormatCoordinate(targetX, targetY),
                    CenterCoordinate = center == null ? string.Empty : FormatCoordinate(center.X, center.Y)
                };

                motions.Add(new GcodeMotionBuilder
                {
                    MotionBase = motionBase,
                    IsRapid = isRapid,
                    Row = row
                });

                pendingMCode = null;
                pendingDwell = null;
            }

            private void AddDwell(GcodeCommandFrame frame)
            {
                string dwell = null;
                if (frame.P.HasValue)
                {
                    dwell = FormatRounded(frame.P.Value);
                }
                else if (frame.S.HasValue)
                {
                    dwell = FormatRounded(frame.S.Value);
                }

                if (!string.IsNullOrWhiteSpace(dwell))
                {
                    pendingDwell = dwell;
                }
            }

            private void ApplyCoordinateSet(GcodeCommandFrame frame)
            {
                if (frame.X.HasValue) currentX = frame.X.Value * unitScale;
                if (frame.Y.HasValue) currentY = frame.Y.Value * unitScale;
                if (frame.Z.HasValue) currentZ = frame.Z.Value * unitScale;
                IncludeBounds(currentX, currentY);
                AddPoint(currentX, currentY, "G92 point");
            }

            private double ResolveAxis(double? value, double current)
            {
                if (!value.HasValue)
                {
                    return current;
                }

                double scaled = value.Value * unitScale;
                return absoluteMode ? scaled : current + scaled;
            }

            private bool TryGetArcCenter(
                GcodeCommandFrame frame,
                double startX,
                double startY,
                double targetX,
                double targetY,
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
                {
                    return TryComputeArcCenterFromRadius(
                        startX,
                        startY,
                        targetX,
                        targetY,
                        frame.R.Value * unitScale,
                        isCw,
                        out centerX,
                        out centerY);
                }

                centerX = 0.0;
                centerY = 0.0;
                return false;
            }

            private void AddPoint(double x, double y, string lineType)
            {
                string key = MakePointKey(x, y);
                if (pointMap.ContainsKey(key))
                {
                    return;
                }

                var point = new CadDocumentService.CadPointData
                {
                    Index = points.Count + 1,
                    LineType = lineType,
                    X = x,
                    Y = y,
                    XDisplay = FormatNumber(x),
                    YDisplay = FormatNumber(y),
                    Key = key
                };

                pointMap.Add(key, point);
                points.Add(point);
            }

            private void IncludeBounds(double x, double y)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            private static bool HasAnyAxis(GcodeCommandFrame frame)
                => frame.X.HasValue || frame.Y.HasValue || frame.Z.HasValue;

            private static bool IsEmptyFrame(GcodeCommandFrame frame)
                => !frame.N.HasValue
                    && !frame.M.HasValue
                    && !frame.G.HasValue
                    && !frame.T.HasValue
                    && !frame.P.HasValue
                    && !frame.X.HasValue
                    && !frame.Y.HasValue
                    && !frame.Z.HasValue
                    && !frame.E.HasValue
                    && !frame.F.HasValue
                    && !frame.A.HasValue
                    && !frame.B.HasValue
                    && !frame.C.HasValue
                    && !frame.S.HasValue
                    && !frame.R.HasValue
                    && !frame.D.HasValue
                    && !frame.I.HasValue
                    && !frame.J.HasValue
                    && !frame.K.HasValue
                    && !frame.L.HasValue
                    && !frame.H.HasValue;
        }

        private sealed class GcodeMotionBuilder
        {
            public string MotionBase { get; set; }
            public bool IsRapid { get; set; }
            public GcodeProcessRowData Row { get; set; }
        }

        private static List<CadDocumentService.CadCoordinate> SampleArc(
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
            double sweep = isCw
                ? NormalizePositiveRadians(startAngle - endAngle)
                : NormalizePositiveRadians(endAngle - startAngle);

            if (sweep < Epsilon)
            {
                sweep = Math.PI * 2.0;
            }

            int steps = Math.Max(12, (int)Math.Ceiling(sweep / (Math.PI / 18.0)));
            double radius = Math.Sqrt(
                (startX - centerX) * (startX - centerX)
                + (startY - centerY) * (startY - centerY));

            var points = new List<CadDocumentService.CadCoordinate>();
            for (int i = 0; i <= steps; i++)
            {
                double angle = isCw
                    ? startAngle - sweep * i / steps
                    : startAngle + sweep * i / steps;
                points.Add(new CadDocumentService.CadCoordinate(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle)));
            }

            return points;
        }

        private static bool TryComputeArcCenterFromRadius(
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
            double height = Math.Sqrt(Math.Max(0.0, radius * radius - (chord * chord / 4.0)));
            double ux = -dy / chord;
            double uy = dx / chord;

            double c1x = midX + ux * height;
            double c1y = midY + uy * height;
            double c2x = midX - ux * height;
            double c2y = midY - uy * height;
            bool wantMajor = radiusValue < 0.0;

            double sweep1 = GetArcSweep(startX, startY, endX, endY, c1x, c1y, isCw);
            bool c1Major = sweep1 > Math.PI + Epsilon;

            if (c1Major == wantMajor)
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

        private static string NormalizeRawLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return string.Empty;
            }

            string withoutChecksum = rawLine;
            int checksumIndex = withoutChecksum.IndexOf('*');
            if (checksumIndex >= 0)
            {
                withoutChecksum = withoutChecksum.Substring(0, checksumIndex);
            }

            return StripParentheticalComments(withoutChecksum).Trim();
        }

        private static string StripParentheticalComments(string value)
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
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static string FormatCoordinate(double x, double y)
            => FormatNumber(x) + ";" + FormatNumber(y);

        private static string FormatNumber(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FormatRounded(double value)
            => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

        private static string MakePointKey(double x, double y)
            => FormatNumber(x) + "|" + FormatNumber(y);
    }
}
