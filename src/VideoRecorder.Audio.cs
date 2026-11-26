using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenStudio
{
    public sealed partial class VideoRecorder
    {
        private sealed class AudioTrack
        {
            private const int Rate = MicrophoneCapture.SampleRate, FrameBytes = MicrophoneCapture.BytesPerFrame;
            private static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
            private static readonly Guid Pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
            private static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
            private static readonly Guid Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
            private static readonly Guid Frequency = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
            private static readonly Guid ByteRate = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
            private static readonly Guid Alignment = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
            private static readonly Guid Bits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
            private static readonly byte[] Silence = new byte[4096 * FrameBytes];
            private readonly Mf.SinkWriter writer;
            private readonly int stream;
            private long origin, framesWritten, lastPacketAt;

            internal AudioTrack(Mf.SinkWriter writer)
            {
                this.writer = writer;
                Mf.MediaType input = null, output = null;
                try
                {
                    Mf.MFCreateMediaType(out output);
                    SetFormat(output, Aac, 12000); // mono AAC-LC, 96 kbit/s
                    writer.AddStream(output, out stream);
                    Mf.MFCreateMediaType(out input);
                    SetFormat(input, Pcm, Rate * FrameBytes);
                    input.SetUINT32(Alignment, FrameBytes);
                    writer.SetInputMediaType(stream, input, IntPtr.Zero);
                }
                finally { Mf.Release(ref input); Mf.Release(ref output); }
            }

            private static void SetFormat(Mf.MediaType type, Guid subtype, int bytesPerSecond)
            {
                type.SetGUID(Mf.MajorType, Audio);
                type.SetGUID(Mf.Subtype, subtype);
                type.SetUINT32(Channels, 1);
                type.SetUINT32(Frequency, Rate);
                type.SetUINT32(Bits, 16);
                type.SetUINT32(ByteRate, bytesPerSecond);
            }

            internal void StartTimeline()
            {
                long count = Stopwatch.GetTimestamp();
                origin = count / Stopwatch.Frequency * TimeSpan.TicksPerSecond +
                         count % Stopwatch.Frequency * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            }

            internal void Drain(MicrophoneCapture mic, long elapsed, bool ending)
            {
                long target = elapsed * Rate / TimeSpan.TicksPerSecond;
                byte[] pcm;
                int count;
                long timestamp;
                while ((ending || framesWritten < target) && mic.TryRead(out pcm, out count, out timestamp))
                {
                    long first = framesWritten;
                    if (timestamp >= 0)
                    {
                        long ticks = timestamp - origin;
                        if (ticks >= -TimeSpan.TicksPerSecond && ticks <= elapsed + TimeSpan.TicksPerSecond)
                        {
                            long measured = Math.Min(target, ticks * Rate / TimeSpan.TicksPerSecond);
                            // Keep contiguous audio sample-accurate; reconcile real gaps and hardware clock drift.
                            if (framesWritten == 0 || Math.Abs(measured - first) > Rate / 50) first = measured;
                        }
                    }
                    lastPacketAt = elapsed;
                    PadUntil(first);
                    int skip = (int)Math.Min(count, framesWritten - first);
                    int take = count - skip;
                    if (ending) take = (int)Math.Min(take, Math.Max(0, target - framesWritten));
                    if (pcm == null) PadUntil(framesWritten + take);
                    else if (take > 0) Write(pcm, skip, take);
                }
                if (elapsed - lastPacketAt > 2 * TimeSpan.TicksPerSecond ||
                    (ending && lastPacketAt == 0 && elapsed >= TimeSpan.TicksPerSecond / 2))
                    throw new MicrophoneException("麦克风已停止提供录音数据。请检查 Windows 的输入设备。");
            }

            internal void Finish(long elapsed) { PadUntil(elapsed * Rate / TimeSpan.TicksPerSecond); }

            private void PadUntil(long frame)
            {
                while (framesWritten < frame)
                    Write(Silence, 0, (int)Math.Min(Silence.Length / FrameBytes, frame - framesWritten));
            }

            private void Write(byte[] pcm, int first, int frames)
            {
                Mf.Buffer buffer = null;
                Mf.Sample sample = null;
                int bytes = checked(frames * FrameBytes);
                try
                {
                    Mf.MFCreateMemoryBuffer(bytes, out buffer);
                    IntPtr data;
                    buffer.Lock(out data, IntPtr.Zero, IntPtr.Zero);
                    try { Marshal.Copy(pcm, first * FrameBytes, data, bytes); }
                    finally { buffer.Unlock(); }
                    buffer.SetCurrentLength(bytes);
                    Mf.MFCreateSample(out sample);
                    sample.AddBuffer(buffer);
                    long start = framesWritten * TimeSpan.TicksPerSecond / Rate;
                    long end = (framesWritten + frames) * TimeSpan.TicksPerSecond / Rate;
                    sample.SetSampleTime(start);
                    sample.SetSampleDuration(end - start);
                    writer.WriteSample(stream, sample);
                    framesWritten += frames;
                }
                finally { Mf.Release(ref sample); Mf.Release(ref buffer); }
            }
        }
    }
}
