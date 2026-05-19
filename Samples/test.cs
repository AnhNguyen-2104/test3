using System;
using System.IO;
using DACDT_2026;
using Gcode.Utils;
using Gcode.Utils.Entity;

namespace TestApp {
    class Program {
        static void Main() {
            var service = new GcodeCoordinateService();
            var res = service.LoadAsCad(@"D:\DACDT_2026\Test\1mm.gcode");
            Console.WriteLine("Primitives: " + res.Primitives.Count);
            Console.WriteLine("Points: " + res.Points.Count);
        }
    }
}
