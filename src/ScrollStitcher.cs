using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenStudio
{
    internal sealed class ScrollStitcher : IDisposable
    {
        private const int MaxHeight = 30000;
        private const long MaxPixels = 40000000;
        private static readonly PixelFormat Format = PixelFormat.Format32bppRgb;
        private const int RowsPerBlock = 32;
        private readonly Dictionary<int, byte[]> blocks = new Dictionary<int, byte[]>();
        private readonly Size frameSize;
        private Sample previous;
        // Document coordinates relative to the first viewport. Both can become negative.
        private int viewportTop, capturedTop;

        internal int Height { get; private set; }
        internal string LastStatus { get; private set; }
        internal bool Unmatched { get; private set; }
        internal bool LimitReached { get; private set; }

        internal ScrollStitcher(Bitmap first)
        {
            if (first == null) throw new ArgumentNullException("first");
            if (first.Height > MaxHeight || (long)first.Width * first.Height > MaxPixels)
                throw new ArgumentException("选区过大，请缩小后再试。");
            frameSize = first.Size;
            previous = new Sample(first);
            TransferRows(first, 0, 0, first.Height, true);
            Height = first.Height;
            LastStatus = "已截取首屏，可向上或向下滚动";
        }

        // Returns true only when the captured range grows. A matched revisit still updates the viewport.
        internal bool TryAppend(Bitmap next)
        {
            if (previous == null) throw new ObjectDisposedException("ScrollStitcher");
            if (next == null) throw new ArgumentNullException("next");
            if (next.Size != frameSize) throw new ArgumentException("截图选区的尺寸发生了变化。");
            if (LimitReached) return false;
            Unmatched = false;
            Sample current = new Sample(next);
            int first, end, changed;
            List<int> probes = current.ChangedFeatures(previous, out first, out end, out changed);
            if (changed <= 3)
            {
                LastStatus = "可向上或向下滚动";
                return false;
            }
            int shift = current.FindShift(previous, probes, first, end);
            if (shift == 0)
            {
                Unmatched = true;
                LastStatus = "未接上，请滚回一些，再慢慢滚动";
                return false;
            }
            int nextTop = viewportTop + shift;
            int newTop = Math.Min(capturedTop, nextTop);
            int newBottom = Math.Max(capturedTop + Height, nextTop + frameSize.Height);
            int newHeight = newBottom - newTop;
            if (newHeight > MaxHeight || (long)newHeight * next.Width > MaxPixels)
            {
                LimitReached = true;
                LastStatus = "长图已达上限，请完成截图";
                return false;
            }

            bool grew = newHeight > Height;
            if (grew)
            {
                // Join inside the moving overlap. Replace the former outer edge so fixed
                // headers and footers occur only once, even when extending after a reversal.
                int firstShared = first + Math.Max(0, -shift);
                int endShared = end - Math.Max(0, shift);
                int seam = (firstShared + endShared) / 2;
                if (nextTop < capturedTop)
                    TransferRows(next, 0, nextTop, seam, true);
                else
                    TransferRows(next, seam, nextTop + seam, next.Height - seam, true);
                capturedTop = newTop;
                Height = newHeight;
            }
            viewportTop = nextTop;
            previous = current;
            LastStatus = !grew ? "这部分已截取，可继续滚动" :
                (shift < 0 ? "已拼接上方内容" : "已拼接下方内容");
            return grew;
        }

        internal Bitmap Render()
        {
            if (previous == null) throw new ObjectDisposedException("ScrollStitcher");
            Bitmap result = new Bitmap(frameSize.Width, Height, Format);
            try
            {
                TransferRows(result, 0, capturedTop, Height, false);
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        // Fixed-size blocks allow either end to grow without moving previously captured rows.
        // Revisiting an edge overwrites the same storage, including for one-pixel scrolls.
        private void TransferRows(Bitmap bitmap, int imageY, int documentY, int rows, bool store)
        {
            int pitch = bitmap.Width * 4;
            BitmapData data = bitmap.LockBits(new Rectangle(0, imageY, bitmap.Width, rows),
                store ? ImageLockMode.ReadOnly : ImageLockMode.WriteOnly, Format);
            try
            {
                int copied = 0;
                while (copied < rows)
                {
                    int y = documentY + copied;
                    int rowInBlock = y & (RowsPerBlock - 1);
                    int blockStart = y - rowInBlock;
                    byte[] bytes;
                    if (!blocks.TryGetValue(blockStart, out bytes))
                    {
                        if (!store) throw new InvalidOperationException("长图缺少已截取的行。");
                        bytes = new byte[checked(pitch * RowsPerBlock)];
                        blocks.Add(blockStart, bytes);
                    }
                    int count = Math.Min(rows - copied, RowsPerBlock - rowInBlock);
                    if (data.Stride != pitch) count = 1;
                    IntPtr pixels = IntPtr.Add(data.Scan0, copied * data.Stride);
                    if (store) Marshal.Copy(pixels, bytes, rowInBlock * pitch, count * pitch);
                    else Marshal.Copy(bytes, rowInBlock * pitch, pixels, count * pitch);
                    copied += count;
                }
            }
            finally { bitmap.UnlockBits(data); }
        }

        public void Dispose()
        {
            previous = null;
            blocks.Clear();
        }

        // A narrow RGB sample for every native image row keeps one-pixel precision in
        // the scroll direction. Ignore outer gutters, including the moving scrollbar thumb.
        private sealed class Sample
        {
            private readonly int cols, rows;
            private readonly int[] pixels;

            internal Sample(Bitmap source)
            {
                int w = source.Width;
                rows = source.Height;
                int margin = Math.Min(w / 12, Math.Max(5, w / 32));
                int available = w - 2 * margin;
                cols = Math.Min(256, available);
                int[] xs = new int[cols];
                for (int x = 0; x < cols; x++) xs[x] = margin + (int)((x + .5) * available / cols);
                pixels = new int[cols * rows];
                int[] line = new int[w];
                BitmapData data = source.LockBits(new Rectangle(0, 0, w, rows), ImageLockMode.ReadOnly, Format);
                try
                {
                    for (int y = 0; y < rows; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, data.Stride * y), line, 0, w);
                        for (int x = 0; x < cols; x++) pixels[y * cols + x] = line[xs[x]] & 0xffffff;
                    }
                }
                finally { source.UnlockBits(data); }
            }

            private static int Distance(int a, int b)
            {
                if (a == b) return 0;
                int r = Math.Abs((a >> 16) - (b >> 16));
                int g = Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255));
                int blue = Math.Abs((a & 255) - (b & 255));
                return Math.Max(r, Math.Max(g, blue));
            }

            internal List<int> ChangedFeatures(Sample old, out int first, out int end, out int changed)
            {
                first = rows; end = 0; changed = 0;
                var probes = new List<int>();
                if (cols < 3 || rows < 5) return probes;
                int band = Math.Max(2, (rows + 79) / 80);
                int[] best = new int[3], position = new int[3];
                for (int top = 1; top < rows - 1; top += band)
                {
                    Array.Clear(best, 0, best.Length);
                    int bottom = Math.Min(rows - 1, top + band);
                    for (int y = top; y < bottom; y++)
                    {
                        int rowChanges = 0;
                        for (int x = 1; x < cols - 1; x++)
                        {
                            int p = y * cols + x, value = pixels[p];
                            int diff = Distance(value, old.pixels[p]);
                            if (diff <= 9) continue;
                            rowChanges++;
                            // Choose features that both moved and contain texture (font strokes,
                            // edges, or photo detail). A blank background is weak evidence.
                            int contrast = Math.Max(Distance(value, pixels[p - cols]), Distance(value, pixels[p - 1]));
                            int strength = Math.Min(contrast, 110) * 2 + Math.Min(diff, 90);
                            int section = x * 3 / cols;
                            if (strength > best[section]) { best[section] = strength; position[section] = p; }
                        }
                        if (rowChanges >= Math.Min(2, Math.Max(1, cols / 40)))
                        {
                            first = Math.Min(first, y);
                            end = y + 1;
                        }
                        changed += rowChanges;
                    }
                    for (int i = 0; i < best.Length; i++)
                        if (best[i] > 20) probes.Add(position[i]);
                }
                return probes;
            }

            private int PatchError(Sample old, int p, int q)
            {
                // Cap single-pixel noise so an animated caret need not spoil its whole band.
                return Math.Min(64, Distance(pixels[p], old.pixels[q])) * 2 +
                       Math.Min(64, Distance(pixels[p - 1], old.pixels[q - 1])) +
                       Math.Min(64, Distance(pixels[p + 1], old.pixels[q + 1])) +
                       Math.Min(64, Distance(pixels[p - cols], old.pixels[q - cols])) +
                       Math.Min(64, Distance(pixels[p + cols], old.pixels[q + cols]));
            }

            internal int FindShift(Sample old, List<int> probes, int first, int end)
            {
                int span = end - first;
                int minimumOverlap = Math.Max(12, Math.Min(160, span / 5));
                if (span <= minimumOverlap || span < rows / 8 || probes.Count < 6) return 0;
                int maxShift = span - minimumOverlap;
                // Index signed scroll offsets around the center slot. Both directions must
                // compete for confidence, especially in repeating chat rows and tables.
                double[] scores = new double[2 * maxShift + 1];
                int best = 0;
                for (int shift = -maxShift; shift <= maxShift; shift++)
                {
                    if (shift == 0) continue;
                    int start = first + Math.Max(0, -shift), stop = end - Math.Max(0, shift);
                    int off = shift * cols, matches = 0, count = 0, goodFrom = rows, goodTo = 0;
                    int error = 0;
                    foreach (int p in probes)
                    {
                        int y = p / cols;
                        if (y < start || y >= stop) continue;
                        count++;
                        int mismatch = PatchError(old, p, p + off);
                        error += Math.Min(180, mismatch);
                        if (mismatch <= 78)
                        {
                            matches++;
                            goodFrom = Math.Min(goodFrom, y);
                            goodTo = Math.Max(goodTo, y);
                        }
                    }
                    if (matches < 5 || matches * 10 < count * 7 || goodTo - goodFrom < minimumOverlap / 2) continue;
                    scores[maxShift + shift] = 1.0 - (double)error / (180 * count);
                    if (scores[maxShift + shift] > scores[maxShift + best]) best = shift;
                }
                if (best == 0 || scores[maxShift + best] < .76) return 0;
                // Rows with a repeated title or icon can have matching anchor patches but
                // different timestamps or list numbers. Compare the rest of each shortlisted
                // alignment before deciding whether those rows are genuinely ambiguous.
                double cutoff = Math.Max(.70, scores[maxShift + best] - .15);
                double strongest = 0, second = 0;
                int candidates = 0, bestShift = 0;
                for (int shift = -maxShift; shift <= maxShift; shift++)
                {
                    int index = maxShift + shift;
                    if (scores[index] < cutoff) { scores[index] = 0; continue; }
                    if (++candidates > 96) return 0; // Little distinguishing detail throughout this frame.
                    double confidence = Verify(old, first, end, shift);
                    scores[index] = confidence;
                    if (confidence > strongest)
                    { second = strongest; strongest = confidence; bestShift = shift; }
                    else second = Math.Max(second, confidence);
                    if (second >= 1) return 0;
                }
                if (strongest < .79 || strongest - second < .0001) return 0;
                for (int shift = -maxShift; shift <= maxShift; shift++)
                    if (shift != bestShift && scores[maxShift + shift] >= Math.Max(.76, strongest - .12) &&
                        !BeatsAlternative(old, first, end, bestShift, shift, strongest, scores[maxShift + shift])) return 0;
                return bestShift;
            }

            // A repeating nine-column grid can agree almost perfectly at many offsets
            // while its small unique identifiers disagree. Look specifically at pixels
            // that distinguish the competing alignments, rather than letting the repeated
            // rules and backgrounds drown out a wrong identifier.
            private bool BeatsAlternative(Sample old, int first, int end, int winner, int alternative,
                                          double winnerScore, double alternativeScore)
            {
                int eligible = 0, matching = 0;
                int start = first - Math.Min(0, Math.Min(winner, alternative));
                int stop = end - Math.Max(0, Math.Max(winner, alternative));
                int step = Math.Max(1, (stop - start) / 650);
                for (int y = start; y < stop; y += step)
                    for (int x = 1; x < cols - 1; x++)
                    {
                        int p = y * cols + x;
                        if (Distance(pixels[p], old.pixels[p]) <= 9) continue;
                        int expected = old.pixels[p + winner * cols];
                        int different = Distance(expected, old.pixels[p + alternative * cols]);
                        if (different < 20) continue;
                        eligible++;
                        if (Distance(pixels[p], expected) <= Math.Max(11, different / 4)) matching++;
                    }
                if (eligible >= 6) return matching >= eligible * .84;
                // Large offsets in opposite directions can align disjoint ends of a list.
                // With no contradictory common pixels, require a near-exact overlap and a
                // clear advantage over the rival on the rows that each can actually explain.
                return (winner < 0) != (alternative < 0) && matching == eligible &&
                       winnerScore >= .995 && winnerScore - alternativeScore >= .01;
            }

            private double Verify(Sample old, int first, int end, int shift)
            {
                int compared = 0, error = 0, dy = shift * cols;
                int start = first + Math.Max(0, -shift), stop = end - Math.Max(0, shift);
                int step = Math.Max(1, (stop - start) / 300);
                for (int y = start; y < stop; y += step)
                    for (int x = 1; x < cols - 1; x++)
                    {
                        int p = y * cols + x;
                        if (Distance(pixels[p], old.pixels[p]) <= 9) continue;
                        compared++;
                        error += Math.Min(40, Distance(pixels[p], old.pixels[p + dy]));
                    }
                return compared >= 8 ? 1.0 - (double)error / (40 * compared) : 0;
            }
        }
    }
}
