using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using netDxf;
using netDxf.Entities;

namespace test1
{

    public sealed class CadDocumentService
    {
        public CadLoadResult Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("DXF path is empty.", nameof(filePath));
            }

            DxfDocument document = DxfDocument.Load(filePath);
            CadExtractionContext context = new CadExtractionContext();

            foreach (EntityObject entity in document.Entities.All)
            {
                ExtractEntity(entity, CadTransform.Identity, context);
            }

            return new CadLoadResult
            {
                FilePath = filePath,
                DirectoryPath = Path.GetDirectoryName(filePath) ?? string.Empty,
                FileName = Path.GetFileName(filePath),
                Bounds = CadBounds.FromRectangle(context.GetBounds()),
                Primitives = context.Primitives,
                Points = context.BuildPointRows()
            };
        }

        private void ExtractEntity(EntityObject entity, CadTransform transform, CadExtractionContext context)
        {
            if (entity == null || !entity.IsVisible)
            {
                return;
            }

            Line line = entity as Line;
            if (line != null)
            {
                PointF start = transform.Apply(line.StartPoint);
                PointF end = transform.Apply(line.EndPoint);
                context.AddPrimitive("Line", new[] { start, end }, false);
                context.AddLinearSegment(start, end, "Line");
                context.AddCandidatePoint(start, "Điểm line", "Line", 1);
                context.AddCandidatePoint(end, "Điểm line", "Line", 1);
                return;
            }

            Polyline2D polyline2D = entity as Polyline2D;
            if (polyline2D != null)
            {
                List<PointF> points = polyline2D.Vertexes.Select(v => transform.Apply(v.Position.X, v.Position.Y)).ToList();
                AddPolylineGeometry(context, points, polyline2D.IsClosed, "Polyline2D");
                return;
            }

            Polyline3D polyline3D = entity as Polyline3D;
            if (polyline3D != null)
            {
                List<PointF> points = polyline3D.Vertexes.Select(transform.Apply).ToList();
                AddPolylineGeometry(context, points, polyline3D.IsClosed, "Polyline3D");
                return;
            }

            Arc arc = entity as Arc;
            if (arc != null)
            {
                List<PointF> arcPoints = SampleArc(arc, transform);
                // netDxf Arc always CCW from StartAngle to EndAngle, unless we calculate cross product or something. 
                // Wait, DXF Arcs are always CCW in OCS. We can assume IsCw = false for now, or check sweep.
                bool isCw = false; 
                CadCoordinate center = new CadCoordinate(transform.Apply(arc.Center).X, transform.Apply(arc.Center).Y);
                context.AddPrimitive("Arc", arcPoints, false, center, isCw, false);
                if (arcPoints.Count > 0)
                {
                    context.AddCandidatePoint(arcPoints[0], "Đầu cung", "Arc", 2);
                    context.AddCandidatePoint(arcPoints[arcPoints.Count - 1], "Cuối cung", "Arc", 2);
                }
                return;
            }

            Circle circle = entity as Circle;
            if (circle != null)
            {
                CadCoordinate center = new CadCoordinate(transform.Apply(circle.Center).X, transform.Apply(circle.Center).Y);
                context.AddPrimitive("Circle", SampleCircle(circle, transform), true, center, false, true);
                context.AddCandidatePoint(transform.Apply(circle.Center), "Tâm circle", "Circle", 3);
                return;
            }

            Insert insert = entity as Insert;
            if (insert != null && insert.Block != null)
            {
                CadTransform child = transform.Append(CadTransform.FromInsert(insert));
                foreach (EntityObject childEntity in insert.Block.Entities)
                {
                    ExtractEntity(childEntity, child, context);
                }
            }
        }

        private void AddPolylineGeometry(CadExtractionContext context, List<PointF> points, bool isClosed, string sourceType)
        {
            if (points.Count == 0)
            {
                return;
            }

            context.AddPrimitive(sourceType, points, isClosed);

            for (int i = 0; i < points.Count - 1; i++)
            {
                context.AddLinearSegment(points[i], points[i + 1], sourceType);
            }

            if (isClosed && points.Count > 2)
            {
                context.AddLinearSegment(points[points.Count - 1], points[0], sourceType);
            }

            foreach (PointF point in points)
            {
                context.AddCandidatePoint(point, "Đỉnh polyline", sourceType, 1);
            }
        }

        private static List<PointF> SampleArc(Arc arc, CadTransform transform)
        {
            List<PointF> points = new List<PointF>();
            double startAngle = NormalizeAngle(arc.StartAngle);
            double endAngle = NormalizeAngle(arc.EndAngle);
            double sweep = endAngle - startAngle;

            if (sweep <= 0)
            {
                sweep += 360.0;
            }

            int steps = Math.Max(18, (int)Math.Ceiling(sweep / 10.0));
            for (int i = 0; i <= steps; i++)
            {
                double angle = startAngle + sweep * i / steps;
                double radians = angle * Math.PI / 180.0;
                double x = arc.Center.X + arc.Radius * Math.Cos(radians);
                double y = arc.Center.Y + arc.Radius * Math.Sin(radians);
                points.Add(transform.Apply(x, y));
            }

            return points;
        }

        private static List<PointF> SampleCircle(Circle circle, CadTransform transform)
        {
            List<PointF> points = new List<PointF>();
            const int steps = 72;

            for (int i = 0; i <= steps; i++)
            {
                double angle = 360.0 * i / steps;
                double radians = angle * Math.PI / 180.0;
                double x = circle.Center.X + circle.Radius * Math.Cos(radians);
                double y = circle.Center.Y + circle.Radius * Math.Sin(radians);
                points.Add(transform.Apply(x, y));
            }

            return points;
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle < 0) angle += 360.0;
            while (angle >= 360.0) angle -= 360.0;
            return angle;
        }

        public sealed class CadLoadResult
        {
            public string FilePath { get; set; }
            public string DirectoryPath { get; set; }
            public string FileName { get; set; }
            public CadBounds Bounds { get; set; }
            public List<CadPrimitiveData> Primitives { get; set; }
            public List<CadPointData> Points { get; set; }
        }

        public sealed class CadBounds
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Right { get; set; }
            public double Bottom { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }

            public static CadBounds FromRectangle(RectangleF bounds)
            {
                return new CadBounds
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Right = bounds.Right,
                    Bottom = bounds.Bottom,
                    Width = bounds.Width,
                    Height = bounds.Height
                };
            }
        }

        public sealed class CadPrimitiveData
        {
            public string SourceType { get; set; }
            public List<CadCoordinate> Points { get; set; }
            public CadCoordinate Center { get; set; }
            public bool IsCw { get; set; }
            public bool IsCircle { get; set; }
        }

        public sealed class CadPointData
        {
            public int Index { get; set; }
            public string LineType { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public string XDisplay { get; set; }
            public string YDisplay { get; set; }
            public string Key { get; set; }
        }

        public sealed class CadCoordinate
        {
            public CadCoordinate()
            {
            }

            public CadCoordinate(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; set; }
            public double Y { get; set; }
        }

        private sealed class CadExtractionContext
        {
            private readonly Dictionary<string, CadPointAccumulator> pointAccumulators = new Dictionary<string, CadPointAccumulator>();
            private int sequence;
            private float minX = float.MaxValue;
            private float minY = float.MaxValue;
            private float maxX = float.MinValue;
            private float maxY = float.MinValue;

            public List<CadPrimitiveData> Primitives { get; } = new List<CadPrimitiveData>();
            public List<LineSegment> LineSegments { get; } = new List<LineSegment>();

            public void AddPrimitive(string sourceType, IEnumerable<PointF> points, bool closeLoop, CadCoordinate center = null, bool isCw = false, bool isCircle = false)
            {
                List<PointF> list = points.ToList();
                if (list.Count == 0)
                {
                    return;
                }

                if (closeLoop && list.Count > 2 && !AreClose(list[0], list[list.Count - 1]))
                {
                    list.Add(list[0]);
                }

                IncludeBounds(list);

                if (list.Count > 1)
                {
                    Primitives.Add(new CadPrimitiveData
                    {
                        SourceType = sourceType,
                        Points = list.Select(p => new CadCoordinate(p.X, p.Y)).ToList(),
                        Center = center,
                        IsCw = isCw,
                        IsCircle = isCircle
                    });
                }
            }

            public void AddLinearSegment(PointF start, PointF end, string sourceType)
            {
                if (AreClose(start, end))
                {
                    return;
                }

                IncludeBounds(new[] { start, end });
                LineSegments.Add(new LineSegment(start, end, sourceType));
            }

            public void AddCandidatePoint(PointF point, string category, string sourceType, int priority)
            {
                IncludeBounds(new[] { point });

                string key = MakePointKey(point);
                CadPointAccumulator accumulator;
                if (!pointAccumulators.TryGetValue(key, out accumulator))
                {
                    accumulator = new CadPointAccumulator(point, category, priority, sequence++);
                    pointAccumulators.Add(key, accumulator);
                }

                accumulator.Merge(point, category, sourceType, priority);
            }

            public List<CadPointData> BuildPointRows()
            {
                AddIntersectionPoints();

                List<CadPointAccumulator> ordered = pointAccumulators.Values
                    .OrderBy(p => p.Priority)
                    .ThenBy(p => p.Order)
                    .ThenBy(p => p.Point.X)
                    .ThenBy(p => p.Point.Y)
                    .ToList();

                List<CadPointData> rows = new List<CadPointData>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    CadPointAccumulator point = ordered[i];
                    rows.Add(new CadPointData
                    {
                        Index = i + 1,
                        LineType = point.DisplayType,
                        X = point.Point.X,
                        Y = point.Point.Y,
                        XDisplay = point.Point.X.ToString("0.###", CultureInfo.InvariantCulture),
                        YDisplay = point.Point.Y.ToString("0.###", CultureInfo.InvariantCulture),
                        Key = MakePointKey(point.Point)
                    });
                }

                return rows;
            }

            public RectangleF GetBounds()
            {
                if (minX == float.MaxValue)
                {
                    return new RectangleF(0, 0, 100, 100);
                }

                return RectangleF.FromLTRB(minX, minY, maxX, maxY);
            }

            private void IncludeBounds(IEnumerable<PointF> points)
            {
                foreach (PointF point in points)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
            }

            private void AddIntersectionPoints()
            {
                for (int i = 0; i < LineSegments.Count; i++)
                {
                    for (int j = i + 1; j < LineSegments.Count; j++)
                    {
                        PointF intersection;
                        if (TryGetIntersection(LineSegments[i], LineSegments[j], out intersection))
                        {
                            AddCandidatePoint(intersection, "Giao điểm", LineSegments[i].SourceType + "/" + LineSegments[j].SourceType, 0);
                        }
                    }
                }
            }

            private static bool TryGetIntersection(LineSegment first, LineSegment second, out PointF intersection)
            {
                const double epsilon = 0.000001;

                double x1 = first.Start.X;
                double y1 = first.Start.Y;
                double x2 = first.End.X;
                double y2 = first.End.Y;
                double x3 = second.Start.X;
                double y3 = second.Start.Y;
                double x4 = second.End.X;
                double y4 = second.End.Y;

                double dx1 = x2 - x1;
                double dy1 = y2 - y1;
                double dx2 = x4 - x3;
                double dy2 = y4 - y3;
                double denominator = dx1 * dy2 - dy1 * dx2;

                if (Math.Abs(denominator) < epsilon)
                {
                    if (AreClose(first.Start, second.Start))
                    {
                        intersection = first.Start;
                        return true;
                    }

                    if (AreClose(first.Start, second.End))
                    {
                        intersection = first.Start;
                        return true;
                    }

                    if (AreClose(first.End, second.Start))
                    {
                        intersection = first.End;
                        return true;
                    }

                    if (AreClose(first.End, second.End))
                    {
                        intersection = first.End;
                        return true;
                    }

                    intersection = PointF.Empty;
                    return false;
                }

                double ua = ((x3 - x1) * dy2 - (y3 - y1) * dx2) / denominator;
                double ub = ((x3 - x1) * dy1 - (y3 - y1) * dx1) / denominator;

                if (ua < -epsilon || ua > 1 + epsilon || ub < -epsilon || ub > 1 + epsilon)
                {
                    intersection = PointF.Empty;
                    return false;
                }

                intersection = new PointF((float)(x1 + ua * dx1), (float)(y1 + ua * dy1));
                return true;
            }

            private static string MakePointKey(PointF point)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.###}|{1:0.###}", point.X, point.Y);
            }

            private static bool AreClose(PointF first, PointF second)
            {
                return Math.Abs(first.X - second.X) < 0.001f && Math.Abs(first.Y - second.Y) < 0.001f;
            }
        }

        private sealed class CadPointAccumulator
        {
            private readonly HashSet<string> sourceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public CadPointAccumulator(PointF point, string category, int priority, int order)
            {
                Point = point;
                Category = category;
                Priority = priority;
                Order = order;
            }

            public PointF Point { get; private set; }
            public string Category { get; private set; }
            public int Priority { get; private set; }
            public int Order { get; private set; }

            public string DisplayType
            {
                get
                {
                    if (sourceTypes.Count == 0)
                    {
                        return Category;
                    }

                    return Category + " (" + string.Join("/", sourceTypes.OrderBy(x => x)) + ")";
                }
            }

            public void Merge(PointF point, string category, string sourceType, int priority)
            {
                Point = point;
                if (priority < Priority)
                {
                    Category = category;
                    Priority = priority;
                }

                if (!string.IsNullOrWhiteSpace(sourceType))
                {
                    sourceTypes.Add(sourceType);
                }
            }
        }

        private struct LineSegment
        {
            public LineSegment(PointF start, PointF end, string sourceType)
            {
                Start = start;
                End = end;
                SourceType = sourceType;
            }

            public PointF Start { get; }
            public PointF End { get; }
            public string SourceType { get; }
        }

        private struct CadTransform
        {
            public static CadTransform Identity => new CadTransform(1, 0, 0, 1, 0, 0);

            public CadTransform(double a, double b, double c, double d, double tx, double ty)
            {
                A = a;
                B = b;
                C = c;
                D = d;
                Tx = tx;
                Ty = ty;
            }

            public double A { get; }
            public double B { get; }
            public double C { get; }
            public double D { get; }
            public double Tx { get; }
            public double Ty { get; }

            public PointF Apply(netDxf.Vector3 value)
            {
                return Apply(value.X, value.Y);
            }

            public PointF Apply(double x, double y)
            {
                double outX = A * x + B * y + Tx;
                double outY = C * x + D * y + Ty;
                return new PointF((float)outX, (float)outY);
            }

            public CadTransform Append(CadTransform local)
            {
                return new CadTransform(
                    A * local.A + B * local.C,
                    A * local.B + B * local.D,
                    C * local.A + D * local.C,
                    C * local.B + D * local.D,
                    A * local.Tx + B * local.Ty + Tx,
                    C * local.Tx + D * local.Ty + Ty);
            }

            public static CadTransform FromInsert(Insert insert)
            {
                double radians = insert.Rotation * Math.PI / 180.0;
                double cos = Math.Cos(radians);
                double sin = Math.Sin(radians);
                return new CadTransform(
                    cos * insert.Scale.X,
                    -sin * insert.Scale.Y,
                    sin * insert.Scale.X,
                    cos * insert.Scale.Y,
                    insert.Position.X,
                    insert.Position.Y);
            }
        }
    }
}
