using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("取景台")]
[assembly: AssemblyProduct("取景台")]
[assembly: AssemblyDescription("Windows 桌面截图、标注与录屏")]
[assembly: AssemblyVersion("1.11.0.0")]

namespace ScreenStudio
{
    internal static class Program
    {
        internal static readonly int ActivateMessage = RegisterWindowMessage("FrameDesk.Activate.6B208ABC");

        [STAThread]
        private static void Main()
        {
            bool first;
            using (Mutex instance = new Mutex(true, @"Local\FrameDesk-6B208ABC", out first))
            {
                if (!first)
                {
                    PostMessage(new IntPtr(0xffff), ActivateMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string text);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
