using System;
using System.Threading;
using System.Windows.Forms;

namespace GbOs86
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "gbOS86-Installer", out created))
            {
                if (!created)
                {
                    MessageBox.Show("The gbOS86 installer is already running.", "gbOS86 Installer",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    HandleFatal(e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    HandleFatal(e.ExceptionObject as Exception);
                };

                try
                {
                    Log.Start(System.IO.Path.Combine(Paths.LogsDir,
                        "installer-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"));
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    HandleFatal(ex);
                }
            }
        }

        private static void HandleFatal(Exception ex)
        {
            string message = ex == null ? "Unknown error." : ex.Message;
            try { Log.Write("FATAL: " + ex); } catch { }
            MessageBox.Show(message + "\n\nLog: " + Log.FilePath, "gbOS86 Installer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
