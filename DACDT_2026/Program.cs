using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DACDT_2026
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // ── Bắt tất cả exception không được xử lý — ngăn app crash ──────────

            // 1. UI thread exceptions (WinForms)
            Application.ThreadException += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("ThreadException: " + e.Exception);
                // Không crash — chỉ log, app tiếp tục chạy
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 2. Background thread / Task exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("UnhandledException: " + e.ExceptionObject);
                // Không crash nếu không phải fatal
            };

            // 3. Unobserved Task exceptions (async void, fire-and-forget)
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("UnobservedTaskException: " + e.Exception);
                e.SetObserved(); // Ngăn crash
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }
}
