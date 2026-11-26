using System;
using System.Drawing;

namespace ScreenStudio
{
    internal enum ShapeKind { Box, Arrow, Text }

    internal enum Grip { None, Body, TopLeft, TopRight, BottomLeft, BottomRight, Start, End }

    internal sealed class AnnotationShape
    {
        internal ShapeKind Kind { get; set; }
        internal PointF A { get; set; }
        internal PointF B { get; set; }
        internal string Caption { get; set; }
        // Wheel notches relative to the default pen or regular font. Control points are independent of thickness.
        internal int Thickness { get; set; }

        internal static int AdjustThickness(ShapeKind kind, int current, int steps)
        {
            // Captions use the font family's light, regular and bold faces at the same pixel size.
            if (kind == ShapeKind.Text) return Math.Max(-1, Math.Min(1, current + steps));
            return Math.Max(-2, Math.Min(12, current + steps));
        }

        private float LineWidth(float baseWidth)
        {
            return Math.Max(1F, baseWidth * (1F + Thickness / 3F));
        }

        internal AnnotationShape Clone()
        {
            return (AnnotationShape)MemberwiseClone();
        }

        // Control points are defined in original-image pixels, independent of the preview scale.
        internal RectangleF Bounds(Graphics g, Font font, StringFormat format)
        {
            if (Kind == ShapeKind.Text)
            {
                SizeF size = String.IsNullOrEmpty(Caption) ? SizeF.Empty :
                    g.MeasureString(Caption, font, Int32.MaxValue, format);
                return new RectangleF(A, size);
            }
            return RectangleF.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y),
                                       Math.Max(A.X, B.X), Math.Max(A.Y, B.Y));
        }

        internal void Paint(Graphics g, Pen pen, Brush brush, Font font, StringFormat format)
        {
            switch (Kind)
            {
                case ShapeKind.Text:
                    if (!String.IsNullOrEmpty(Caption)) g.DrawString(Caption, font, brush, A, format);
                    break;
                case ShapeKind.Box:
                    RectangleF bounds = Bounds(g, font, format);
                    using (var border = (Pen)pen.Clone())
                    {
                        border.Width = LineWidth(pen.Width);
                        g.DrawRectangle(border, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                    }
                    break;
                case ShapeKind.Arrow:
                    PointF[] outline = ArrowOutline(pen.Width);
                    if (outline != null) g.FillPolygon(brush, outline);
                    break;
            }
        }

        internal bool HitTest(Graphics g, PointF pos, float tolerance, Font font, StringFormat format, float strokeWidth)
        {
            if (Kind == ShapeKind.Arrow)
            {
                PointF[] outline = ArrowOutline(strokeWidth);
                PointF nearest = outline == null ? A : NearestOnArrow(pos, outline);
                return DistanceSquared(pos, nearest) <= tolerance * tolerance;
            }
            RectangleF bounds = Bounds(g, font, format);
            if (Kind == ShapeKind.Text)
            {
                bounds.Inflate(tolerance, tolerance);
                return !String.IsNullOrEmpty(Caption) && bounds.Contains(pos);
            }
            float reach = tolerance + LineWidth(strokeWidth) * .5F;
            PointF topLeft = bounds.Location, topRight = new PointF(bounds.Right, bounds.Top);
            PointF bottomLeft = new PointF(bounds.Left, bounds.Bottom), bottomRight = new PointF(bounds.Right, bounds.Bottom);
            return NearSegment(pos, topLeft, topRight, reach) || NearSegment(pos, topRight, bottomRight, reach) ||
                   NearSegment(pos, bottomRight, bottomLeft, reach) || NearSegment(pos, bottomLeft, topLeft, reach);
        }

        private PointF[] ArrowOutline(float strokeWidth)
        {
            float dx = B.X - A.X, dy = B.Y - A.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < .001F) return null;
            float unitX = dx / length, unitY = dy / length;
            float headLength = Math.Min(length * .65F, Math.Max(8F, strokeWidth * 5F));
            float baseRadius = Math.Min(strokeWidth * .60F, headLength * .26F);
            float shaftRadius = baseRadius * (LineWidth(strokeWidth) / strokeWidth);
            float headRadius = headLength * .52F + 1.5F * (shaftRadius - baseRadius);
            PointF neck = new PointF(B.X - headLength * unitX, B.Y - headLength * unitY);
            // Fill the entire outline at once so the head joins the shaft without a seam.
            return new PointF[] {
                Offset(A, -unitY, unitX, shaftRadius),
                Offset(neck, -unitY, unitX, shaftRadius),
                Offset(neck, -unitY, unitX, headRadius),
                B,
                Offset(neck, unitY, -unitX, headRadius),
                Offset(neck, unitY, -unitX, shaftRadius),
                Offset(A, unitY, -unitX, shaftRadius)
            };
        }

        private static PointF NearestOnArrow(PointF point, PointF[] outline)
        {
            bool inside = false;
            PointF previous = outline[outline.Length - 1], closest = previous;
            float best = float.MaxValue;
            foreach (PointF next in outline)
            {
                PointF edge = NearestOnSegment(point, previous, next);
                float distance = DistanceSquared(point, edge);
                if (distance < best) { best = distance; closest = edge; }
                if ((previous.Y > point.Y) != (next.Y > point.Y) &&
                    point.X < previous.X + (point.Y - previous.Y) * (next.X - previous.X) / (next.Y - previous.Y))
                    inside = !inside;
                previous = next;
            }
            return inside ? point : closest;
        }

        private static PointF Offset(PointF point, float dx, float dy, float distance)
        {
            return new PointF(point.X + dx * distance, point.Y + dy * distance);
        }

        private static PointF NearestOnSegment(PointF point, PointF start, PointF end)
        {
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float squaredLength = dx * dx + dy * dy;
            float t = squaredLength == 0F ? 0F :
                Math.Max(0F, Math.Min(1F, ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squaredLength));
            return new PointF(start.X + t * dx, start.Y + t * dy);
        }

        private static bool NearSegment(PointF point, PointF start, PointF end, float radius)
        {
            return DistanceSquared(point, NearestOnSegment(point, start, end)) <= radius * radius;
        }

        private static float DistanceSquared(PointF a, PointF b)
        {
            float x = a.X - b.X, y = a.Y - b.Y;
            return x * x + y * y;
        }
    }
}
