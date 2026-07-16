using System;
using System.Windows.Forms;

namespace NovelGrabber;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        // a stray UI-thread exception should surface, not silently kill the whole app
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "NovelGrabber",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new MainForm());
    }
}
