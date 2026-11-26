using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenStudio
{
    public static class ScreenCapture
    {
        private const uint SourceCopy = 0x00CC0020;
        private const uint CaptureLayeredWindows = 0x40000000;

        public static Bitmap Grab(Rectangle region, bool includeCursor)
        {
            if (region.Width < 1 || region.Height < 1)
                throw new ArgumentOutOfRangeException("region", "截图区域必须至少包含一个像素。");

            Bitmap bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
            try
            {
                using (Graphics surface = Graphics.FromImage(bitmap))
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前桌面。");

                    try
                    {
                        IntPtr target = surface.GetHdc();
                        try
                        {
                            // Some display drivers allow plain SRCCOPY but reject CAPTUREBLT.
                            if (!BitBlt(target, 0, 0, region.Width, region.Height, screen,
                                        region.X, region.Y, SourceCopy | CaptureLayeredWindows) &&
                                !BitBlt(target, 0, 0, region.Width, region.Height, screen,
                                        region.X, region.Y, SourceCopy))
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取屏幕像素失败。");

                            if (includeCursor) PaintCursor(target, region);
                        }
                        finally { surface.ReleaseHdc(target); }
                    }
                    finally { ReleaseDC(IntPtr.Zero, screen); }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static void PaintCursor(IntPtr target, Rectangle region)
        {
            CursorInfo cursor = new CursorInfo();
            cursor.Size = Marshal.SizeOf(typeof(CursorInfo));
            if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || cursor.Handle == IntPtr.Zero) return;

            IconInfo icon;
            if (!GetIconInfo(cursor.Handle, out icon)) return;
            try
            {
                int x = cursor.ScreenPosition.X - region.X - (int)icon.HotspotX;
                int y = cursor.ScreenPosition.Y - region.Y - (int)icon.HotspotY;
                // GDI clips the cursor when its hotspot lies outside the capture rectangle.
                DrawIconEx(target, x, y, cursor.Handle, 0, 0, 0, IntPtr.Zero, 3);
            }
            finally
            {
                // GetIconInfo gives the caller its own copies of both bitmaps.
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Handle;
            public Point ScreenPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
            public uint HotspotX, HotspotY;
            public IntPtr Mask, Color;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
                                          IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CursorInfo cursor);
        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr handle, out IconInfo icon);
        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width,
                                              int height, int frame, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
    }
}
