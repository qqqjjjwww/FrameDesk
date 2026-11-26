using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal static class Art
    {
        internal static readonly Color Paper = Color.FromArgb(246, 248, 246);
        internal static readonly Color Ink = Color.FromArgb(25, 47, 43);
        internal static readonly Color Muted = Color.FromArgb(82, 104, 97);
        internal static readonly Color Line = Color.FromArgb(211, 222, 215);
        internal static readonly Color Jade = Color.FromArgb(5, 103, 80);
        internal static readonly Color Mint = Color.FromArgb(232, 245, 237);
        internal static readonly Color Rail = Color.FromArgb(229, 235, 230);
        internal static readonly Color Lime = Color.FromArgb(194, 244, 157);

        internal static void Init(Graphics g, float zoom)
        {
            g.ScaleTransform(zoom, zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        internal static void Box(Graphics g, float x, float y, float w, float h, float r, Color fill, Color border)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                float d = Math.Min(r * 2, Math.Min(w, h));
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border.A > 0) using (Pen pen = new Pen(border, 1)) g.DrawPath(pen, p);
            }
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
                    case "brand":
                    case "region":
                        g.DrawLines(pen, new PointF[]{new PointF(3,9),new PointF(3,3),new PointF(9,3)});
                        g.DrawLines(pen, new PointF[]{new PointF(15,3),new PointF(21,3),new PointF(21,9)});
                        g.DrawLines(pen, new PointF[]{new PointF(21,15),new PointF(21,21),new PointF(15,21)});
                        g.DrawLines(pen, new PointF[]{new PointF(9,21),new PointF(3,21),new PointF(3,15)});
                        if (kind == "brand") Dot(g, Lime, 8.5f, 8.5f, 7);
                        break;
                    case "screen":
                        g.DrawRectangle(pen, 2.5f, 3.5f, 19, 13.5f);
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
                        g.DrawRectangle(pen, 2.5f, 6, 13, 12);
                        g.DrawLines(pen, new PointF[]{new PointF(16,10),new PointF(21.5f,7),new PointF(21.5f,17),new PointF(16,14)});
                        break;
                    case "folder":
                        g.DrawPolygon(pen, new PointF[]{new PointF(2,5),new PointF(9,5),new PointF(11,8),new PointF(22,8),new PointF(22,19),new PointF(2,19)});
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
            Color accent = Enabled ? Art.Jade : Art.Muted;
            Color hoverFill = Color.FromArgb(240, 246, 240);
            if (Kind == ButtonKind.Tab)
            {
                if (Selected || hover)
                    Art.Box(g, 0.8f, 0.8f, w - 1.6f, h - 1.6f, 8, Selected ? Color.White : hoverFill, Selected ? Art.Line : Color.Transparent);
                float left = w / 2 - 52;
                Art.Icon(g, Glyph, left, (h - 21) / 2, 21, Selected ? accent : Art.Muted);
                Art.Label(g, Text, left + 28, 0, 46, h, 16, Selected ? Art.Ink : Art.Muted, true, StringAlignment.Near);
                Art.Label(g, Detail, left + 73, 1, 46, h, 12, Art.Muted, false, StringAlignment.Near);
            }
            else if (Kind == ButtonKind.Scope)
            {
                Art.Box(g, 0.8f, 0.8f, w - 1.6f, h - 1.6f, 9,
                        Selected ? Art.Mint : (hover ? hoverFill : Color.White), Selected ? accent : Art.Line);
                Art.Icon(g, Glyph, 15, (h - 26) / 2, 26, Selected ? accent : Art.Muted);
                Art.Label(g, Text, 53, 10, w - 92, 25, 16, Art.Ink, true, StringAlignment.Near);
                Art.Label(g, Detail, 53, 36, w - 86, 22, 12.5f, Art.Muted, false, StringAlignment.Near);
                using (Pen p = new Pen(Selected ? accent : Art.Line, 1.5f))
                    g.DrawEllipse(p, w - 32, (h - 15) / 2, 15, 15);
                if (Selected) Art.Dot(g, accent, w - 28, (h - 7) / 2, 7);
            }
            else if (Kind == ButtonKind.Switch)
            {
                Art.Label(g, Text, 0, 0, w - 48, h, 14, Art.Ink, false, StringAlignment.Near);
                Art.Box(g, w - 40, (h - 22) / 2, 39, 22, 11,
                        Selected ? accent : Color.FromArgb(152, 168, 160), Color.Transparent);
                Art.Dot(g, Color.White, w - (Selected ? 20 : 37), (h - 16) / 2, 16);
            }
            else if (Kind == ButtonKind.Primary)
            {
                Color fill = armed ? Color.FromArgb(3, 72, 56) : (hover ? Color.FromArgb(5, 86, 67) : accent);
                Art.Box(g, 1, 1, w - 2, h - 2, 10, fill, Color.Transparent);
                Art.Icon(g, Glyph, 20, (h - 22) / 2, 22, Color.White);
                Art.Label(g, Text, 55, 0, w - 213, h, 17, Color.White, true, StringAlignment.Near);
                Art.Label(g, Detail, w - 207, 0, 186, h, 13, Color.FromArgb(219, 241, 225), false, StringAlignment.Far);
            }
            else
            {
                if (hover && Enabled) Art.Box(g, 1, 1, w - 2, h - 2, 6, Art.Mint, Color.Transparent);
                Art.Label(g, Text, 0, 0, w, h, 13, accent, false, StringAlignment.Center);
            }
            if (Focused && ShowFocusCues)
                using (Pen p = new Pen(Kind == ButtonKind.Primary ? Color.White : Art.Jade))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, 4, 4, w - 8, h - 8);
                }
        }
    }
}
