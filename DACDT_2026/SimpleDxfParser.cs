using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace DACDT_2026
{
    /// <summary>
    /// Simple DXF parser — reads only ENTITIES section (LINE, ARC, CIRCLE, LWPOLYLINE).
    /// No recursion, no external library. Handles any file size without StackOverflow.
    /// </summary>
    public static class SimpleDxfParser
    {
        public static CadDocumentService.CadLoadResult Parse(string filePath)
        {
            var primitives = new List<CadDocumentService.CadPrimitiveData>();
            var points = new List<CadDocumentService.CadPointData>();
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            // Read all lines
            string[] lines = File.ReadAllLines(filePath);

            // Find ENTITIES section
            int entitiesStart = -1;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Trim() == "0" && i + 1 < lines.Length && lines[i + 1].Trim() == "ENTITIES")
                {
                    entitiesStart = i + 2;
                    break;
                }
                if (lines[i].Trim() == "ENTITIES")
                {
                    entitiesStart = i + 1;
                    break;
                }
            }

            if (entitiesStart < 0)
                throw new Exception("DXF file has no ENTITIES section.");

            // Parse entities
            int idx = entitiesStart;
            while (idx < lines.Length)
            {
                string code = lines[idx].Trim();
                if (code == "0" && idx + 1 < lines.Length)
                {
                    string entityType = lines[idx + 1].Trim().ToUpperInvariant();
                    if (entityType == "ENDSEC" || entityType == "EOF")
                        break;

                    idx += 2; // Skip "0" and entity type

                    if (entityType == "LINE")
                        idx = ParseLine(lines, idx, primitives, ref minX, ref minY, ref maxX, ref maxY);
                    else if (entityType == "ARC")
                        idx = ParseArc(lines, idx, primitives, ref minX, ref minY, ref maxX, ref maxY);
                    else if (entityType == "CIRCLE")
                        idx = ParseCircle(lines, idx, primitives, ref minX, ref minY, ref maxX, ref maxY);
                    else if (entityType == "LWPOLYLINE")
                        idx = ParseLwPolyline(lines, idx, primitives, ref minX, ref minY, ref maxX, ref maxY);
                    else if (entityType == "SPLINE")
                        idx = ParseSpline(lines, idx, primitives, ref minX, ref minY, ref maxX, ref maxY);
                    else
                        idx = SkipEntity(lines, idx); // Skip unknown entities
                }
                else
                {
                    idx++;
                }
            }

            // Build bounds
            if (minX == float.MaxValue) { minX = 0; minY = 0; maxX = 100; maxY = 100; }
            var bounds = new CadDocumentService.CadBounds
            {
                Left = minX, Top = minY, Right = maxX, Bottom = maxY,
                Width = Math.Max(maxX - minX, 1), Height = Math.Max(maxY - minY, 1),
                MinZ = 0, MaxZ = 0
            };

            return new CadDocumentService.CadLoadResult
            {
                FilePath = Path.GetFullPath(filePath),
                DirectoryPath = Path.GetDirectoryName(filePath) ?? "",
                FileName = Path.GetFileName(filePath),
                Bounds = bounds,
                Primitives = primitives,
                Points = points
            };
        }

        private static int ParseLine(string[] lines, int idx, List<CadDocumentService.CadPrimitiveData> prims,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            while (idx < lines.Length - 1)
            {
                string code = lines[idx].Trim();
                if (code == "0") break; // Next entity
                string val = lines[idx + 1].Trim();
                switch (code)
                {
                    case "10": x1 = Dbl(val); break;
                    case "20": y1 = Dbl(val); break;
                    case "11": x2 = Dbl(val); break;
                    case "21": y2 = Dbl(val); break;
                }
                idx += 2;
            }
            prims.Add(new CadDocumentService.CadPrimitiveData
            {
                SourceType = "Line",
                Points = new List<CadDocumentService.CadCoordinate>
                {
                    new CadDocumentService.CadCoordinate(x1, y1),
                    new CadDocumentService.CadCoordinate(x2, y2)
                }
            });
            UpdateBounds(x1, y1, ref minX, ref minY, ref maxX, ref maxY);
            UpdateBounds(x2, y2, ref minX, ref minY, ref maxX, ref maxY);
            return idx;
        }

        private static int ParseArc(string[] lines, int idx, List<CadDocumentService.CadPrimitiveData> prims,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            double cx = 0, cy = 0, radius = 0, startAngle = 0, endAngle = 0;
            while (idx < lines.Length - 1)
            {
                string code = lines[idx].Trim();
                if (code == "0") break;
                string val = lines[idx + 1].Trim();
                switch (code)
                {
                    case "10": cx = Dbl(val); break;
                    case "20": cy = Dbl(val); break;
                    case "40": radius = Dbl(val); break;
                    case "50": startAngle = Dbl(val); break;
                    case "51": endAngle = Dbl(val); break;
                }
                idx += 2;
            }
            // Sample arc points
            double sweep = endAngle - startAngle;
            if (sweep <= 0) sweep += 360;
            int steps = Math.Max(18, (int)Math.Ceiling(sweep / 5.0));
            var pts = new List<CadDocumentService.CadCoordinate>();
            for (int i = 0; i <= steps; i++)
            {
                double angle = (startAngle + sweep * i / steps) * Math.PI / 180.0;
                double px = cx + radius * Math.Cos(angle);
                double py = cy + radius * Math.Sin(angle);
                pts.Add(new CadDocumentService.CadCoordinate(px, py));
                UpdateBounds(px, py, ref minX, ref minY, ref maxX, ref maxY);
            }
            prims.Add(new CadDocumentService.CadPrimitiveData
            {
                SourceType = "Arc",
                Points = pts,
                Center = new CadDocumentService.CadCoordinate(cx, cy),
                IsCw = false
            });
            return idx;
        }

        private static int ParseCircle(string[] lines, int idx, List<CadDocumentService.CadPrimitiveData> prims,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            double cx = 0, cy = 0, radius = 0;
            while (idx < lines.Length - 1)
            {
                string code = lines[idx].Trim();
                if (code == "0") break;
                string val = lines[idx + 1].Trim();
                switch (code)
                {
                    case "10": cx = Dbl(val); break;
                    case "20": cy = Dbl(val); break;
                    case "40": radius = Dbl(val); break;
                }
                idx += 2;
            }
            int steps = 72;
            var pts = new List<CadDocumentService.CadCoordinate>();
            for (int i = 0; i <= steps; i++)
            {
                double angle = 360.0 * i / steps * Math.PI / 180.0;
                double px = cx + radius * Math.Cos(angle);
                double py = cy + radius * Math.Sin(angle);
                pts.Add(new CadDocumentService.CadCoordinate(px, py));
                UpdateBounds(px, py, ref minX, ref minY, ref maxX, ref maxY);
            }
            prims.Add(new CadDocumentService.CadPrimitiveData
            {
                SourceType = "Circle",
                Points = pts,
                Center = new CadDocumentService.CadCoordinate(cx, cy),
                IsCircle = true
            });
            return idx;
        }

        private static int ParseLwPolyline(string[] lines, int idx, List<CadDocumentService.CadPrimitiveData> prims,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            var pts = new List<CadDocumentService.CadCoordinate>();
            bool closed = false;
            double curX = 0, curY = 0;
            bool hasVertex = false;

            while (idx < lines.Length - 1)
            {
                string code = lines[idx].Trim();
                if (code == "0") break;
                string val = lines[idx + 1].Trim();
                switch (code)
                {
                    case "70":
                        int flags = Int(val);
                        closed = (flags & 1) != 0;
                        break;
                    case "10":
                        if (hasVertex)
                        {
                            pts.Add(new CadDocumentService.CadCoordinate(curX, curY));
                            UpdateBounds(curX, curY, ref minX, ref minY, ref maxX, ref maxY);
                        }
                        curX = Dbl(val);
                        hasVertex = true;
                        break;
                    case "20":
                        curY = Dbl(val);
                        break;
                }
                idx += 2;
            }
            if (hasVertex)
            {
                pts.Add(new CadDocumentService.CadCoordinate(curX, curY));
                UpdateBounds(curX, curY, ref minX, ref minY, ref maxX, ref maxY);
            }
            if (closed && pts.Count > 2)
                pts.Add(new CadDocumentService.CadCoordinate(pts[0].X, pts[0].Y));

            if (pts.Count >= 2)
            {
                prims.Add(new CadDocumentService.CadPrimitiveData
                {
                    SourceType = "Polyline2D",
                    Points = pts
                });
            }
            return idx;
        }

        private static int SkipEntity(string[] lines, int idx)
        {
            while (idx < lines.Length - 1)
            {
                if (lines[idx].Trim() == "0") break;
                idx += 2;
            }
            return idx;
        }

        private static int ParseSpline(string[] lines, int idx, List<CadDocumentService.CadPrimitiveData> prims,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            var controlPoints = new List<CadDocumentService.CadCoordinate>();
            double curX = 0;
            while (idx < lines.Length - 1)
            {
                string code = lines[idx].Trim();
                if (code == "0") break;
                string val = lines[idx + 1].Trim();

                switch (code)
                {
                    case "10":
                        curX = Dbl(val);
                        break;
                    case "20":
                        double curY = Dbl(val);
                        controlPoints.Add(new CadDocumentService.CadCoordinate(curX, curY));
                        UpdateBounds(curX, curY, ref minX, ref minY, ref maxX, ref maxY);
                        break;
                }
                idx += 2;
            }

            if (controlPoints.Count >= 2)
            {
                // Implementing B-spline interpolation for smoother curves
                var interpolatedPoints = InterpolateBSpline(controlPoints);
                prims.Add(new CadDocumentService.CadPrimitiveData
                {
                    SourceType = "Spline",
                    Points = interpolatedPoints
                });
            }

            return idx;
        }

        private static List<CadDocumentService.CadCoordinate> InterpolateBSpline(List<CadDocumentService.CadCoordinate> controlPoints)
        {
            // Fallback to a robust Catmull-Rom style interpolation that works for any number of control points.
            var pts = new List<CadDocumentService.CadCoordinate>();
            int n = controlPoints.Count;
            if (n == 0) return pts;
            if (n == 1)
            {
                pts.Add(controlPoints[0]);
                return pts;
            }
            if (n == 2)
            {
                // Simple straight line between two points
                pts.Add(controlPoints[0]);
                pts.Add(controlPoints[1]);
                return pts;
            }

            int segmentsPerPair = 8; // points generated per segment
            for (int i = 0; i < n - 1; i++)
            {
                var p0 = i == 0 ? controlPoints[i] : controlPoints[i - 1];
                var p1 = controlPoints[i];
                var p2 = controlPoints[i + 1];
                var p3 = (i + 2 < n) ? controlPoints[i + 2] : controlPoints[i + 1];

                for (int j = 0; j <= segmentsPerPair; j++)
                {
                    double t = (double)j / segmentsPerPair;
                    double t2 = t * t;
                    double t3 = t2 * t;

                    // Catmull-Rom spline (centripetal-like basis)
                    double x = 0.5 * ((-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3
                                      + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2
                                      + (-p0.X + p2.X) * t
                                      + 2 * p1.X);
                    double y = 0.5 * ((-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3
                                      + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2
                                      + (-p0.Y + p2.Y) * t
                                      + 2 * p1.Y);

                    pts.Add(new CadDocumentService.CadCoordinate(x, y));
                }
            }

            return pts;
        }


        private static void UpdateBounds(double x, double y, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (x < minX) minX = (float)x;
            if (y < minY) minY = (float)y;
            if (x > maxX) maxX = (float)x;
            if (y > maxY) maxY = (float)y;
        }

        private static double Dbl(string s)
        {
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v);
            return v;
        }

        private static int Int(string s)
        {
            int.TryParse(s, out int v);
            return v;
        }
    }
}
