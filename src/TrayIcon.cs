using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenStudio
{
    // The shell sends icon events to this hidden window. On hover it doubles as a non-activating exit button.
    internal sealed class TrayIcon : Form
    {
        private const uint IconId = 1, Callback = 0x8000 + 71;
        private const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4;
        private const uint MessageFlag = 1, IconFlag = 2, TipFlag = 4, InfoFlag = 16;
        private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private readonly Icon artwork;
        private readonly ContextMenuStrip menu;
        private readonly Action primaryClick;
        private readonly Func<string> exitCaption;
        private readonly Button exit;
        private readonly Timer timer = new Timer();
        private bool started, registered, version4;
        private string status = "";
        private Point lastIconPoint;
        private Rectangle anchor;
        private int awayTicks;

        internal TrayIcon(Icon artwork, ContextMenuStrip menu, Action primaryClick, Action quit, Func<string> exitCaption)
        {
            this.artwork = artwork;
            this.menu = menu;
            this.primaryClick = primaryClick;
            this.exitCaption = exitCaption;
            Text = "取景台 · 托盘";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            Padding = new Padding(1);
            exit = new Button { Text = "退出取景台", Dock = DockStyle.Fill, TabStop = false, Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
            exit.FlatAppearance.BorderSize = 0;
            exit.Click += delegate { Dismiss(); quit(); };
            Controls.Add(exit);
            ApplySkin();
            timer.Tick += Tick;
        }

        internal void ApplySkin()
        {
            StudioSkin skin = StudioSkin.Current;
            BackColor = exit.ForeColor = skin.Accent;
            exit.BackColor = skin.Tint;
            exit.FlatAppearance.MouseOverBackColor = skin.PrimaryFill;
        }

        internal void Start(string initialStatus)
        {
            status = initialStatus;
            started = true;
            if (IsHandleCreated) Register(); else CreateHandle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (started) Register();
        }

        private void Register()
        {
            IconData data = Data();
            data.flags = MessageFlag | IconFlag | TipFlag;
            data.callback = Callback;
            data.icon = artwork.Handle;
            data.tip = status;
            registered = Shell_NotifyIcon(Add, ref data) || Shell_NotifyIcon(Modify, ref data);
            if (registered)
            {
                data.version = 4;
                version4 = Shell_NotifyIcon(SetVersion, ref data);
                timer.Stop();
            }
            else { timer.Interval = 3000; timer.Start(); } // Explorer may still be starting.
        }

        internal void SetStatus(string value)
        {
            if (status == value) return;
            status = value;
            if (!registered) return;
            IconData data = Data();
            data.flags = TipFlag;
            data.tip = value;
            Shell_NotifyIcon(Modify, ref data);
        }

        internal void ShowRecordingNotice()
        {
            if (!registered) return;
            IconData data = Data();
            data.flags = InfoFlag;
            data.infoFlags = 1; // NIIF_INFO
            data.title = "已开始录屏";
            data.info = "再次按 F2 或单击取景台托盘图标停止。";
            Shell_NotifyIcon(Modify, ref data);
        }

        internal void Dismiss()
        {
            if (registered) timer.Stop();
            awayTicks = 0;
            if (Visible) Hide();
        }

        private bool GetIconBounds(out Rectangle bounds)
        {
            var id = new IconIdentifier { size = (uint)Marshal.SizeOf(typeof(IconIdentifier)), window = Handle, id = IconId };
            Edges rect;
            int result = Shell_NotifyIconGetRect(ref id, out rect);
            bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            return result == 0 && bounds.Width > 0 && bounds.Height > 0;
        }

        private void ShowHover(Point point, bool shellHover)
        {
            if (!registered || Visible || menu.Visible || MouseButtons != MouseButtons.None) return;
            if (!GetIconBounds(out anchor) || !Rectangle.Inflate(anchor, 1, 1).Contains(point))
            {
                // The overflow flyout can move before Explorer updates the icon rectangle.
                if (!shellHover && point != lastIconPoint) return;
                anchor = new Rectangle(point.X - 8, point.Y - 8, 17, 17);
            }

            Rectangle area = Screen.FromPoint(point).WorkingArea;
            Location = point; // Measure on the icon's monitor, including overflow and vertical taskbars.
            float scale = Math.Max(1F, GetDpiForWindow(Handle) / 96F);
            int width = (int)Math.Round(134 * scale), height = (int)Math.Round(36 * scale);
            int gap = (int)Math.Round(3 * scale);
            int x = anchor.Left + (anchor.Width - width) / 2;
            int y = point.Y < area.Top + area.Height / 2 ? anchor.Bottom + gap : anchor.Top - height - gap;
            Bounds = new Rectangle(Math.Max(area.Left, Math.Min(x, area.Right - width)),
                                   Math.Max(area.Top, Math.Min(y, area.Bottom - height)), width, height);
            if (Environment.OSVersion.Version.Build >= 19041) SetWindowDisplayAffinity(Handle, 0x11);
            exit.Text = exitCaption();
            Show();
            awayTicks = 0;
            timer.Interval = 125;
            timer.Start();
        }

        private void Tick(object sender, EventArgs e)
        {
            if (!registered) { Register(); return; }
            if (!Visible) { timer.Stop(); ShowHover(Cursor.Position, false); return; }
            exit.Text = exitCaption();
            // Leave a short bridge from icon to button so the user can move onto it and click.
            Rectangle bridge = Rectangle.Union(anchor, Bounds);
            bridge.Inflate(3, 3);
            if (bridge.Contains(Cursor.Position)) awayTicks = 0;
            else if (++awayTicks >= 3) Dismiss();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } // NOACTIVATE | TOOLWINDOW
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Dismiss(); return; }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
            if (m.Msg == TaskbarCreated && started) { Dismiss(); Register(); return; }
            if (m.Msg == Callback && started)
            {
                int code = (int)(m.LParam.ToInt64() & 0xffff);
                // The shell's saved anchor may still be off-screen while the overflow flyout opens.
                if (code == 0x406) ShowHover(Cursor.Position, true); // NIN_POPUPOPEN
                else if (code == 0x407 && !Visible && registered) timer.Stop(); // NIN_POPUPCLOSE before dwell
                else if (code == 0x200 && !Visible && !menu.Visible) // WM_MOUSEMOVE fallback for older shells
                {
                    lastIconPoint = Cursor.Position;
                    if (!timer.Enabled) { timer.Interval = Math.Max(150, SystemInformation.MouseHoverTime); timer.Start(); }
                }
                else if (code == 0x201 || code == 0x204) Dismiss();
                else if (code == 0x7b || (!version4 && code == 0x205)) // keyboard or right click
                {
                    Dismiss();
                    Rectangle r;
                    Point pt = Cursor.Position;
                    if (version4 && m.WParam.ToInt64() != -1)
                    {
                        long packed = m.WParam.ToInt64();
                        Point location = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
                        if (Screen.FromPoint(location).Bounds.Contains(location)) pt = location;
                    }
                    else if (GetIconBounds(out r) && Screen.FromRectangle(r).Bounds.Contains(r))
                        pt = new Point(r.Left + r.Width / 2, r.Top);
                    SetForegroundWindow(Handle);
                    menu.Show(pt);
                }
                else if (code == 0x400 || code == 0x401 || (!version4 && code == 0x202)) // NIN_SELECT / NIN_KEYSELECT
                {
                    Dismiss();
                    primaryClick();
                }
                return;
            }
            base.WndProc(ref m);
        }

        private IconData Data()
        {
            return new IconData { size = (uint)Marshal.SizeOf(typeof(IconData)), window = Handle, id = IconId };
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (registered)
            {
                IconData data = Data();
                Shell_NotifyIcon(Delete, ref data);
                registered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                started = false;
                timer.Dispose();
            }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct IconData
        {
            internal uint size;
            internal IntPtr window;
            internal uint id, flags, callback;
            internal IntPtr icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string tip;
            internal uint state, stateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string info;
            internal uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string title;
            internal uint infoFlags;
            internal Guid guid;
            internal IntPtr balloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconIdentifier { internal uint size; internal IntPtr window; internal uint id; internal Guid guid; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Edges { internal int left, top, right, bottom; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
        [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Edges bounds);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    }
}
