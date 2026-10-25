// Development helper: renders MainForm to a PNG without showing it on screen.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace GbOs86
{
    internal static class UiSnapshot
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string path = args.Length > 0 ? args[0] : "ui.png";
            using (MainForm form = new MainForm())
            {
                form.ClientSize = new Size(1020, 900);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.ShowInTaskbar = false;
                form.Show();
                for (int i = 0; i < 120; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(100);
                }
                form.PerformLayout();
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                form.Hide();
            }
            Console.WriteLine(path);
        }
    }
}
