using System;
using System.Threading;
using System.Windows.Forms;
using Spica.UI;

namespace Spica
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Deliberately NOT calling Application.EnableVisualStyles(): every native control
            // falls back to the classic Windows 9x renderer.
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            var form = new MainForm();
            if (args.Length > 0) form.StartupFile = args[0];
            Application.Run(form);
        }

        static int crashing;

        static void ShowCrash(Exception ex)
        {
            if (Interlocked.Exchange(ref crashing, 1) == 1) return;
            try
            {
                Win98MessageBox.Show(null,
                    "SPICA has performed an illegal operation.\n\n" + (ex?.ToString() ?? "Unknown error"),
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
            }
            finally { crashing = 0; }
        }
    }
}
