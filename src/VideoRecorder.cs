using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace ScreenStudio
{
    /// <summary>Records an H.264 MP4 with optional microphone audio on one MTA thread.</summary>
    public sealed partial class VideoRecorder : IDisposable
    {
        private readonly Rectangle region;
        private readonly int fps;
        private readonly bool includeCursor;
        private readonly bool includeMicrophone;
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread worker;
        private Exception failure;
        private byte[] result;
        private bool disposed;

        // Raised asynchronously for a failure after Start returned successfully.
        public event Action<Exception> Failed;
        internal MicrophoneException MicrophoneError { get; private set; }

        public VideoRecorder(Rectangle region, int fps, bool includeCursor)
            : this(region, fps, includeCursor, false) { }

        public VideoRecorder(Rectangle region, int fps, bool includeCursor, bool includeMicrophone)
        {
            if (region.Width < 1 || region.Height < 1) throw new ArgumentException("录制区域为空。", "region");
            if (fps < 1 || fps > 60) throw new ArgumentOutOfRangeException("fps");
            this.region = region;
            this.fps = fps;
            this.includeCursor = includeCursor;
            this.includeMicrophone = includeMicrophone;
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("VideoRecorder");
                if (worker != null) throw new InvalidOperationException("录制已经启动。");
                worker = new Thread(Record);
                worker.IsBackground = true;
                worker.Name = "MP4 recording";
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
                ready.WaitOne();
                CheckFailure();
            }
        }

        public byte[] Stop()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("VideoRecorder");
                if (worker == null) throw new InvalidOperationException("尚未开始录制。");
                stop.Set();
                worker.Join();
                CheckFailure();
                return result;
            }
        }

        private void CheckFailure()
        {
            if (failure != null) throw new IOException("录屏失败：" + failure.Message, failure);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stop.Set();
                if (worker != null) worker.Join();
                stop.Dispose();
                ready.Dispose();
                result = null;
            }
        }

        private void Record()
        {
            bool started = false;
            bool mfStarted = false;
            IStream memory = null;
            IntPtr byteStream = IntPtr.Zero;
            Mf.SinkWriter writer = null;
            Mf.Sample pending = null;
            MicrophoneCapture microphone = null;
            try
            {
                Mf.MFStartup(0x00020070, 0);
                mfStarted = true;
                memory = Mf.SHCreateMemStream(IntPtr.Zero, 0);
                if (memory == null) throw new OutOfMemoryException("无法分配录屏内存。");
                Mf.MFCreateMFByteStreamOnStream(memory, out byteStream);

                // The Windows software H.264 encoder accepts even dimensions, up to 4096 x 2304.
                double scale = Math.Min(1.0, Math.Min(4096.0 / region.Width, 2304.0 / region.Height));
                Size picture = new Size(Math.Max(1, (int)Math.Round(region.Width * scale)),
                                        Math.Max(1, (int)Math.Round(region.Height * scale)));
                Size canvas = new Size(Math.Max(16, (picture.Width + 1) & ~1),
                                       Math.Max(16, (picture.Height + 1) & ~1));
                int stream;
                // A non-null IMFByteStream selects the in-memory destination. The suffix only selects the muxer.
                Mf.MFCreateSinkWriterFromURL("clipboard.mp4", byteStream, IntPtr.Zero, out writer);
                Configure(writer, canvas, out stream);
                AudioTrack audio = null;
                if (includeMicrophone)
                {
                    microphone = new MicrophoneCapture();
                    audio = new AudioTrack(writer);
                }
                writer.BeginWriting();
                long period = TimeSpan.TicksPerSecond / fps;
                pending = GrabSample(picture, canvas);
                if (audio != null) audio.StartTimeline();
                Stopwatch clock = Stopwatch.StartNew();
                if (microphone != null) microphone.Start();
                long last = 0;
                long due = period;
                started = true;
                ready.Set();
                for (;;)
                {
                    long remaining = due - clock.Elapsed.Ticks;
                    int wait = remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, (remaining + 9999) / 10000);
                    bool ending = stop.WaitOne(wait);
                    long now = Math.Max(last + 1, clock.Elapsed.Ticks);
                    if (ending && last == 0) now = Math.Max(period, now);
                    if (audio != null)
                    {
                        try
                        {
                            if (ending) microphone.Stop();
                            audio.Drain(microphone, now, ending);
                        }
                        catch (MicrophoneException ex)
                        {
                            // Finish a playable recording with everything captured before disconnection.
                            MicrophoneError = ex;
                            ending = true;
                        }
                        if (ending) audio.Finish(now);
                    }
                    pending.SetSampleTime(last);
                    pending.SetSampleDuration(now - last);
                    writer.WriteSample(stream, pending);
                    Mf.Release(ref pending);
                    if (ending) break;
                    last = now;
                    pending = GrabSample(picture, canvas);
                    // Skip missed slots without accelerating the playback timeline.
                    due = (now / period + 1) * period;
                }
                writer.Finish();
                Mf.Release(ref writer);
                result = ReadRecording(memory);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (microphone != null) microphone.Dispose();
                Mf.Release(ref pending);
                Mf.Release(ref writer);
                if (byteStream != IntPtr.Zero) Marshal.Release(byteStream);
                Mf.Release(ref memory);
                if (mfStarted) Mf.MFShutdown();
                if (!started) ready.Set();
                Exception problem = failure ?? MicrophoneError;
                if (started && problem != null)
                {
                    Action<Exception> handler = Failed;
                    if (handler != null) ThreadPool.QueueUserWorkItem(delegate { handler(problem); });
                }
            }
        }

        private static byte[] ReadRecording(IStream memory)
        {
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            memory.Stat(out stat, 1); // STATFLAG_NONAME
            if (stat.cbSize <= 0) throw new IOException("录屏数据为空。");
            if (stat.cbSize > 0x7FFFFFC7L) throw new IOException("录屏大小超过单个剪贴板对象的容量。");
            byte[] movie = new byte[(int)stat.cbSize];
            memory.Seek(0, 0, IntPtr.Zero);
            IntPtr read = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                memory.Read(movie, movie.Length, read);
                if (Marshal.ReadInt32(read) != movie.Length) throw new EndOfStreamException("录屏数据不完整。");
            }
            finally { Marshal.FreeHGlobal(read); }
            return movie;
        }

        private void Configure(Mf.SinkWriter writer, Size size, out int stream)
        {
            Mf.MediaType input = null;
            Mf.MediaType output = null;
            try
            {
                output = CreateType(Mf.H264, size);
                int bitrate = (int)Math.Max(480000, Math.Min(40000000L, (long)size.Width * size.Height * fps / 5));
                output.SetUINT32(Mf.AverageBitrate, bitrate);
                writer.AddStream(output, out stream);
                input = CreateType(Mf.Rgb32, size);
                input.SetUINT32(Mf.DefaultStride, -size.Width * 4);
                writer.SetInputMediaType(stream, input, IntPtr.Zero);
            }
            finally
            {
                Mf.Release(ref input);
                Mf.Release(ref output);
            }
        }

        private Mf.MediaType CreateType(Guid subtype, Size size)
        {
            Mf.MediaType type;
            Mf.MFCreateMediaType(out type);
            try
            {
                type.SetGUID(Mf.MajorType, Mf.Video);
                type.SetGUID(Mf.Subtype, subtype);
                type.SetUINT32(Mf.InterlaceMode, 2); // MFVideoInterlace_Progressive
                type.SetUINT64(Mf.FrameSize, ((long)size.Width << 32) | (uint)size.Height);
                type.SetUINT64(Mf.FrameRate, ((long)fps << 32) | 1L);
                type.SetUINT64(Mf.PixelAspectRatio, (1L << 32) | 1L);
                return type;
            }
            catch
            {
                Mf.Release(ref type);
                throw;
            }
        }

        private Mf.Sample GrabSample(Size picture, Size canvas)
        {
            using (Bitmap screen = ScreenCapture.Grab(region, includeCursor))
            {
                if (screen.Size == canvas && picture == canvas) return CreateSample(screen);
                using (Bitmap fitted = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(fitted))
                    {
                        graphics.Clear(Color.Black);
                        if (screen.Size == picture)
                            graphics.DrawImageUnscaled(screen, 0, 0);
                        else
                        {
                            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            graphics.DrawImage(screen, new Rectangle(Point.Empty, picture));
                        }
                    }
                    return CreateSample(fitted);
                }
            }
        }

        private static Mf.Sample CreateSample(Bitmap frame)
        {
            Mf.Buffer buffer = null;
            Mf.Sample sample = null;
            int widthBytes = checked(frame.Width * 4);
            int length = checked(widthBytes * frame.Height);
            try
            {
                Mf.MFCreateMemoryBuffer(length, out buffer);
                IntPtr target;
                buffer.Lock(out target, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    Rectangle area = new Rectangle(Point.Empty, frame.Size);
                    BitmapData bits = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        // RGB32 in Media Foundation convention is bottom-up. Scan0 is the display's top row.
                        IntPtr top = IntPtr.Add(target, length - widthBytes);
                        Mf.MFCopyImage(top, -widthBytes, bits.Scan0, bits.Stride, widthBytes, frame.Height);
                    }
                    finally { frame.UnlockBits(bits); }
                }
                finally { buffer.Unlock(); }
                buffer.SetCurrentLength(length);
                Mf.MFCreateSample(out sample);
                sample.AddBuffer(buffer);
                return sample;
            }
            catch
            {
                Mf.Release(ref sample);
                throw;
            }
            finally { Mf.Release(ref buffer); }
        }

        // The gaps below preserve the native COM method order from mfobjects.h / mfreadwrite.h.
        // They skip methods the recorder never calls, including IMFSample's 30 inherited attribute methods.
        private static class Mf
        {
            internal static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            internal static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            internal static readonly Guid Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
            internal static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
            internal static readonly Guid Rgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
            internal static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
            internal static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
            internal static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
            internal static readonly Guid PixelAspectRatio = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
            internal static readonly Guid AverageBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
            internal static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFStartup(int version, int flags);
            [DllImport("mfplat.dll")]
            internal static extern int MFShutdown();
            [DllImport("shlwapi.dll", ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.Interface)]
            internal static extern IStream SHCreateMemStream(IntPtr initial, uint length);
            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFCreateMFByteStreamOnStream(IStream input, out IntPtr output);
            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFCreateMediaType(out MediaType type);
            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFCreateSample(out Sample sample);
            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFCreateMemoryBuffer(int length, out Buffer buffer);
            [DllImport("mfplat.dll", PreserveSig = false)]
            internal static extern void MFCopyImage(IntPtr dest, int destStride, IntPtr source, int sourceStride, int bytesWide, int lines);
            [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
            internal static extern void MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out SinkWriter writer);

            internal static void Release<T>(ref T value) where T : class
            {
                if (value == null) return;
                Marshal.ReleaseComObject(value);
                value = null;
            }

            [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface MediaType
            {
                void _VtblGap1_18();
                void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
                void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
                void _VtblGap2_1();
                void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
            }

            [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface Buffer
            {
                void Lock(out IntPtr data, IntPtr maxLength, IntPtr currentLength);
                void Unlock();
                void _VtblGap1_1();
                void SetCurrentLength(int length);
            }

            [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface Sample
            {
                void _VtblGap1_33();
                void SetSampleTime(long time);
                void _VtblGap2_1();
                void SetSampleDuration(long duration);
                void _VtblGap3_3();
                void AddBuffer(Buffer buffer);
            }

            [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface SinkWriter
            {
                void AddStream(MediaType output, out int stream);
                void SetInputMediaType(int stream, MediaType input, IntPtr attributes);
                void BeginWriting();
                void WriteSample(int stream, Sample sample);
                void _VtblGap1_4();
                void Finish();
            }
        }
    }
}
