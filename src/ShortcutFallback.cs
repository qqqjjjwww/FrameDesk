using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenStudio
{
    // RegisterHotKey is normally sufficient. When a requested key is already reserved,
    // handle just that key on a separate message loop so slow UI work cannot stall input.
    internal sealed class ShortcutFallback : IDisposable
    {
        private readonly IntPtr recipient;
        private readonly IntPtr desktop;
        private readonly bool wantsPhoto, wantsVideo;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly Thread thread;
        private readonly HookCallback callback;
        private IntPtr hook;
        private uint threadId;
        private enum PressState { Released, Forwarded, Captured }
        private PressState photoState, videoState;
        internal bool Installed { get { return hook != IntPtr.Zero; } }

        internal ShortcutFallback(IntPtr recipient, bool photo, bool video)
        {
            this.recipient = recipient;
            desktop = GetThreadDesktop(GetCurrentThreadId());
            wantsPhoto = photo; wantsVideo = video;
            callback = OnKeyboard;
            thread = new Thread(Listen) { IsBackground = true, Name = "FrameDesk shortcuts" };
            thread.Start();
            ready.WaitOne();
        }

        private void Listen()
        {
            NativeMessage message;
            try
            {
                threadId = GetCurrentThreadId();
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) return;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            }
            finally { ready.Set(); }
            if (!Installed) return;
            try
            {
                while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            finally { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr details)
        {
            if (code == 0)
            {
                int key = Marshal.ReadInt32(details);
                bool interested = (wantsPhoto && key == (int)Keys.F1) || (wantsVideo && key == (int)Keys.F2);
                if (interested)
                {
                    int msg = (int)message;
                    bool down = msg == 0x100 || msg == 0x104;
                    bool up = msg == 0x101 || msg == 0x105;
                    if (down || up)
                    {
                        bool modified = down && (msg == 0x104 || Held(0x10) || Held(0x11) || Held(0x12) || Held(0x5B) || Held(0x5C));
                        if (HandleKey(key, down, modified)) return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, details);
        }

        private bool HandleKey(int key, bool down, bool modified)
        {
            bool photo = key == (int)Keys.F1;
            if (photo ? !wantsPhoto : (key != (int)Keys.F2 || !wantsVideo)) return false;
            PressState previous = photo ? photoState : videoState;
            if (down && previous != PressState.Released) return previous == PressState.Captured;
            PressState next = PressState.Released;
            bool consume = previous == PressState.Captured;
            if (down)
            {
                consume = !modified && PostMessage(recipient, 0x0312, new IntPtr(photo ? 11 : 12), new IntPtr(key << 16));
                next = consume ? PressState.Captured : PressState.Forwarded;
            }
            if (photo) photoState = next; else videoState = next;
            return consume;
        }

        private static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        public void Dispose()
        {
            if (thread.IsAlive)
            {
                PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
                thread.Join();
            }
            ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            internal IntPtr Hwnd;
            internal uint Message;
            internal IntPtr WParam, LParam;
            internal uint Time;
            internal int X, Y;
            internal uint Private;
        }

        private delegate IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint id);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr details);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint id);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
