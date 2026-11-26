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
                    g.DrawRectangle(pen, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
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
                if (outline == null) return NearSegment(pos, A, A, tolerance);
                bool inside = false;
                PointF previous = outline[outline.Length - 1];
                foreach (PointF next in outline)
                {
                    if (NearSegment(pos, previous, next, tolerance)) return true;
                    if ((previous.Y > pos.Y) != (next.Y > pos.Y) &&
                        pos.X < previous.X + (pos.Y - previous.Y) * (next.X - previous.X) / (next.Y - previous.Y))
                        inside = !inside;
                    previous = next;
                }
                return inside;
            }
            RectangleF bounds = Bounds(g, font, format);
            if (Kind == ShapeKind.Text)
            {
                bounds.Inflate(tolerance, tolerance);
                return !String.IsNullOrEmpty(Caption) && bounds.Contains(pos);
            }
            float reach = tolerance + strokeWidth * .5F;
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
            float headRadius = headLength * .52F;
            float shaftRadius = Math.Min(strokeWidth * .60F, headRadius * .50F);
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

        private static PointF Offset(PointF point, float dx, float dy, float distance)
        {
            return new PointF(point.X + dx * distance, point.Y + dy * distance);
        }

        private static bool NearSegment(PointF point, PointF start, PointF end, float radius)
        {
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float squaredLength = dx * dx + dy * dy;
            float t = squaredLength == 0F ? 0F :
                Math.Max(0F, Math.Min(1F, ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squaredLength));
            float x = point.X - (start.X + t * dx), y = point.Y - (start.Y + t * dy);
            return x * x + y * y <= radius * radius;
        }
    }
}
