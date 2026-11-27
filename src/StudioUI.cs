using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal static class Art
    {
        internal static void Init(Graphics g, float zoom)
        {
            g.ScaleTransform(zoom, zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(r * 2, Math.Min(w, h));
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        internal static void Box(Graphics g, float x, float y, float w, float h, float r, Color fill, Color border, float stroke = 1)
        {
            using (GraphicsPath p = Rounded(x, y, w, h, r))
            {
                if (fill.A > 0) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border.A > 0) using (Pen pen = new Pen(border, stroke)) g.DrawPath(pen, p);
            }
        }

        internal static void DrawCover(Graphics g, Rectangle frame, StudioSkin skin, Image mascot, bool thumbnail)
        {
            GraphicsState start = g.Save();
            g.TranslateTransform(frame.X, frame.Y);
            int w = frame.Width, h = frame.Height;
            Rectangle bounds = new Rectangle(0, 0, w, h);
            using (GraphicsPath shape = Rounded(0, 0, w, h, thumbnail ? 8 : 19))
            using (var wash = new LinearGradientBrush(bounds, skin.CoverStart, skin.CoverEnd, 0f))
            using (var border = new Pen(skin.Line))
            {
                g.FillPath(wash, shape);
                GraphicsState inside = g.Save();
                g.SetClip(shape, CombineMode.Intersect);
                int left = thumbnail ? 0 : w / 2;
                if (skin == StudioSkin.Strawberry)
                {
                    int step = thumbnail ? 9 : 15;
                    using (var ribbon = new SolidBrush(Color.FromArgb(42, skin.Pattern)))
                    {
                        for (int x = left; x < w; x += step) g.FillRectangle(ribbon, x, 0, step/3, h);
                        for (int y = 4; y < h; y += step) g.FillRectangle(ribbon, left, y, w-left, step/3);
                    }
                }
                else
                {
                    int wave = thumbnail ? 21 : 51;
                    float amplitude = thumbnail ? 4 : 9;
                    using (var ripple = new Pen(Color.FromArgb(115, Color.White), thumbnail ? 1.4f : 2.8f))
                        for (int y = h/3; y < h+12; y += thumbnail ? 10 : 16)
                            for (int x = left; x < w; x += wave)
                                g.DrawBezier(ripple, x,y, x+wave*.4f,y-amplitude, x+wave*.6f,y+amplitude, x+wave,y);
                }
                if (!thumbnail)
                {
                    // Continue the underlying gradient at the edge of the pattern.
                    Rectangle fadeBounds = new Rectangle(left-1, 0, 95, h);
                    Color blend = Color.FromArgb((skin.CoverStart.R+skin.CoverEnd.R)/2,
                        (skin.CoverStart.G+skin.CoverEnd.G)/2, (skin.CoverStart.B+skin.CoverEnd.B)/2);
                    using (var fade = new LinearGradientBrush(fadeBounds, blend, Color.FromArgb(0, blend), 0f))
                        g.FillRectangle(fade, left, 0, fadeBounds.Width-2, h);
                    Spark(g, w*.68f, h*.32f, 6, Color.FromArgb(255, 226, 142), skin.NoteInk);
                    Spark(g, w*.922f, h*.72f, 4, Color.White, skin.Pattern);
                    if (skin == StudioSkin.SeaSalt)
                    {
                        using (var bubble = new Pen(Color.FromArgb(180, skin.Pattern), 1.2f))
                        {
                            g.DrawEllipse(bubble, w*.71f, h*.68f, 7, 7);
                            g.DrawEllipse(bubble, w*.91f, h*.18f, 10, 10);
                        }
                    }
                    else Dot(g, skin.Pattern, w*.71f, h*.69f, 4);
                }
                if (mascot != null)
                {
                    float scale = Math.Min((thumbnail ? w-6 : 125f) / mascot.Width, (h-4f) / mascot.Height);
                    float width = mascot.Width * scale, height = mascot.Height * scale;
                    float center = thumbnail ? w/2f : w*.806f;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(mascot, new RectangleF(center-width/2, (h-height)/2, width, height));
                }
                g.Restore(inside);
                g.DrawPath(border, shape);
            }
            g.Restore(start);
        }

        private static void Spark(Graphics g, float x, float y, float r, Color fill, Color outline)
        {
            float s = r * .31f;
            PointF[] pts = { new PointF(x, y-r), new PointF(x+s, y-s), new PointF(x+r, y),
                new PointF(x+s, y+s), new PointF(x, y+r), new PointF(x-s, y+s), new PointF(x-r, y), new PointF(x-s, y-s) };
            using (var brush = new SolidBrush(fill)) g.FillPolygon(brush, pts);
            using (var pen = new Pen(outline, 1)) { pen.LineJoin = LineJoin.Round; g.DrawPolygon(pen, pts); }
        }

        internal static void Label(Graphics g, string value, float x, float y, float w, float h,
                                   float size, Color color, bool bold, StringAlignment alignment)
        {
            using (Font font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat f = new StringFormat(StringFormatFlags.NoWrap))
            {
                f.Alignment = alignment;
                f.LineAlignment = StringAlignment.Center;
                f.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(value, font, brush, new RectangleF(x, y, w, h), f);
            }
        }

        internal static void Dot(Graphics g, Color color, float x, float y, float size)
        {
            using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, x, y, size, size);
        }

        internal static void Icon(Graphics g, string kind, float x, float y, float size, Color color)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 24f, size / 24f);
            using (Pen pen = new Pen(color, 1.7f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case "palette":
                        using (var contour = new GraphicsPath())
                        {
                            contour.AddBezier(20, 14, 16, 17, 13, 10, 12, 15);
                            contour.AddBezier(12, 15, 11, 17, 16, 19, 13, 21);
                            contour.AddBezier(13, 21, 6, 23, 1, 16, 3, 10);
                            contour.AddBezier(3, 10, 5, 3, 14, 1, 19, 6);
                            contour.AddBezier(19, 6, 22, 9, 23, 12, 20, 14);
                            g.DrawPath(pen, contour);
                        }
                        Dot(g, color, 6, 10, 2.6f); Dot(g, color, 10, 6, 2.6f); Dot(g, color, 15, 7, 2.6f);
                        break;
                    case "move":
                        g.DrawLine(pen, 3, 12, 21, 12);
                        g.DrawLine(pen, 12, 3, 12, 21);
                        g.DrawLines(pen, new[]{new Point(6,9),new Point(3,12),new Point(6,15)});
                        g.DrawLines(pen, new[]{new Point(18,9),new Point(21,12),new Point(18,15)});
                        g.DrawLines(pen, new[]{new Point(9,6),new Point(12,3),new Point(15,6)});
                        g.DrawLines(pen, new[]{new Point(9,18),new Point(12,21),new Point(15,18)});
                        break;
                    case "box":
                        g.DrawRectangle(pen, 3, 5, 18, 14);
                        break;
                    case "arrow":
                        g.DrawLine(pen, 4, 20, 20, 4);
                        g.DrawLines(pen, new[]{new Point(10,4),new Point(20,4),new Point(20,14)});
                        break;
                    case "text":
                        g.DrawLine(pen, 4, 5, 20, 5);
                        g.DrawLine(pen, 12, 5, 12, 20);
                        g.DrawLine(pen, 8, 20, 16, 20);
                        break;
                    case "undo":
                        g.DrawLines(pen, new[]{new Point(8,4),new Point(3,9),new Point(8,14)});
                        g.DrawLine(pen, 4, 9, 13, 9);
                        g.DrawBezier(pen, 13, 9, 21, 9, 22, 16, 18, 20);
                        break;
                    case "region":
                        g.DrawLines(pen, new PointF[]{new PointF(3,9),new PointF(3,3),new PointF(9,3)});
                        g.DrawLines(pen, new PointF[]{new PointF(15,3),new PointF(21,3),new PointF(21,9)});
                        g.DrawLines(pen, new PointF[]{new PointF(21,15),new PointF(21,21),new PointF(15,21)});
                        g.DrawLines(pen, new PointF[]{new PointF(9,21),new PointF(3,21),new PointF(3,15)});
                        break;
                    case "screen":
                        Box(g, 2.5f, 3.5f, 19, 13.5f, 2.7f, Color.Transparent, color, 1.7f);
                        g.DrawLine(pen, 12, 17, 12, 20.5f);
                        g.DrawLine(pen, 8, 20.5f, 16, 20.5f);
                        break;
                    case "scroll":
                        g.DrawLines(pen, new PointF[]{new PointF(6,3),new PointF(3,3),new PointF(3,21),new PointF(18,21),new PointF(18,18)});
                        g.DrawLine(pen, 7, 8, 12, 8);
                        g.DrawLine(pen, 7, 12, 11, 12);
                        g.DrawLine(pen, 18, 2, 18, 14);
                        g.DrawLines(pen, new PointF[]{new PointF(14,10),new PointF(18,14),new PointF(22,10)});
                        break;
                    case "photo":
                        g.DrawPolygon(pen, new PointF[]{new PointF(2,7),new PointF(7,7),new PointF(9,4),new PointF(15,4),new PointF(17,7),new PointF(22,7),new PointF(22,20),new PointF(2,20)});
                        g.DrawEllipse(pen, 8, 9.5f, 8, 8);
                        break;
                    case "video":
                        Box(g, 2.5f, 6, 13, 12, 2.7f, Color.Transparent, color, 1.7f);
                        g.DrawLines(pen, new PointF[]{new PointF(16,10),new PointF(21.5f,7),new PointF(21.5f,17),new PointF(16,14)});
                        break;
                    case "clipboard":
                        Box(g, 4, 4.5f, 16, 17, 3, Color.Transparent, color, 1.7f);
                        Box(g, 8.5f, 2.5f, 7, 4.2f, 2, StudioSkin.Current.Note, color, 1.5f);
                        g.DrawLine(pen, 8, 11, 16, 11);
                        g.DrawLine(pen, 8, 15, 13, 15);
                        break;
                    case "cursor":
                        g.DrawPolygon(pen, new PointF[]{new PointF(5,2.5f),new PointF(20,13),new PointF(13,14),new PointF(10,21),new PointF(5,2.5f)});
                        break;
                    case "microphone":
                        Box(g, 8.5f, 2, 7, 12, 3.5f, Color.Transparent, color, 1.7f);
                        g.DrawArc(pen, 5, 5, 14, 13, 0, 180);
                        g.DrawLine(pen, 12, 18, 12, 21);
                        g.DrawLine(pen, 8.5f, 21, 15.5f, 21);
                        break;
                    case "clip":
                        GraphicsState turn = g.Save();
                        g.TranslateTransform(12, 12);
                        g.RotateTransform(36);
                        g.DrawArc(pen, -5, -9, 10, 10, 180, 180);
                        g.DrawLine(pen, 5, -4, 5, 4);
                        g.DrawArc(pen, -2.5f, .5f, 7.5f, 7.5f, 0, 180);
                        g.DrawLine(pen, -2.5f, 4, -2.5f, -3);
                        g.DrawLine(pen, -5, -4, -5, 3);
                        g.Restore(turn);
                        break;
                    case "check":
                        g.DrawLines(pen, new PointF[]{new PointF(5,12),new PointF(10,17),new PointF(19,7)});
                        break;
                }
            }
            g.Restore(state);
        }
    }

    internal enum ButtonKind { Tab, Scope, Switch, Primary, Link, Skin }

    internal sealed class StudioButton : Button
    {
        internal ButtonKind Kind;
        internal string Glyph = "", Detail = "";
        internal Color Surface = StudioSkin.Current.Paper;
        internal bool Selected;
        internal float Zoom = 1;
        private bool hover, armed;

        internal StudioButton(ButtonKind kind, string text, string glyph)
        {
            Kind = kind; Text = text; Glyph = glyph;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = armed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); armed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); armed = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            StudioSkin skin = StudioSkin.Current;
            g.Clear(Surface);
            Art.Init(g, Zoom);
            float w = Width / Zoom, h = Height / Zoom;
            bool video = Glyph == "video";
            Color accent = video ? skin.Secondary : skin.Accent;
            Color tint = video ? skin.SecondaryTint : skin.Tint;
            if (Kind == ButtonKind.Skin)
            {
                Art.Box(g, 1, 1, w-2, h-2, h/2, hover || armed ? tint : Color.White, hover ? accent : skin.Line);
                Art.Icon(g, Glyph, 10, (h-17)/2, 17, accent);
                Art.Label(g, Text, 31, 0, 33, h, 13.5f, accent, true, StringAlignment.Near);
                using (var arrow = new Pen(accent, 1.4f))
                    g.DrawLines(arrow, new[] { new PointF(w-18,h/2-2), new PointF(w-15,h/2+1), new PointF(w-12,h/2-2) });
            }
            else if (Kind == ButtonKind.Tab)
            {
                if (Selected || hover)
                    Art.Box(g, 1, 1, w-2, h-2, (h-2)/2, Selected ? Color.White : tint,
                            Selected ? skin.Line : Color.Transparent);
                Color ink = Selected ? accent : skin.Muted;
                Art.Icon(g, Glyph, w/2-27, (h-16)/2, 16, ink);
                Art.Label(g, Text, w/2-6, 0, 44, h, 14, ink, Selected, StringAlignment.Near);
            }
            else if (Kind == ButtonKind.Scope)
            {
                Color badge = Glyph == "screen" ? skin.Note : (Glyph == "scroll" ? skin.SecondaryTint : skin.Tint);
                Color ink = Glyph == "screen" ? skin.NoteInk : (Glyph == "scroll" ? skin.Secondary : skin.Accent);
                Art.Box(g, 1, 1, w-2, h-2, 13, Selected || hover ? badge : Color.White,
                        Selected || hover ? ink : skin.Line, Selected ? 1.7f : 1);
                Art.Box(g, 12, (h-34)/2, 34, 34, 11, Selected ? Color.White : badge, Color.Transparent);
                Art.Icon(g, Glyph, 19, (h-20)/2, 20, ink);
                Art.Label(g, Text, 53, h/2-23, w-78, 23, 15.5f, skin.Ink, true, StringAlignment.Near);
                Art.Label(g, Detail, 53, h/2+1, w-58, 20, 12.5f, skin.Muted, false, StringAlignment.Near);
                if (Selected)
                {
                    Art.Dot(g, ink, w-24, 10, 14);
                    Art.Icon(g, "check", w-23, 11, 12, Color.White);
                }
            }
            else if (Kind == ButtonKind.Switch)
            {
                if (hover) Art.Box(g, 1, 1, w-2, h-2, 9, skin.Paper, Color.Transparent);
                Art.Icon(g, Glyph, 5, (h-17)/2, 17, skin.Muted);
                Art.Label(g, Text, 28, 0, w-78, h, 13.5f, skin.Ink, false, StringAlignment.Near);
                Art.Box(g, w-41, (h-20)/2, 37, 20, 10, Selected ? skin.Accent : skin.OffTrack, Color.Transparent);
                Art.Dot(g, Color.White, w-(Selected ? 22 : 38), (h-15)/2, 15);
                if (Selected) Art.Icon(g, "check", w-21, (h-13)/2, 13, skin.Accent);
            }
            else if (Kind == ButtonKind.Primary)
            {
                float top = armed ? 3 : 1, mid = (h-4)/2 + top;
                Color fill = video ? skin.SecondaryFill : skin.PrimaryFill;
                if (hover) fill = ControlPaint.Light(fill, .18f);
                Art.Box(g, 1, 4, w-2, h-5, 15, accent, Color.Transparent);
                Art.Box(g, 1, top, w-2, h-5, 15, fill, accent, 1.5f);
                Art.Box(g, 15, mid-16, 32, 32, 11, tint, Color.Transparent);
                Art.Icon(g, Glyph, 21, mid-10, 20, skin.Ink);
                Art.Label(g, Text, 57, mid-19, w-203, 38, 17.5f, skin.Ink, true, StringAlignment.Near);
                float hintWidth = video ? 112 : (Detail == "F1" ? 50 : 85);
                Art.Box(g, w-15-hintWidth, mid-13, hintWidth, 26, 8, tint, Color.Transparent);
                Art.Label(g, Detail, w-15-hintWidth, mid-13, hintWidth, 26, 12, accent, true, StringAlignment.Center);
            }
            else
            {
                Art.Box(g, 1, 1, w-2, h-2, 8, hover ? skin.Tint : Color.White, skin.Line);
                Art.Label(g, Text, 0, 0, w, h, 13, accent, true, StringAlignment.Center);
            }
            if (Focused && ShowFocusCues)
                using (Pen p = new Pen(skin.Ink))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, 4, 4, w - 8, h - 8);
                }
        }
    }

    // Use the native radio group for keyboard navigation, focus and checked-state accessibility.
    internal sealed class OutputChoice : RadioButton
    {
        internal float Zoom = 1;
        private readonly string detail;
        private bool hover;

        internal OutputChoice(string title, string detail)
        {
            Text = title;
            this.detail = detail;
            AccessibleDescription = detail;
            AutoSize = false;
            Cursor = Cursors.Hand;
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            StudioSkin skin = StudioSkin.Current;
            g.Clear(BackColor);
            Art.Init(g, Zoom);
            float w = Width / Zoom, h = Height / Zoom;
            if (Checked || hover)
                Art.Box(g, 1, 1, w-2, h-2, 8, Checked ? skin.SecondaryTint : skin.Paper, Color.Transparent);
            using (var edge = new Pen(Checked ? skin.Secondary : skin.Muted, 1.4f))
                g.DrawEllipse(edge, 11, (h-14)/2, 14, 14);
            if (Checked) Art.Dot(g, skin.Secondary, 14.5f, (h-7)/2, 7);
            Art.Label(g, Text, 34, 0, 119, h, 14.5f, skin.Ink, Checked, StringAlignment.Near);
            Art.Label(g, detail, 156, 0, w-161, h, 12.5f, skin.Muted, false, StringAlignment.Near);
            if (Focused && ShowFocusCues)
                using (var focus = new Pen(skin.Secondary) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(focus, 3, 3, w-6, h-6);
        }
    }
}
