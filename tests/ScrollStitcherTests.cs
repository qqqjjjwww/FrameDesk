// Compile this file together with src/ScrollStitcher.cs and a reference to System.Drawing.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenStudio;

internal static class ScrollStitcherTests
{
    private const int Width = 143, ViewHeight = 193;

    // A repeatable page with distinct rows, plus optional fixed header and footer.
    private static Bitmap View(int offset, int height, int header, int footer, int repeatEvery = 0)
    {
        Bitmap bitmap = new Bitmap(Width, height, PixelFormat.Format32bppRgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            int[] line = new int[Width];
            for (int y = 0; y < height; y++)
            {
                int pageRow = offset + y - header;
                if (repeatEvery > 0) pageRow %= repeatEvery;
                var random = new Random(unchecked(pageRow * 17471 + 8317));
                for (int x = 0; x < Width; x++)
                {
                    if (y < header) line[x] = 0x001500 + y * 16;
                    else if (y >= height - footer) line[x] = 0x600044 + (height - y) * 16;
                    else line[x] = random.Next(0x1000000);
                }
                Marshal.Copy(line, 0, IntPtr.Add(data.Scan0, y * data.Stride), Width);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Sequence(string name, int header, int footer, params int[] offsets)
    {
        int min = offsets[0], max = min;
        using (Bitmap initial = View(min, ViewHeight, header, footer))
        using (var stitcher = new ScrollStitcher(initial))
        {
            foreach (int offset in offsets)
            {
                bool grows = offset < min || offset > max;
                using (Bitmap frame = View(offset, ViewHeight, header, footer))
                    Check(stitcher.TryAppend(frame) == grows && !stitcher.Unmatched,
                          name + " at " + offset + ": " + stitcher.LastStatus);
                min = Math.Min(min, offset);
                max = Math.Max(max, offset);
                Check(stitcher.Height == ViewHeight + max - min, name + ": duplicate or missing rows");
            }
            using (Bitmap result = stitcher.Render())
            using (Bitmap expected = View(min, ViewHeight + max - min, header, footer))
            {
                Check(result.Size == expected.Size, name + ": wrong size");
                for (int y = 0; y < expected.Height; y++)
                    for (int x = 0; x < Width; x++)
                        if (expected.GetPixel(x, y).ToArgb() != result.GetPixel(x, y).ToArgb())
                            throw new Exception(name + ": pixel mismatch at " + x + ", " + y);
            }
        }
        Console.WriteLine("PASS " + name);
    }

    private static void Run()
    {
        Sequence("up from middle", 0, 0, 510, 477, 401, 400, 299, 237, 237);
        Sequence("up with fixed header and footer", 29, 18, 700, 620, 619, 524, 491);
        Sequence("down with fixed header and footer", 29, 18, 450, 490, 535, 610, 611, 706);
        Sequence("extend both ends, revisit captured rows", 29, 18,
                 500, 557, 610, 570, 608, 640, 565, 490, 432, 470, 426, 522, 600, 660, 610);
        Sequence("reverse after starting up", 0, 0, 500, 431, 384, 430, 493, 536, 538, 453, 371);
        using (Bitmap initial = View(500, ViewHeight, 0, 0))
        using (var stitcher = new ScrollStitcher(initial))
        using (Bitmap gap = View(120, ViewHeight, 0, 0))
        using (Bitmap recover = View(444, ViewHeight, 0, 0))
        {
            Check(!stitcher.TryAppend(gap) && stitcher.Unmatched && stitcher.Height == ViewHeight, "gap was accepted");
            Check(stitcher.TryAppend(recover) && stitcher.Height == ViewHeight + 56, "upwards recovery failed");
        }
        Console.WriteLine("PASS recover after missing overlap");
        using (Bitmap initial = View(500, ViewHeight, 16, 12, 19))
        using (var stitcher = new ScrollStitcher(initial))
        using (Bitmap repeated = View(475, ViewHeight, 16, 12, 19))
        {
            Check(!stitcher.TryAppend(repeated) && stitcher.Unmatched && stitcher.Height == ViewHeight,
                  "indistinguishable upward and downward alignments must not grow the capture");
        }
        Console.WriteLine("PASS ambiguous repeating rows");
    }

    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
