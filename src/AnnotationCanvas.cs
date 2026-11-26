using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ScreenStudio
{
    public static partial class AnnotationEditor
    {
        private sealed class CaptionInput : TextBox
        {
            internal bool Composing { get; private set; }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x010D) Composing = true; // WM_IME_STARTCOMPOSITION
                base.WndProc(ref m);
                if (m.Msg == 0x010E) Composing = false; // WM_IME_ENDCOMPOSITION
            }
        }

        private sealed class EditorCanvas : Panel
        {
            private readonly Bitmap original;
            private readonly VScrollBar pageScroll;
            private bool needsScroll;
            private int wheelRemainder;
            private readonly List<AnnotationShape> shapes = new List<AnnotationShape>();
            private readonly Stack<Change> history = new Stack<Change>();
            private readonly float strokeWidth;
            private readonly Font captionFont;
            private readonly StringFormat captionFormat;
            private CaptionInput input;
            private Font inputFont;
            private int selected = -1, editing = -1, dragged = -1;
            private PointF textAnchor, pressPoint, fixedCorner;
            private Point pressClient;
            private Grip dragGrip;
            private AnnotationShape preview;
            private bool moved;

            // A step stores only one annotation, never a copy of the screenshot.
            private sealed class Change
            {
                internal int Index;
                internal AnnotationShape Before;
                internal bool Removed;
            }

            internal Tool ActiveTool { get; private set; }
            internal event Action StateChanged;
            internal bool CanUndo { get { return history.Count > 0 || input != null || preview != null; } }
            internal bool HasSelection { get { return selected >= 0; } }
            internal ShapeKind? SelectionKind { get { return HasSelection ? (ShapeKind?)shapes[selected].Kind : null; } }
            internal bool EditingText { get { return input != null && input.Focused; } }
            internal bool Composing { get { return EditingText && input.Composing; } }

            internal EditorCanvas(Bitmap source, bool longImage)
            {
                original = source;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
                TabStop = true;
                Margin = Padding.Empty;
                BackColor = Color.FromArgb(42, 53, 49);
                float scale = Math.Min(2.7F, Math.Max(.75F, longImage ? source.Width / 1500F : source.Height / 1080F));
                strokeWidth = Math.Max(2F, 3F * scale);
                captionFont = new Font("Microsoft YaHei UI", 25 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                captionFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
                captionFormat.FormatFlags |= StringFormatFlags.NoWrap;
                Cursor = Cursors.Cross;
                if (longImage)
                {
                    pageScroll = new VScrollBar { Dock = DockStyle.Right, Visible = false, AccessibleName = "浏览长截图" };
                    pageScroll.ValueChanged += delegate { PlaceInput(); Invalidate(); };
                    pageScroll.Scroll += delegate { CommitText(); };
                    Controls.Add(pageScroll);
                    UpdateScrollRange();
                }
            }

            private float Inset { get { return Math.Min(14F, Math.Max(0, Math.Min(ClientSize.Width, ClientSize.Height) / 10F)); } }
            private float LongImageScale(int width) { return Math.Min(1F, Math.Max(1F, width - 2 * Inset) / original.Width); }

            private void UpdateScrollRange()
            {
                if (pageScroll == null) return;
                int height = Math.Max(1, ClientSize.Height);
                needsScroll = original.Height * LongImageScale(ClientSize.Width) + 2 * Inset > height + 1;
                pageScroll.Visible = needsScroll;
                if (!needsScroll) { pageScroll.Value = 0; return; }
                int width = ClientSize.Width - pageScroll.Width;
                int content = (int)Math.Ceiling(original.Height * LongImageScale(width) + 2 * Inset);
                pageScroll.Maximum = Math.Max(height, content) - 1;
                pageScroll.LargeChange = height;
                pageScroll.SmallChange = Math.Max(20, height / 14);
                int last = pageScroll.Maximum - pageScroll.LargeChange + 1;
                if (pageScroll.Value > last) pageScroll.Value = last;
            }

            private RectangleF Frame
            {
                get
                {
                    int width = ClientSize.Width - (needsScroll ? pageScroll.Width : 0);
                    float scale = pageScroll != null ? LongImageScale(width) :
                        Math.Min(2F, Math.Min(Math.Max(1, width - 2 * Inset) / original.Width,
                                             Math.Max(1, ClientSize.Height - 2 * Inset) / original.Height));
                    float w = original.Width * scale, h = original.Height * scale;
                    float top = needsScroll ? Inset - pageScroll.Value : (ClientSize.Height - h) / 2F;
                    return new RectangleF((width - w) / 2F, top, w, h);
                }
            }

            private PointF ImagePoint(Point client)
            {
                RectangleF frame = Frame;
                float scale = frame.Width / original.Width;
                return new PointF(Limit((client.X - frame.X) / scale, 0, original.Width - 1),
                                  Limit((client.Y - frame.Y) / scale, 0, original.Height - 1));
            }

            private static float Limit(float value, float low, float high)
            {
                return Math.Max(low, Math.Min(high, value));
            }

            private void Notify()
            {
                Invalidate();
                if (StateChanged != null) StateChanged();
            }

            internal void SwitchTool(Tool tool)
            {
                CommitText();
                ClearDrag();
                ActiveTool = tool;
                // Drawing tools start with an unobstructed canvas. Selection mode keeps the last mark active.
                if (tool != Tool.Select) selected = -1;
                Cursor = DefaultCursorForTool();
                Notify();
            }

            private Cursor DefaultCursorForTool()
            {
                if (ActiveTool == Tool.Select) return Cursors.Default;
                return ActiveTool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
            }

            private void ApplyChange(int index, AnnotationShape next)
            {
                bool adding = index == shapes.Count;
                history.Push(new Change { Index = index, Before = adding ? null : shapes[index], Removed = next == null });
                if (next == null) shapes.RemoveAt(index);
                else if (adding) shapes.Add(next);
                else shapes[index] = next;
                selected = next == null ? -1 : index;
            }

            internal void Undo()
            {
                if (!CancelInteraction() && history.Count != 0)
                {
                    Change step = history.Pop();
                    if (step.Removed) shapes.Insert(step.Index, step.Before);
                    else if (step.Before == null) shapes.RemoveAt(step.Index);
                    else shapes[step.Index] = step.Before;
                    selected = step.Before == null ? -1 : step.Index;
                }
                Focus();
                Notify();
            }

            internal void DeleteSelected()
            {
                if (input != null || preview != null || !HasSelection) return;
                ApplyChange(selected, null);
                Notify();
            }

            internal bool CancelInteraction()
            {
                if (input != null) CloseInput();
                else if (preview != null) ClearDrag();
                else return false;
                Focus();
                Notify();
                return true;
            }

            private void ClearDrag()
            {
                preview = null;
                dragged = -1;
                dragGrip = Grip.None;
                moved = false;
                if (Capture) Capture = false;
            }

            private void OpenInput(PointF point, int index)
            {
                textAnchor = point;
                editing = selected = index;
                input = new CaptionInput {
                    Multiline = true, AcceptsReturn = true, WordWrap = false,
                    BorderStyle = BorderStyle.FixedSingle, ImeMode = ImeMode.On,
                    ForeColor = MarkRed, BackColor = Color.White, MaxLength = 8000,
                    Text = index < 0 ? "" : shapes[index].Caption
                };
                Controls.Add(input);
                input.TextChanged += delegate { PlaceInput(); };
                PlaceInput();
                input.BringToFront();
                input.Focus();
                if (index >= 0) input.SelectAll();
                Notify();
            }

            private void PlaceInput()
            {
                if (input == null) return;
                RectangleF frame = Frame;
                float scale = frame.Width / original.Width;
                float size = Math.Max(12F, captionFont.Size * scale);
                if (inputFont == null || Math.Abs(inputFont.Size - size) > .2F)
                {
                    Font previous = inputFont;
                    inputFont = new Font(captionFont.FontFamily, size, captionFont.Style, GraphicsUnit.Pixel);
                    input.Font = inputFont;
                    if (previous != null) previous.Dispose();
                }
                int x = (int)(frame.X + textAnchor.X * scale), y = (int)(frame.Y + textAnchor.Y * scale);
                int width = Math.Min(Math.Max(1, ClientSize.Width), Math.Max(124, Math.Min(500, ClientSize.Width - x - 5)));
                int height = Math.Min(Math.Max(1, ClientSize.Height),
                    Math.Max(30, Math.Min(8, Math.Max(1, input.Lines.Length)) * input.Font.Height + 10));
                input.SetBounds(Math.Max(0, Math.Min(x, ClientSize.Width - width)),
                                Math.Max(0, Math.Min(y, ClientSize.Height - height)), width, height);
            }

            private void CloseInput()
            {
                CaptionInput old = input;
                input = null;
                editing = -1;
                Controls.Remove(old);
                old.Dispose();
                if (inputFont != null) { inputFont.Dispose(); inputFont = null; }
            }

            internal void CommitText()
            {
                if (input == null) return;
                int index = editing;
                string value = input.Text.TrimEnd();
                CloseInput();
                if (String.IsNullOrWhiteSpace(value))
                {
                    if (index >= 0) ApplyChange(index, null);
                }
                else if (index < 0 || shapes[index].Caption != value)
                {
                    var shape = new AnnotationShape { Kind = ShapeKind.Text, Caption = value, A = textAnchor };
                    using (Graphics g = CreateGraphics())
                    {
                        RectangleF bounds = shape.Bounds(g, captionFont, captionFormat);
                        shape.A = new PointF(Limit(textAnchor.X, 0, original.Width - bounds.Width - 1),
                                             Limit(textAnchor.Y, 0, original.Height - bounds.Height - 1));
                    }
                    ApplyChange(index < 0 ? shapes.Count : index, shape);
                }
                Notify();
            }

            private static readonly Grip[] CornerGrips = { Grip.TopLeft, Grip.TopRight, Grip.BottomLeft, Grip.BottomRight };
            private static readonly Grip[] ArrowGrips = { Grip.Start, Grip.End };
            private static readonly Grip[] NoGrips = {};

            private static Grip[] GripsFor(AnnotationShape shape)
            {
                return shape.Kind == ShapeKind.Box ? CornerGrips : (shape.Kind == ShapeKind.Arrow ? ArrowGrips : NoGrips);
            }

            private static PointF GripPoint(AnnotationShape shape, Grip grip)
            {
                if (grip == Grip.Start) return shape.A;
                if (grip == Grip.End) return shape.B;
                bool left = grip == Grip.TopLeft || grip == Grip.BottomLeft;
                bool top = grip == Grip.TopLeft || grip == Grip.TopRight;
                return new PointF(left ? Math.Min(shape.A.X, shape.B.X) : Math.Max(shape.A.X, shape.B.X),
                                  top ? Math.Min(shape.A.Y, shape.B.Y) : Math.Max(shape.A.Y, shape.B.Y));
            }

            private static Grip Opposite(Grip grip)
            {
                switch (grip)
                {
                    case Grip.TopLeft: return Grip.BottomRight;
                    case Grip.TopRight: return Grip.BottomLeft;
                    case Grip.BottomLeft: return Grip.TopRight;
                    default: return Grip.TopLeft;
                }
            }

            // Returns the topmost painted mark first. In selection mode a box is also movable from inside.
            private int Hit(PointF point, out Grip grip)
            {
                grip = Grip.None;
                float scale = Frame.Width / original.Width;
                using (Graphics g = CreateGraphics())
                {
                    float px = g.DpiX / 96F / scale;
                    if (HasSelection)
                    {
                        float best = 6.5F * px * 6.5F * px;
                        foreach (Grip handle in GripsFor(shapes[selected]))
                        {
                            PointF at = GripPoint(shapes[selected], handle);
                            float dx = point.X - at.X, dy = point.Y - at.Y;
                            if (dx * dx + dy * dy > best) continue;
                            best = dx * dx + dy * dy;
                            grip = handle;
                        }
                        if (grip != Grip.None) return selected;
                    }
                    for (int i = shapes.Count - 1; i >= 0; i--)
                        if (shapes[i].HitTest(g, point, 3.5F * px, captionFont, captionFormat, strokeWidth))
                        { grip = Grip.Body; return i; }
                    if (ActiveTool == Tool.Select)
                        for (int i = shapes.Count - 1; i >= 0; i--)
                            if (shapes[i].Kind == ShapeKind.Box && shapes[i].Bounds(g, captionFont, captionFormat).Contains(point))
                            { grip = Grip.Body; return i; }
                }
                return -1;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                CommitText();
                ClearDrag();
                Focus();
                if (!Frame.Contains(e.Location)) { selected = -1; Notify(); return; }
                pressPoint = ImagePoint(e.Location);
                pressClient = e.Location;
                int index = Hit(pressPoint, out dragGrip);
                if (index >= 0)
                {
                    selected = dragged = index;
                    AnnotationShape source = shapes[index];
                    if (source.Kind == ShapeKind.Text && e.Clicks == 2)
                    {
                        OpenInput(source.A, index);
                        return;
                    }
                    if (source.Kind == ShapeKind.Box && dragGrip != Grip.Body)
                        fixedCorner = GripPoint(source, Opposite(dragGrip));
                    preview = source.Clone();
                }
                else
                {
                    selected = -1;
                    if (ActiveTool == Tool.Select) { Notify(); return; }
                    if (ActiveTool == Tool.Text) { OpenInput(pressPoint, -1); return; }
                    preview = new AnnotationShape { Kind = ActiveTool == Tool.Box ? ShapeKind.Box : ShapeKind.Arrow,
                                                    A = pressPoint, B = pressPoint };
                }
                Capture = true;
                Notify();
            }

            private void DragTo(Point client)
            {
                if (preview == null) return;
                PointF point = ImagePoint(client);
                if (dragged < 0) { preview.B = point; return; }
                if (!moved && Math.Abs(client.X - pressClient.X) < Math.Max(2, SystemInformation.DragSize.Width / 2) &&
                    Math.Abs(client.Y - pressClient.Y) < Math.Max(2, SystemInformation.DragSize.Height / 2)) return;
                moved = true;
                AnnotationShape before = shapes[dragged];
                float dx = point.X - pressPoint.X, dy = point.Y - pressPoint.Y;
                if (dragGrip == Grip.Body)
                {
                    using (Graphics g = CreateGraphics())
                    {
                        RectangleF bounds = before.Bounds(g, captionFont, captionFormat);
                        dx = Limit(dx, -bounds.Left, original.Width - 1 - bounds.Right);
                        dy = Limit(dy, -bounds.Top, original.Height - 1 - bounds.Bottom);
                    }
                    preview.A = new PointF(before.A.X + dx, before.A.Y + dy);
                    if (before.Kind != ShapeKind.Text) preview.B = new PointF(before.B.X + dx, before.B.Y + dy);
                }
                else
                {
                    PointF gripPosition = GripPoint(before, dragGrip);
                    PointF target = new PointF(Limit(gripPosition.X + dx, 0, original.Width - 1),
                                               Limit(gripPosition.Y + dy, 0, original.Height - 1));
                    if (dragGrip == Grip.Start) preview.A = target;
                    else if (dragGrip == Grip.End) preview.B = target;
                    else { preview.A = fixedCorner; preview.B = target; }
                }
            }

            private static Cursor CursorFor(Grip grip)
            {
                switch (grip)
                {
                    case Grip.TopLeft:
                    case Grip.BottomRight: return Cursors.SizeNWSE;
                    case Grip.TopRight:
                    case Grip.BottomLeft: return Cursors.SizeNESW;
                    case Grip.Start:
                    case Grip.End: return Cursors.Cross;
                    default: return Cursors.SizeAll;
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (preview != null) { DragTo(e.Location); Invalidate(); return; }
                Grip grip;
                Cursor = Frame.Contains(e.Location) && Hit(ImagePoint(e.Location), out grip) >= 0 ?
                    CursorFor(grip) : DefaultCursorForTool();
            }

            private static bool HasGeometry(AnnotationShape shape)
            {
                float dx = shape.B.X - shape.A.X, dy = shape.B.Y - shape.A.Y;
                if (shape.Kind == ShapeKind.Box) return Math.Abs(dx) >= 2 && Math.Abs(dy) >= 2;
                return shape.Kind == ShapeKind.Text || dx * dx + dy * dy >= 9;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left || preview == null) return;
                DragTo(e.Location);
                if (HasGeometry(preview))
                {
                    if (preview.Kind == ShapeKind.Box)
                    {
                        PointF topLeft = GripPoint(preview, Grip.TopLeft);
                        PointF bottomRight = GripPoint(preview, Grip.BottomRight);
                        preview.A = topLeft;
                        preview.B = bottomRight;
                    }
                    if (dragged < 0) ApplyChange(shapes.Count, preview);
                    else if (moved && (shapes[dragged].A != preview.A || shapes[dragged].B != preview.B))
                        ApplyChange(dragged, preview);
                }
                ClearDrag();
                Notify();
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (preview != null && !Capture) { ClearDrag(); Notify(); }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                UpdateScrollRange();
                PlaceInput();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (!needsScroll || preview != null) return;
                wheelRemainder += e.Delta;
                int step = wheelRemainder / SystemInformation.MouseWheelScrollDelta;
                if (step == 0) return;
                wheelRemainder %= SystemInformation.MouseWheelScrollDelta;
                CommitText();
                int lines = SystemInformation.MouseWheelScrollLines;
                int distance = lines < 0 ? pageScroll.LargeChange : pageScroll.SmallChange * lines;
                int max = pageScroll.Maximum - pageScroll.LargeChange + 1;
                pageScroll.Value = Math.Max(0, Math.Min(max, pageScroll.Value - step * distance));
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                RectangleF frame = Frame;
                Graphics g = e.Graphics;
                g.InterpolationMode = frame.Width < original.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(original, frame, new RectangleF(0, 0, original.Width, original.Height), GraphicsUnit.Pixel);
                GraphicsState state = g.Save();
                try
                {
                    g.SetClip(frame);
                    g.TranslateTransform(frame.X, frame.Y);
                    float scale = frame.Width / original.Width;
                    g.ScaleTransform(scale, scale);
                    PaintMarks(g, true);
                    if (HasSelection && input == null) PaintSelection(g, preview ?? shapes[selected], g.DpiX / 96F / scale);
                }
                finally { g.Restore(state); }
            }

            private void PaintSelection(Graphics g, AnnotationShape shape, float px)
            {
                using (var edge = new Pen(Jade, 1.35F * px))
                using (var white = new SolidBrush(Color.White))
                {
                    if (shape.Kind == ShapeKind.Text)
                    {
                        edge.DashStyle = DashStyle.Dash;
                        RectangleF bounds = shape.Bounds(g, captionFont, captionFormat);
                        bounds.Inflate(2 * px, 2 * px);
                        g.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    }
                    foreach (Grip handle in GripsFor(shape))
                    {
                        PointF point = GripPoint(shape, handle);
                        float side = 7 * px;
                        RectangleF r = new RectangleF(point.X - side / 2, point.Y - side / 2, side, side);
                        if (shape.Kind == ShapeKind.Arrow) { g.FillEllipse(white, r); g.DrawEllipse(edge, r); }
                        else { g.FillRectangle(white, r); g.DrawRectangle(edge, r.X, r.Y, side, side); }
                    }
                }
            }

            private void PaintMarks(Graphics g, bool interactive)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var pen = new Pen(MarkRed, strokeWidth))
                using (var brush = new SolidBrush(MarkRed))
                {
                    for (int i = 0; i < shapes.Count; i++)
                    {
                        if (interactive && input != null && i == editing) continue;
                        AnnotationShape shape = interactive && i == dragged && preview != null ? preview : shapes[i];
                        shape.Paint(g, pen, brush, captionFont, captionFormat);
                    }
                    if (interactive && preview != null && dragged < 0) preview.Paint(g, pen, brush, captionFont, captionFormat);
                }
            }

            internal Bitmap RenderOriginalSize()
            {
                Bitmap result = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
                try
                {
                    using (Graphics g = Graphics.FromImage(result))
                    {
                        g.DrawImageUnscaled(original, 0, 0);
                        PaintMarks(g, false);
                    }
                    return result;
                }
                catch { result.Dispose(); throw; }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (input != null) CloseInput();
                    captionFont.Dispose();
                    captionFormat.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
