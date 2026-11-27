using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenStudio
{
    // Owned by the UI thread. The windows contain only pixels outside the active capture areas.
    internal sealed class CaptureRegionGuide : IDisposable
    {
        private readonly List<ShadeWindow> windows = new List<ShadeWindow>();
        private Rectangle? recording, scrolling;
        private bool recordingIsSelection, takingPhoto, disposed;

        internal void SetRecording(Rectangle? area, bool selected = false)
        {
            recording = area;
            recordingIsSelection = selected;
            Refresh();
        }

        internal void SetScrolling(Rectangle? area)
        {
            scrolling = area;
            Refresh();
        }

        internal void SetTakingPhoto(bool value)
        {
            // F1 can run during F2. Keep the picker snapshot and annotation editor unobscured.
            takingPhoto = value;
            Refresh();
        }

        internal void Refresh()
        {
            CloseWindows();
            if (disposed) return;
            Rectangle? selection = scrolling ?? (recordingIsSelection && !takingPhoto ? recording : null);
            if (!selection.HasValue) return;
            try
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle display = screen.Bounds;
                    using (var outside = new Region(new Rectangle(Point.Empty, display.Size)))
                    using (var identity = new Matrix())
                    {
                        Rectangle local = selection.Value;
                        local.Offset(-display.X, -display.Y);
                        outside.Exclude(local);
                        if (scrolling.HasValue && recording.HasValue)
                        {
                            // An independent scroll capture must also keep the live video's pixels clear.
                            Rectangle liveVideo = recording.Value;
                            liveVideo.Offset(-display.X, -display.Y);
                            outside.Exclude(liveVideo);
                        }
                        if (outside.GetRegionScans(identity).Length == 0) continue;
                        var window = new ShadeWindow(display, local, outside);
                        windows.Add(window);
                        window.Show();
                    }
                }
                // A restored guide also goes behind any already visible recording/scroll controls.
                foreach (Form form in Application.OpenForms)
                    if (form.TopMost && form.Visible && !(form is ShadeWindow) && form.IsHandleCreated)
                        SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x13); // NOSIZE | NOMOVE | NOACTIVATE
            }
            catch (ExternalException ex)
            {
                // A monitor can disappear while its window is being created. Capture can continue without the guide.
                CloseWindows();
                Debug.WriteLine(ex);
            }
        }

        private void CloseWindows()
        {
            foreach (ShadeWindow window in windows) window.Dispose();
            windows.Clear();
        }

        public void Dispose()
        {
            disposed = true;
            CloseWindows();
        }

        private sealed class ShadeWindow : Form
        {
            private readonly Rectangle display, selection;

            internal ShadeWindow(Rectangle display, Rectangle selection, Region outside)
            {
                this.display = display;
                this.selection = selection;
                AutoScaleMode = AutoScaleMode.None;
                StartPosition = FormStartPosition.Manual;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(24, 24, 24);
                Opacity = .32;
                Bounds = display;
                Region = outside.Clone();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams p = base.CreateParams;
                    // A layered, mouse-transparent window passes input through to other processes, too.
                    p.ExStyle |= 0x08000000 | 0x00080000 | 0x80 | 0x20; // NOACTIVATE | LAYERED | TOOLWINDOW | TRANSPARENT
                    p.ClassStyle &= ~0x00020000; // no drop shadow across the cutout
                    return p;
                }
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                SetWindowPos(Handle, new IntPtr(-1), display.X, display.Y, display.Width, display.Height, 0x10);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Rectangle rim = selection;
                int width = Math.Max(2, (int)Math.Round(2 * e.Graphics.DpiX / 96F));
                rim.Inflate(width, width);
                // The native window region clips out the selection, including its very first/last pixel.
                using (var accent = new SolidBrush(StudioSkin.Current.PrimaryFill)) e.Graphics.FillRectangle(accent, rim);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // HTTRANSPARENT
                if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
                base.WndProc(ref m);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
