using System;
class P
{
    static void Main()
    {
        string[] progs = new string[] { "ActUtlType.ActUtlType", "ActMLUtlType.ActMLUtlType", "ActUtlType.ActMLUtlType" };
        foreach (var p in progs)
        {
            try
            {
                var t = Type.GetTypeFromProgID(p);
                Console.WriteLine(p + " => " + (t != null ? t.FullName : "<null>"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(p + " => Exception: " + ex.Message);
            }
        }
    }

