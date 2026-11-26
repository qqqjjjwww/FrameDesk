using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenStudio
{
    internal sealed class MicrophoneException : IOException
    {
        internal MicrophoneException(string message, Exception cause = null) : base(message, cause) { }
    }

    // WASAPI capture and its COM objects stay on the recorder's MTA thread.
    internal sealed class MicrophoneCapture : IDisposable
    {
        internal const int SampleRate = 48000, BytesPerFrame = 2;
        private IAudioClient client;
        private IAudioCaptureClient capture;
        private byte[] buffer;
        private bool running;

        internal MicrophoneCapture()
        {
            IDeviceEnumerator devices = null;
            IDevice device = null;
            try
            {
                devices = (IDeviceEnumerator)new DeviceEnumerator();
                devices.GetDefaultAudioEndpoint(1, 1, out device); // eCapture, eMultimedia
                Guid audioClientId = typeof(IAudioClient).GUID;
                device.Activate(ref audioClientId, 1, IntPtr.Zero, out client);
                WaveFormat format = new WaveFormat {
                    tag = 1, channels = 1, samplesPerSecond = SampleRate,
                    bytesPerSecond = SampleRate * BytesPerFrame, alignment = BytesPerFrame, bits = 16
                };
                // Shared mode with Windows' channel matrix and sample-rate converter.
                const uint convertPcm = 0x80000000, qualityResampling = 0x08000000;
                client.Initialize(0, convertPcm | qualityResampling, TimeSpan.TicksPerSecond, 0, ref format, IntPtr.Zero);
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                client.GetService(ref captureId, out capture);
            }
            catch (Exception ex)
            {
                Dispose();
                throw OpenError(ex);
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                if (devices != null) Marshal.ReleaseComObject(devices);
            }
        }

        private static MicrophoneException OpenError(Exception ex)
        {
            string reason;
            switch ((uint)ex.HResult)
            {
                case 0x80070005:
                    reason = "麦克风访问被拒绝。请在 Windows 设置的“麦克风”页面允许桌面应用访问麦克风。";
                    break;
                case 0x80070490:
                case 0x88890004:
                    reason = "未找到可用的麦克风。请连接麦克风，并在 Windows 声音设置中选好默认输入设备。";
                    break;
                default:
                    reason = "无法打开默认麦克风。请检查 Windows 声音设置中的输入设备，以及是否有应用独占麦克风。";
                    break;
            }
            return new MicrophoneException(reason + " 如需只录画面，请关闭取景台的“录制麦克风”再录。", ex);
        }

        internal void Start()
        {
            try { client.Start(); running = true; }
            catch (Exception ex) { throw OpenError(ex); }
        }

        internal void Stop()
        {
            if (!running) return;
            try { client.Stop(); }
            catch (COMException ex) { throw new MicrophoneException("麦克风已断开或无法继续录音。", ex); }
            catch (UnauthorizedAccessException ex) { throw OpenError(ex); }
            finally { running = false; }
        }

        // The packet timestamp and Stopwatch both use the performance counter, expressed here in 100 ns.
        internal bool TryRead(out byte[] pcm, out int frames, out long timestamp)
        {
            pcm = null; frames = 0; timestamp = -1;
            try
            {
                uint available;
                capture.GetNextPacketSize(out available);
                if (available == 0) return false;
                IntPtr data;
                uint count, flags;
                ulong devicePosition, qpc;
                capture.GetBuffer(out data, out count, out flags, out devicePosition, out qpc);
                try
                {
                    frames = checked((int)count);
                    if ((flags & 4) == 0 && qpc != 0) timestamp = (long)qpc; // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR
                    if ((flags & 2) == 0 && frames > 0) // silent packets need no backing storage
                    {
                        int bytes = checked(frames * BytesPerFrame);
                        if (buffer == null || buffer.Length < bytes) buffer = new byte[bytes];
                        Marshal.Copy(data, buffer, 0, bytes);
                        pcm = buffer;
                    }
                }
                finally { capture.ReleaseBuffer(count); }
                return frames != 0;
            }
            catch (COMException ex)
            {
                throw new MicrophoneException("麦克风已断开或无法继续录音。", ex);
            }
            catch (UnauthorizedAccessException ex) { throw OpenError(ex); }
        }

        public void Dispose()
        {
            try { Stop(); } catch (MicrophoneException) { }
            if (capture != null) { Marshal.ReleaseComObject(capture); capture = null; }
            if (client != null) { Marshal.ReleaseComObject(client); client = null; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort tag, channels;
            public uint samplesPerSecond, bytesPerSecond;
            public ushort alignment, bits, extraSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceEnumerator
        {
            void _VtblGap1_1();
            void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevice
        {
            void Activate(ref Guid iid, uint context, IntPtr args, out IAudioClient client);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            void Initialize(int mode, uint flags, long duration, long period, ref WaveFormat format, IntPtr session);
            void _VtblGap1_6();
            void Start();
            void Stop();
            void _VtblGap2_2();
            void GetService(ref Guid iid, out IAudioCaptureClient capture);
        }
        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }
    }
}
