using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal sealed class RecordingPanel : Form
    {
        private enum Phase { Preparing, Recording, Finishing, Copied }

        private readonly Button stop;
        private readonly Font stopFont;
        private Phase phase;
        private bool microphoneOn;
        private TimeSpan elapsed;
        private Rectangle lastRegion;
        private float zoom = 1;

        internal RecordingPanel(Action stopRecording)
        {
            if (stopRecording == null) throw new ArgumentNullException("stopRecording");
            Text = "取景台 · 录屏控制";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            stopFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            stop = new Button {
                Text = "停止 · F2", AccessibleName = "停止录屏并复制",
                UseVisualStyleBackColor = false, FlatStyle = FlatStyle.Flat,
                Font = stopFont, TabStop = false, Cursor = Cursors.Hand
            };
            stop.FlatAppearance.BorderSize = 0;
            stop.FlatAppearance.MouseOverBackColor = Color.White;
            stop.Click += delegate { stopRecording(); };
            Controls.Add(stop);
            ApplySkin();
        }

        internal void ApplySkin()
        {
            StudioSkin skin = StudioSkin.Current;
            BackColor = skin.Ink;
            stop.ForeColor = skin.Accent;
            stop.BackColor = skin.Tint;
            stop.FlatAppearance.MouseDownBackColor = skin.PrimaryFill;
            Invalidate(true);
        }

        internal bool TryShowFor(Rectangle region, bool microphone = false)
        {
            Hide();
            microphoneOn = microphone;
            phase = Phase.Preparing;
            elapsed = TimeSpan.Zero;
            lastRegion = region;
            stop.Visible = true;
            stop.Enabled = true;
            stop.Text = "停止 · F2";
            AccessibleDescription = "正在准备录屏";

            if (region.Width < 1 || region.Height < 1) return false;
            // On older systems 0x11 can succeed but behave as WDA_MONITOR (a black box).
            bool excluded = Environment.OSVersion.Version.Build >= 19041 &&
                            SetWindowDisplayAffinity(Handle, 0x11);
            Screen main = Screen.FromRectangle(region);
            if (!PlaceOn(main, region, excluded))
            {
                bool found = false;
                foreach (Screen other in Screen.AllScreens)
                {
                    if (other.DeviceName != main.DeviceName && PlaceOn(other, region, false))
                    { found = true; break; }
                }
                if (!found) return false;
            }
            Show();
            Update();
            return true;
        }

        // Used before the encoder starts on displays that cannot exclude a recording control.
        // The caller hides the notice and flushes the compositor before capturing the first frame.
        internal void ShowPreparationNotice()
        {
            phase = Phase.Preparing;
            stop.Enabled = stop.Visible = true;
            AccessibleDescription = "正在准备录屏";
            ShowOnTarget();
        }

        private bool PlaceOn(Screen screen, Rectangle recording, bool canOverlap)
        {
            Rectangle area = screen.WorkingArea;
            // Move the hidden window before asking for its per-monitor DPI.
            SetBounds(area.Left + area.Width / 2, area.Top + area.Height / 2, 1, 1);
            uint dpi = GetDpiForWindow(Handle);
            zoom = dpi > 0 ? dpi / 96f : 1f;
            int w = Px(302), h = Px(52), gap = Px(12);
            if (w > area.Width || h > area.Height) return false;
            int left = area.Left + Math.Min(gap, (area.Width - w) / 2);
            int right = area.Right - w - Math.Min(gap, (area.Width - w) / 2);
            int top = area.Top + Math.Min(gap, (area.Height - h) / 2);
            int bottom = area.Bottom - h - Math.Min(gap, (area.Height - h) / 2);
            int middle = area.Left + (area.Width - w) / 2;
            Point[] points = { new Point(middle, top), new Point(middle, bottom),
                               new Point(left, top), new Point(right, top),
                               new Point(left, bottom), new Point(right, bottom) };
            foreach (Point p in points)
            {
                Rectangle candidate = new Rectangle(p.X, p.Y, w, h);
                if (!canOverlap && candidate.IntersectsWith(recording)) continue;
                Bounds = candidate;
                stop.Bounds = new Rectangle(Px(197), Px(9), Px(95), Px(34));
                return true;
            }
            return false;
        }

        internal void UpdateElapsed(TimeSpan duration)
        {
            if ((phase != Phase.Preparing && phase != Phase.Recording) || duration < TimeSpan.Zero) return;
            phase = Phase.Recording;
            AccessibleDescription = microphoneOn ? "正在录屏，同时录制麦克风" : "正在录屏，无声";
            elapsed = duration;
            Invalidate();
            if (Visible && duration == TimeSpan.Zero) Update();
        }

        internal void ShowFinishing()
        {
            phase = Phase.Finishing;
            stop.Enabled = false;
            stop.Text = "请稍候";
            AccessibleDescription = "正在完成录屏";
            Invalidate();
            if (Visible) Update();
        }

        internal void ShowCopied(TimeSpan duration)
        {
            phase = Phase.Copied;
            elapsed = duration;
            stop.Visible = false;
            AccessibleDescription = "录屏已复制到剪贴板";
            // At completion no frames are being captured, so a previously unavailable placement is safe.
            ShowOnTarget();
        }

        private void ShowOnTarget()
        {
            if (!Visible)
            {
                Screen screen = lastRegion.IsEmpty ? Screen.FromPoint(Cursor.Position) : Screen.FromRectangle(lastRegion);
                if (!PlaceOn(screen, lastRegion, true)) return;
                Show();
            }
            Invalidate();
            Update();
        }

        private int Px(int value) { return (int)Math.Round(zoom * value); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return p;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // WM_MOUSEACTIVATE: MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Art.Init(g, zoom);
            using (Pen border = new Pen(StudioSkin.Current.Muted))
                g.DrawRectangle(border, .5f, .5f, ClientSize.Width / zoom - 1, ClientSize.Height / zoom - 1);
            bool live = phase == Phase.Recording;
            bool copied = phase == Phase.Copied;
            Color signal = copied ? StudioSkin.Current.SuccessFill : (live ? Color.FromArgb(255, 122, 144) : Color.FromArgb(246, 196, 111));
            Art.Dot(g, signal, 16, 21, 10);
            string status = phase == Phase.Preparing ? "正在准备录屏" : (live ? "录制中" : (copied ? "录屏已复制" : "正在复制…"));
            Art.Label(g, status, 36, 0, live ? 61 : 156, 52, 16, Color.White, true, StringAlignment.Near);
            if (live || copied)
            {
                if (live && microphoneOn) Art.Icon(g, "microphone", 85, 18, 16, StudioSkin.Current.SuccessFill);
                string time = elapsed.TotalHours >= 1
                    ? ((long)elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + elapsed.ToString(@"\:mm\:ss")
                    : elapsed.ToString(@"mm\:ss");
                Art.Label(g, time, copied ? 194 : 102, 0, 90, 52, 18, StudioSkin.Current.SecondaryTint, false, StringAlignment.Near);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) stopFont.Dispose();
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    }
}
