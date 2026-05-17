using System;

namespace DACDT_2026
{
    /// <summary>
    /// Form1 — Model classes (inner data structures).
    /// Tách ra để dễ tìm và chỉnh sửa cấu trúc dữ liệu độc lập với UI logic.
    /// </summary>
    public partial class Form1
    {
        private sealed class MonitorRow
        {
            public string Register { get; set; }
            public string Value    { get; set; }
            public string Status   { get; set; }
        }

        private sealed class ProcessRow
        {
            public string Key              { get; set; }
            public string MotionType       { get; set; }
            public string MCodeValue       { get; set; }
            public string Dwell            { get; set; }
            public string Speed            { get; set; }
            public string EndCoordinate    { get; set; }
            public string CenterCoordinate { get; set; }
            public double EndZ             { get; set; } // Z tọa độ đích (0 = không có Z)
        }

        private sealed class TelemetryBuffer
        {
            public string Path   { get; set; }
            public int    Length { get; set; }
        }

        private sealed class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string   Direction { get; set; }
            public string   Address   { get; set; }
            public string   Value     { get; set; }
            public string   Status    { get; set; }
            public string   Message   { get; set; }
        }
    }
}
