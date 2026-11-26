using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenStudio
{
    public sealed class RegionPicker : Form
    {
        private static readonly Color Accent = Color.FromArgb(124, 238, 215);
        private static readonly Color LabelFill = Color.FromArgb(26, 34, 43);
        private readonly Rectangle desktop;
        private readonly Bitmap snapshot;
        private readonly Rectangle hintMonitor;
        private Point origin;
        private Rectangle selection;
        private Rectangle? result;
        private bool dragging, canSwitchToScroll, scrolling;

        private RegionPicker(Rectangle desktop, Bitmap snapshot)
        {
            this.desktop = desktop;
            this.snapshot = snapshot;
            hintMonitor = LocalMonitor(Cursor.Position);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Bounds = desktop;
            Cursor = Cursors.Cross;
            AccessibleName = "框选屏幕区域";
        }

        public static Rectangle? Pick()
        {
            DwmFlush();
            Rectangle desktop = SystemInformation.VirtualScreen;
            bool scrolling = false;
            using (Bitmap snapshot = ScreenCapture.Grab(desktop, false))
                return Choose(desktop, snapshot, false, ref scrolling);
        }

        public static Bitmap PickImage(bool includeCursor)
        {
            bool scrolling = false;
            Rectangle? unused;
            return PickPhoto(includeCursor, false, ref scrolling, out unused);
        }

        internal static Bitmap PickPhoto(bool includeCursor, bool canSwitch, ref bool scrolling, out Rectangle? picked)
        {
            DwmFlush();
            Rectangle desktop = SystemInformation.VirtualScreen;
            using (Bitmap snapshot = ScreenCapture.Grab(desktop, includeCursor))
            {
                picked = Choose(desktop, snapshot, canSwitch, ref scrolling);
                return picked.HasValue && !scrolling ? CropSelection(snapshot, desktop, picked.Value) : null;
            }
        }

        internal static Bitmap CropSelection(Bitmap snapshot, Rectangle desktop, Rectangle selection)
        {
            selection.Offset(-desktop.X, -desktop.Y);
            return snapshot.Clone(selection, snapshot.PixelFormat);
        }

        private static Rectangle? Choose(Rectangle desktop, Bitmap snapshot, bool canSwitch, ref bool scrolling)
        {
            try
            {
                using (RegionPicker picker = new RegionPicker(desktop, snapshot))
                {
                    picker.canSwitchToScroll = canSwitch;
                    picker.scrolling = scrolling;
                    if (picker.ShowDialog() != DialogResult.OK) return null;
                    scrolling = picker.scrolling;
                    return picker.result;
                }
            }
            finally { DwmFlush(); }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Set the native bounds after WinForms has applied its initial screen constraints.
            SetWindowPos(Handle, new IntPtr(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (canSwitchToScroll && !dragging && keyData == Keys.S)
            {
                if ((msg.LParam.ToInt64() & 0x40000000L) == 0) scrolling = !scrolling;
                Invalidate();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            if (e.Button != MouseButtons.Left) return;
            origin = ClampPoint(e.Location);
            selection = new Rectangle(origin, new Size(1, 1));
            dragging = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            selection = SelectionAt(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging || e.Button != MouseButtons.Left) return;
            selection = SelectionAt(e.Location);
            dragging = false;
            Capture = false;
            if (selection.Width == 1 && selection.Height == 1) { Invalidate(); return; }
            selection.Offset(desktop.Location);
            result = selection;
            DialogResult = DialogResult.OK;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture) { dragging = false; Invalidate(); }
        }

        private Point ClampPoint(Point point)
        {
            return new Point(Math.Max(0, Math.Min(desktop.Width - 1, point.X)),
                             Math.Max(0, Math.Min(desktop.Height - 1, point.Y)));
        }

        private Rectangle SelectionAt(Point point)
        {
            Point end = ClampPoint(point);
            return Rectangle.FromLTRB(Math.Min(origin.X, end.X), Math.Min(origin.Y, end.Y),
                                      Math.Max(origin.X, end.X) + 1, Math.Max(origin.Y, end.Y) + 1);
        }

        private Rectangle LocalMonitor(Point screenPoint)
        {
            Rectangle monitor = Screen.FromPoint(screenPoint).Bounds;
            monitor.Offset(-desktop.X, -desktop.Y);
            return monitor;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle visible = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, snapshot.Size));
            if (visible.IsEmpty) return;
            g.DrawImage(snapshot, visible, visible, GraphicsUnit.Pixel);
            using (SolidBrush shade = new SolidBrush(Color.FromArgb(116, 5, 12, 18)))
                g.FillRectangle(shade, visible);

            float scale = Math.Max(1f, g.DpiX / 96f);
            if (!dragging)
            {
                string hint = canSwitchToScroll ? (scrolling ? "框选可滚动的内容    ·    S 切换为普通截图" :
                    "拖动鼠标选择区域    ·    S 切换为滚动截图") : "拖动鼠标选择区域";
                DrawLabel(g, hint + "    ·    Esc 取消", hintMonitor, Point.Empty, true, scale);
                return;
            }

            Rectangle clear = Rectangle.Intersect(visible, selection);
            if (clear.Width > 0 && clear.Height > 0) g.DrawImage(snapshot, clear, clear, GraphicsUnit.Pixel);
            using (Pen outline = new Pen(Accent, Math.Max(1, (int)Math.Round(2 * scale))))
            {
                outline.Alignment = PenAlignment.Inset;
                g.DrawRectangle(outline, selection.X, selection.Y, selection.Width - 1, selection.Height - 1);
            }
            string size = selection.Width + " × " + selection.Height + " px";
            Rectangle activeMonitor = LocalMonitor(Cursor.Position);
            Point anchor = new Point(Math.Max(activeMonitor.Left, selection.Left),
                                     Math.Max(activeMonitor.Top, selection.Top));
            DrawLabel(g, size, activeMonitor, anchor, false, scale);
        }

        private void DrawLabel(Graphics g, string text, Rectangle monitor, Point anchor, bool centered, float scale)
        {
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
            using (Font font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Point))
            {
                Size size = TextRenderer.MeasureText(g, text, font, Size.Empty, flags);
                int gap = (int)Math.Round(9 * scale);
                int width = size.Width + gap * 3, height = size.Height + gap * 2;
                int x = centered ? monitor.Left + (monitor.Width - width) / 2 : anchor.X;
                int y = centered ? monitor.Top + gap * 4 : anchor.Y - gap - height;
                if (!centered && y < monitor.Top + gap) y = anchor.Y + gap;
                x = Math.Max(monitor.Left + gap, Math.Min(monitor.Right - width - gap, x));
                y = Math.Max(monitor.Top + gap, Math.Min(monitor.Bottom - height - gap, y));
                Rectangle box = new Rectangle(x, y, width, height);
                using (SolidBrush fill = new SolidBrush(LabelFill)) g.FillRectangle(fill, box);
                using (SolidBrush accent = new SolidBrush(Accent)) g.FillRectangle(accent, box.X, box.Y, Math.Max(2, (int)(3 * scale)), box.Height);
                TextRenderer.DrawText(g, text, font, box, Color.White, flags);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
