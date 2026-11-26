using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal static class ScrollCaptureSession
    {
        internal static async Task<Bitmap> CaptureAsync(Rectangle region, bool includeCursor)
        {
            if (region.Width < 16 || region.Height < 32)
                throw new ArgumentException("滚动选区太小，请框选较完整的内容区域。");
            FocusTarget(region);
            DwmFlush();
            ScrollStitcher stitcher;
            using (Bitmap first = ScreenCapture.Grab(region, includeCursor))
                stitcher = new ScrollStitcher(first);
            using (stitcher)
            using (var panel = new ScrollPanel(region, includeCursor, stitcher))
            {
                bool accepted = await panel.TrackAsync();
                return accepted ? stitcher.Render() : null;
            }
        }

        private static void FocusTarget(Rectangle region)
        {
            // The picker was the active window. Restore the visible target so wheel input
            // also works for users who disabled scrolling inactive windows in Windows.
            Point center = new Point(region.Left + region.Width / 2, region.Top + region.Height / 2);
            IntPtr target = GetAncestor(WindowFromPoint(center), 2); // GA_ROOT
            uint pid;
            if (target != IntPtr.Zero && IsWindowVisible(target) &&
                GetWindowThreadProcessId(target, out pid) != 0 && pid != GetCurrentProcessId())
                SetForegroundWindow(target);
        }

        private sealed class ScrollPanel : Form
        {
            private const int FinishId = 61, CancelId = 62;
            private readonly Rectangle region;
            private readonly bool includeCursor;
            private readonly ScrollStitcher stitcher;
            private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Timer sampling = new Timer();
            private readonly Button finish, cancel;
            private NotifyIcon tray;
            private ContextMenuStrip trayMenu;
            private bool finishKey, cancelKey, ending, temporaryNotice;
            private float zoom = 1;
            private string status = "";
            private int height;

            internal ScrollPanel(Rectangle region, bool includeCursor, ScrollStitcher stitcher)
            {
                this.region = region;
                this.includeCursor = includeCursor;
                this.stitcher = stitcher;
                Text = "取景台 · 滚动截图";
                AccessibleName = Text;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                ShowInTaskbar = false;
                TopMost = true;
                DoubleBuffered = true;
                BackColor = Art.Ink;
                finish = MakeButton("完成", Art.Iris, delegate { End(true); });
                cancel = MakeButton("取消", Color.WhiteSmoke, delegate { End(false); });
                sampling.Tick += OnSample;
            }

            private Button MakeButton(string text, Color fill, EventHandler action)
            {
                var button = new Button {
                    Text = text, AccessibleName = text, TabStop = false, FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand, BackColor = fill, ForeColor = Art.Ink,
                    UseVisualStyleBackColor = false
                };
                button.FlatAppearance.BorderSize = 0;
                button.Click += action;
                Controls.Add(button);
                return button;
            }

            internal Task<bool> TrackAsync()
            {
                const uint noRepeat = 0x4000;
                finishKey = RegisterHotKey(Handle, FinishId, noRepeat, (uint)Keys.Enter);
                cancelKey = RegisterHotKey(Handle, CancelId, noRepeat, (uint)Keys.Escape);
                finish.Text = finishKey ? "完成 · Enter" : "完成";
                cancel.Text = cancelKey ? "取消 · Esc" : "取消";

                Screen target = Screen.FromRectangle(region);
                bool positioned = PlaceOn(target, false);
                if (!positioned && Environment.OSVersion.Version.Build >= 19041 &&
                    SetWindowDisplayAffinity(Handle, 0x11))
                    positioned = PlaceOn(target, true);
                if (!positioned)
                {
                    foreach (Screen screen in Screen.AllScreens)
                        if (screen.DeviceName != target.DeviceName && PlaceOn(screen, false)) { positioned = true; break; }
                }
                if (!positioned)
                {
                    // One older display with a full-screen selection: show one short hint,
                    // then keep all controls in the notification area for clean frames.
                    trayMenu = new ContextMenuStrip();
                    trayMenu.Items.Add("完成滚动截图", null, delegate { End(true); });
                    trayMenu.Items.Add("取消滚动截图", null, delegate { End(false); });
                    tray = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = trayMenu,
                                            Text = "取景台 · 滚动截图，单击完成", Visible = true };
                    tray.MouseClick += delegate(object sender, MouseEventArgs e)
                    { if (e.Button == MouseButtons.Left) End(true); };
                    temporaryNotice = true;
                    PlaceOn(target, true);
                }
                height = stitcher.Height;
                status = temporaryNotice ? "稍后从取景台托盘图标完成或取消" : "将鼠标移入选区，缓慢向下滚动";
                AccessibleDescription = status;
                Show();
                Update();
                sampling.Interval = temporaryNotice ? 1200 : 95;
                sampling.Start();
                Application.ApplicationExit += OnApplicationExit;
                return completion.Task;
            }

            private bool PlaceOn(Screen screen, bool overlap)
            {
                Rectangle area = screen.WorkingArea;
                SetBounds(area.Left + area.Width / 2, area.Top + area.Height / 2, 1, 1);
                uint dpi = GetDpiForWindow(Handle);
                zoom = dpi > 0 ? dpi / 96f : 1f;
                int w = Math.Min(Px(500), area.Width), h = Px(64), gap = Px(8);
                if (h > area.Height || w < Px(260)) return false;
                int left = area.Left, right = area.Right - w, top = area.Top, bottom = area.Bottom - h;
                int nearX = Math.Max(left, Math.Min(right, region.Left + (region.Width - w) / 2));
                int nearY = Math.Max(top, Math.Min(bottom, region.Top));
                Point[] positions = {
                    new Point(nearX,region.Top-h-gap), new Point(nearX,region.Bottom+gap),
                    new Point(region.Right+gap,nearY), new Point(region.Left-w-gap,nearY),
                    new Point(nearX,top+gap), new Point(nearX,bottom-gap),
                    new Point(left,top), new Point(right,top), new Point(left,bottom), new Point(right,bottom)
                };
                foreach (Point p in positions)
                {
                    Rectangle candidate = new Rectangle(p.X, p.Y, w, h);
                    if (!area.Contains(candidate) || (!overlap && region.IntersectsWith(candidate))) continue;
                    Bounds = candidate;
                    cancel.Bounds = new Rectangle(w - Px(91), Px(14), Px(81), Px(36));
                    finish.Bounds = new Rectangle(w - Px(202), Px(14), Px(103), Px(36));
                    return true;
                }
                return false;
            }

            private int Px(int value) { return (int)Math.Round(zoom * value); }

            private void OnSample(object sender, EventArgs e)
            {
                if (ending || (trayMenu != null && trayMenu.Visible)) return;
                try
                {
                    if (temporaryNotice)
                    {
                        temporaryNotice = false;
                        Hide();
                        DwmFlush();
                        sampling.Interval = 95;
                    }
                    SampleFrame();
                    if (stitcher.LimitReached) sampling.Stop();
                }
                catch (Exception ex) { StopControls(); completion.TrySetException(ex); }
            }

            private void SampleFrame()
            {
                using (Bitmap next = ScreenCapture.Grab(region, includeCursor))
                    stitcher.TryAppend(next);
                string update = stitcher.LastStatus;
                if (height == stitcher.Height && status == update) return;
                height = stitcher.Height;
                status = update;
                finish.Text = stitcher.Unmatched ? "完成已有部分" : (finishKey ? "完成 · Enter" : "完成");
                AccessibleDescription = String.Format("{0} 像素，{1}", height, status);
                if (tray != null)
                    tray.Text = String.Format("取景台 · 滚动截图 {0} px · 单击完成", height);
                Invalidate();
            }

            private void OnApplicationExit(object sender, EventArgs e) { End(false); }

            private void End(bool accept)
            {
                if (ending) return;
                try
                {
                    if (accept && !stitcher.LimitReached)
                    {
                        bool partialWasShown = stitcher.Unmatched;
                        if (temporaryNotice)
                        {
                            temporaryNotice = false;
                            Hide();
                            sampling.Interval = 95;
                        }
                        if (trayMenu != null && trayMenu.Visible) trayMenu.Close();
                        DwmFlush();
                        SampleFrame();
                        if (stitcher.Unmatched && !partialWasShown)
                        {
                            if (tray != null) tray.ShowBalloonTip(1600, "最后一段未接上",
                                "请稍微往回滚，或再次选择完成以保留已拼接的部分。", ToolTipIcon.Info);
                            return;
                        }
                    }
                    StopControls();
                    completion.TrySetResult(accept);
                }
                catch (Exception ex) { StopControls(); completion.TrySetException(ex); }
            }

            private void StopControls()
            {
                ending = true;
                sampling.Stop();
                if (tray != null) tray.Visible = false;
                Hide();
                ReleaseKeys();
            }

            private void ReleaseKeys()
            {
                if (IsHandleCreated)
                {
                    if (finishKey) UnregisterHotKey(Handle, FinishId);
                    if (cancelKey) UnregisterHotKey(Handle, CancelId);
                }
                finishKey = cancelKey = false;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams p = base.CreateParams;
                    p.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    return p;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                if (m.Msg == 0x0312 && (m.WParam.ToInt32() == FinishId || m.WParam.ToInt32() == CancelId))
                {
                    End(m.WParam.ToInt32() == FinishId);
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                Art.Init(g, zoom);
                float available = ClientSize.Width / zoom - 235;
                Art.Dot(g, stitcher.LimitReached || stitcher.Unmatched ? Color.Gold : Art.Mint, 12, 17, 8);
                Art.Label(g, "滚动截图  ·  " + height + " px", 28, 6, available, 29, 15, Color.White, true, StringAlignment.Near);
                Art.Label(g, status, 12, 32, available + 16, 24, 12, Art.Lilac, false, StringAlignment.Near);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                End(false);
                base.OnFormClosed(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Application.ApplicationExit -= OnApplicationExit;
                    ReleaseKeys();
                    sampling.Dispose();
                    if (tray != null) tray.Dispose();
                    if (trayMenu != null) trayMenu.Dispose();
                    completion.TrySetResult(false);
                }
                base.Dispose(disposing);
            }
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr child, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
