using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DACDT_2026
{
    internal static class Program
    {
        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");

        private static void LogException(string source, object exObj)
        {
            try
            {
                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string msg = $"[{ts}] {source}\n{exObj}\n{new string('-', 60)}\n";
                File.AppendAllText(LogPath, msg);
                System.Diagnostics.Debug.WriteLine(msg);
            }
            catch { /* ignore log errors */ }
        }

        [STAThread]
        static void Main()
        {
            // ── Bắt tất cả exception không được xử lý — ngăn app crash ──────────

            // 1. UI thread exceptions (WinForms)
            Application.ThreadException += (sender, e) =>
            {
                LogException("UI ThreadException", e.Exception);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 2. Background thread / Task exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                LogException("AppDomain UnhandledException (terminating=" + e.IsTerminating + ")", e.ExceptionObject);
            };

            // 3. Unobserved Task exceptions (async void, fire-and-forget)
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                LogException("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            // 4. First-chance exceptions — chỉ log exception KHÔNG được catch (fatal)
            // KHÔNG log FormatException vì nó được catch bình thường trong parser
            // và ghi file liên tục có thể gây crash do I/O recursive
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                if (e.Exception is StackOverflowException
                    || e.Exception is AccessViolationException
                    || e.Exception is OutOfMemoryException)
                {
                    LogException("FirstChance " + e.Exception.GetType().Name, e.Exception);
                }
            };

            try
            {
                LogException("App Start", "Application started");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
                LogException("App End", "Application exited normally");
            }
            catch (Exception ex)
            {
                LogException("Main Exception", ex);
                throw;
            }
        }
    }
}
