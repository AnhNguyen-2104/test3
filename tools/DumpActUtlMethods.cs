using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        string path = @"libs\\Interop.ActUtlTypeLib.dll";
        Assembly asm = Assembly.LoadFile(System.IO.Path.GetFullPath(path));
        foreach (var t in asm.GetTypes())
        {
            Console.WriteLine("Type: " + t.FullName);
            foreach (var m in t.GetMethods())
            {
                Console.Write("  " + m.Name + "(");
                var ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    Console.Write(p.ParameterType.FullName + (p.ParameterType.IsByRef ? "&" : ""));
                    if (i < ps.Length - 1) Console.Write(", ");
                }
                Console.WriteLine(") -> " + m.ReturnType.FullName);
            }
            Console.WriteLine();
        }
    }
}

