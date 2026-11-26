using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal static class Art
    {
        internal static readonly Color Paper = Color.FromArgb(255, 250, 246);
        internal static readonly Color Ink = Color.FromArgb(60, 44, 62);
        internal static readonly Color Muted = Color.FromArgb(107, 89, 104);
        internal static readonly Color Line = Color.FromArgb(226, 209, 215);
        internal static readonly Color Berry = Color.FromArgb(135, 47, 81);
        internal static readonly Color Blush = Color.FromArgb(255, 233, 238);
        internal static readonly Color Pink = Color.FromArgb(255, 175, 195);
        internal static readonly Color Violet = Color.FromArgb(91, 65, 135);
        internal static readonly Color Lilac = Color.FromArgb(239, 231, 252);
        internal static readonly Color Iris = Color.FromArgb(218, 200, 250);
        internal static readonly Color Note = Color.FromArgb(255, 245, 218);
        internal static readonly Color Mint = Color.FromArgb(182, 234, 205);
        internal static readonly Color Leaf = Color.FromArgb(39, 101, 77);

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

        internal static void DrawCover(Graphics g, Image mascot)
        {
            Rectangle frame = new Rectangle(24, 16, 608, 106);
            using (GraphicsPath shape = Rounded(frame.X, frame.Y, frame.Width, frame.Height, 20))
            using (var wash = new LinearGradientBrush(frame, Color.FromArgb(255, 229, 225), Color.FromArgb(229, 217, 250), 0f))
            using (var grid = new Pen(Color.FromArgb(122, 255, 253, 250), 1))
            using (var border = new Pen(Color.FromArgb(220, 194, 209)))
            {
                g.FillPath(wash, shape);
                GraphicsState saved = g.Save();
                g.SetClip(shape, CombineMode.Intersect);
                for (int x = 318; x < frame.Right; x += 14) g.DrawLine(grid, x, frame.Top, x, frame.Bottom);
                for (int y = 24; y < frame.Bottom; y += 14) g.DrawLine(grid, 318, y, frame.Right, y);
                // Fade the edge of the graph paper into the title area.
                using (var fade = new LinearGradientBrush(new Rectangle(317, 16, 78, 106),
                    Color.FromArgb(243, 223, 238), Color.FromArgb(0, 243, 223, 238), 0f))
                    g.FillRectangle(fade, 318, 16, 76, 106);
                g.Restore(saved);
                g.DrawPath(border, shape);
            }
            Spark(g, 417, 49, 8, Color.FromArgb(255, 212, 114), Color.FromArgb(153, 111, 77));
            Spark(g, 584, 85, 6, Color.White, Color.FromArgb(165, 131, 183));
            Dot(g, Color.FromArgb(227, 143, 164), 429, 88, 5);
            Dot(g, Color.FromArgb(255, 250, 228), 581, 38, 7);
            if (mascot != null)
            {
                float scale = Math.Min(150f / mascot.Width, 104f / mascot.Height);
                float w = mascot.Width * scale, h = mascot.Height * scale;
                GraphicsState saved = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(mascot, new RectangleF(509 - w / 2, 69 - h / 2, w, h));
                g.Restore(saved);
            }
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
                        Box(g, 8.5f, 2.5f, 7, 4.2f, 2, Note, color, 1.5f);
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

    internal enum ButtonKind { Tab, Scope, Switch, Primary, Link }

    internal sealed class StudioButton : Button
    {
        internal ButtonKind Kind;
        internal string Glyph = "", Detail = "";
        internal Color Surface = Art.Paper;
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
            g.Clear(Surface);
            Art.Init(g, Zoom);
            float w = Width / Zoom, h = Height / Zoom;
            bool video = Glyph == "video";
            Color accent = video ? Art.Violet : Art.Berry;
            Color tint = video ? Art.Lilac : Art.Blush;
            if (Kind == ButtonKind.Tab)
            {
                float top = armed ? 2.5f : 1;
                if (Selected) Art.Box(g, 1, 3, w-2, h-4, 14, accent, Color.Transparent);
                Art.Box(g, 1, top, w-2, h-5, 14, Selected || hover ? tint : Color.White,
                        Selected || hover ? accent : Art.Line, Selected ? 1.6f : 1);
                float mid = (h-3)/2 + top-1;
                Art.Icon(g, Glyph, 18, mid-10, 20, accent);
                Art.Label(g, Text, 46, mid-16, w-98, 32, 16, Art.Ink, true, StringAlignment.Near);
                Art.Box(g, w-48, mid-10, 31, 20, 6, Selected ? Color.White : tint, Color.Transparent);
                Art.Label(g, Detail, w-48, mid-10, 31, 20, 11.5f, accent, true, StringAlignment.Center);
            }
            else if (Kind == ButtonKind.Scope)
            {
                Color badge = Glyph == "screen" ? Art.Note : (Glyph == "scroll" ? Art.Lilac : Art.Blush);
                Color ink = Glyph == "screen" ? Color.FromArgb(127, 83, 46) : (Glyph == "scroll" ? Art.Violet : Art.Berry);
                Art.Box(g, 1, 1, w-2, h-2, 13, Selected || hover ? badge : Color.White,
                        Selected || hover ? ink : Art.Line, Selected ? 1.7f : 1);
                Art.Box(g, 12, (h-34)/2, 34, 34, 11, Selected ? Color.White : badge, Color.Transparent);
                Art.Icon(g, Glyph, 19, (h-20)/2, 20, ink);
                Art.Label(g, Text, 53, 10, w-78, 23, 15.5f, Art.Ink, true, StringAlignment.Near);
                Art.Label(g, Detail, 53, 34, w-58, 20, 12.5f, Art.Muted, false, StringAlignment.Near);
                if (Selected)
                {
                    Art.Dot(g, ink, w-24, 10, 14);
                    Art.Icon(g, "check", w-23, 11, 12, Color.White);
                }
            }
            else if (Kind == ButtonKind.Switch)
            {
                if (hover) Art.Box(g, 1, 1, w-2, h-2, 9, Art.Paper, Color.Transparent);
                Art.Icon(g, Glyph, 5, (h-17)/2, 17, Art.Muted);
                Art.Label(g, Text, 28, 0, w-78, h, 13.5f, Art.Ink, false, StringAlignment.Near);
                Art.Box(g, w-41, (h-20)/2, 37, 20, 10, Selected ? Art.Berry : Color.FromArgb(191, 178, 189), Color.Transparent);
                Art.Dot(g, Color.White, w-(Selected ? 22 : 38), (h-15)/2, 15);
                if (Selected) Art.Icon(g, "check", w-21, (h-13)/2, 13, Art.Berry);
            }
            else if (Kind == ButtonKind.Primary)
            {
                float top = armed ? 3 : 1, mid = (h-4)/2 + top;
                Color fill = video ? Art.Iris : Art.Pink;
                if (hover) fill = ControlPaint.Light(fill, .18f);
                Art.Box(g, 1, 4, w-2, h-5, 15, accent, Color.Transparent);
                Art.Box(g, 1, top, w-2, h-5, 15, fill, accent, 1.5f);
                Art.Box(g, 15, mid-16, 32, 32, 11, tint, Color.Transparent);
                Art.Icon(g, Glyph, 21, mid-10, 20, Art.Ink);
                Art.Label(g, Text, 57, mid-19, w-203, 38, 17.5f, Art.Ink, true, StringAlignment.Near);
                float hintWidth = video ? 112 : 85;
                Art.Box(g, w-15-hintWidth, mid-13, hintWidth, 26, 8, tint, Color.Transparent);
                Art.Label(g, Detail, w-15-hintWidth, mid-13, hintWidth, 26, 12, accent, true, StringAlignment.Center);
            }
            else
            {
                Art.Box(g, 1, 1, w-2, h-2, 8, hover ? Art.Blush : Color.White, Art.Line);
                Art.Label(g, Text, 0, 0, w, h, 13, accent, true, StringAlignment.Center);
            }
            if (Focused && ShowFocusCues)
                using (Pen p = new Pen(Art.Ink))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, 4, 4, w - 8, h - 8);
                }
        }
    }
}
