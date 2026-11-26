using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Forms;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ScreenStudio
{
    internal static class VideoClipboard
    {
        private const int NotImplemented = unchecked((int)0x80004001);
        private const int InvalidArgument = unchecked((int)0x80070057);
        private const int FormatError = unchecked((int)0x80040064);
        private const int IndexError = unchecked((int)0x80040068);
        private const int MediumError = unchecked((int)0x80040069);
        private const int AspectError = unchecked((int)0x8004006B);
        private const int TargetError = unchecked((int)0x80040065);
        private const int NoAdvice = unchecked((int)0x80040003);
        private const int ClipboardBusy = unchecked((int)0x800401D0);
        private static WeakReference owner;

        public static void Copy(byte[] bytes, string fileName)
        {
            Copy(bytes, fileName, null);
        }

        public static void Copy(byte[] bytes, string fileName, string attachmentPath)
        {
            if (Application.OleRequired() != ApartmentState.STA)
                throw new InvalidOperationException("请在窗口线程中复制录屏。");
            ComDataObject data = CreateDataObject(bytes, fileName, attachmentPath);
            int hr = OleSetClipboard(data);
            for (int attempt = 0; hr == ClipboardBusy && attempt < 5; attempt++)
            {
                Thread.Sleep(30);
                hr = OleSetClipboard(data);
            }
            if (hr < 0) throw new COMException("无法将录屏复制到剪贴板，请稍后重试。", hr);
            owner = new WeakReference(data);
        }

        public static void FlushOwned()
        {
            ComDataObject data = owner == null ? null : owner.Target as ComDataObject;
            if (data == null || OleIsCurrentClipboard(data) != 0) return;
            int hr = OleFlushClipboard();
            for (int attempt = 0; hr == ClipboardBusy && attempt < 5; attempt++)
            {
                Thread.Sleep(30);
                if (OleIsCurrentClipboard(data) != 0) return;
                hr = OleFlushClipboard();
            }
            if (hr < 0) throw new COMException("Windows 未能在退出前保留剪贴板中的录屏，请先粘贴视频。", hr);
            owner = null;
        }

        internal static ComDataObject CreateDataObject(byte[] bytes, string fileName)
        {
            return CreateDataObject(bytes, fileName, null);
        }

        internal static ComDataObject CreateDataObject(byte[] bytes, string fileName, string attachmentPath)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("没有可复制的录屏数据。", "bytes");
            if (String.IsNullOrWhiteSpace(fileName) || fileName.Length >= 260 ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("录屏文件名无效。", "fileName");
            if (attachmentPath != null)
            {
                attachmentPath = Path.GetFullPath(attachmentPath);
                if (!File.Exists(attachmentPath))
                    throw new FileNotFoundException("录屏附件不存在，请重试复制。", attachmentPath);
            }
            return new VideoDataObject(bytes, fileName, attachmentPath);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class VideoDataObject : ComDataObject
        {
            private readonly byte[] bytes;
            private readonly byte[] descriptor;
            private readonly byte[] fileDrop;
            private const short FileDropFormat = 15; // CF_HDROP
            private readonly short dropEffect = Register("Preferred DropEffect");
            private readonly short fileDescriptor = Register("FileGroupDescriptorW");
            private readonly short fileContents = Register("FileContents");
            private readonly short mime = Register("video/mp4");
            private readonly short localOnly = Register("ExcludeClipboardContentFromMonitorProcessing");
            private static readonly byte[] LocalOnlyMarker = new byte[] { 1 };
            private static readonly byte[] CopyEffect = BitConverter.GetBytes(1); // DROPEFFECT_COPY

            internal VideoDataObject(byte[] bytes, string fileName, string attachmentPath)
            {
                this.bytes = bytes;
                if (attachmentPath != null)
                {
                    // The DROPFILES header is 20 bytes on both x86 and x64. Follow
                    // it with a double-null-terminated list of UTF-16 file paths.
                    const int headerSize = 20;
                    byte[] paths = System.Text.Encoding.Unicode.GetBytes(attachmentPath + "\0\0");
                    fileDrop = new byte[headerSize + paths.Length];
                    using (BinaryWriter writer = new BinaryWriter(new MemoryStream(fileDrop)))
                    {
                        writer.Write(headerSize);
                        writer.BaseStream.Position = 16;
                        writer.Write(1); // fWide
                        writer.Write(paths);
                    }
                }
                // FILEGROUPDESCRIPTORW holds one packed FILEDESCRIPTORW. The size and
                // name below follow the fixed offsets defined in shlobj_core.h.
                descriptor = new byte[596];
                using (BinaryWriter writer = new BinaryWriter(new MemoryStream(descriptor)))
                {
                    writer.Write(1);
                    writer.Write(0x80000044u); // FD_UNICODE | FD_ATTRIBUTES | FD_FILESIZE
                    writer.BaseStream.Position = 40;
                    writer.Write(0x80); // FILE_ATTRIBUTE_NORMAL
                    writer.BaseStream.Position = 72;
                    writer.Write(bytes.Length); // nFileSizeLow (high part is zero)
                    writer.Write(System.Text.Encoding.Unicode.GetBytes(fileName));
                }
            }

            public int QueryGetData(ref FORMATETC format)
            {
                if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return AspectError;
                if (format.ptd != IntPtr.Zero) return TargetError;
                if (format.cfFormat == fileContents)
                {
                    if (format.lindex != 0 && format.lindex != -1) return IndexError;
                    return (format.tymed & (TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL)) != 0 ? 0 : MediumError;
                }
                bool attachmentFormat = fileDrop != null && (format.cfFormat == FileDropFormat || format.cfFormat == dropEffect);
                if (!attachmentFormat && format.cfFormat != fileDescriptor && format.cfFormat != mime && format.cfFormat != localOnly)
                    return FormatError;
                if (format.lindex != -1) return IndexError;
                return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : MediumError;
            }

            public void GetData(ref FORMATETC format, out STGMEDIUM medium)
            {
                medium = new STGMEDIUM();
                int hr = QueryGetData(ref format);
                if (hr < 0) throw new COMException("请求的剪贴板格式不可用。", hr);
                if (format.cfFormat == fileContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
                {
                    IntPtr stream = SHCreateMemStream(bytes, (uint)bytes.Length);
                    if (stream == IntPtr.Zero) throw new OutOfMemoryException("内存不足，无法读取录屏。");
                    medium.tymed = TYMED.TYMED_ISTREAM;
                    medium.unionmember = stream;
                }
                else
                {
                    medium.tymed = TYMED.TYMED_HGLOBAL;
                    byte[] payload = bytes;
                    if (format.cfFormat == FileDropFormat) payload = fileDrop;
                    else if (format.cfFormat == dropEffect) payload = CopyEffect;
                    else if (format.cfFormat == fileDescriptor) payload = descriptor;
                    else if (format.cfFormat == localOnly) payload = LocalOnlyMarker;
                    medium.unionmember = AllocateGlobal(payload);
                }
            }

            public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
            {
                if (direction != DATADIR.DATADIR_GET) throw new COMException("不支持此操作。", NotImplemented);
                List<FORMATETC> formats = new List<FORMATETC>();
                if (fileDrop != null)
                {
                    formats.Add(Format(FileDropFormat, -1, TYMED.TYMED_HGLOBAL));
                    formats.Add(Format(dropEffect, -1, TYMED.TYMED_HGLOBAL));
                }
                formats.Add(Format(fileDescriptor, -1, TYMED.TYMED_HGLOBAL));
                // OLE persists unindexed formats when it converts the data object
                // into an ordinary clipboard on application exit.
                formats.Add(Format(fileContents, -1, TYMED.TYMED_HGLOBAL));
                formats.Add(Format(fileContents, 0, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL));
                formats.Add(Format(mime, -1, TYMED.TYMED_HGLOBAL));
                formats.Add(Format(localOnly, -1, TYMED.TYMED_HGLOBAL));
                return new FormatEnumerator(formats.ToArray(), 0);
            }

            public int GetCanonicalFormatEtc(ref FORMATETC format, out FORMATETC result)
            {
                result = format; result.ptd = IntPtr.Zero;
                return 0x00040130; // DATA_S_SAMEFORMATETC
            }
            public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
            { throw new COMException("不支持此传输方式。", NotImplemented); }
            public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
            { throw new COMException("剪贴板数据为只读。", NotImplemented); }
            public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection)
            { connection = 0; return NoAdvice; }
            public void DUnadvise(int connection)
            { throw new COMException("不支持此操作。", NoAdvice); }
            public int EnumDAdvise(out IEnumSTATDATA enumerator)
            { enumerator = null; return NoAdvice; }
        }

        private static FORMATETC Format(short id, int index, TYMED media)
        {
            return new FORMATETC { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = media };
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class FormatEnumerator : IEnumFORMATETC
        {
            private readonly FORMATETC[] items;
            private int position;
            internal FormatEnumerator(FORMATETC[] items, int position)
            { this.items = items; this.position = position; }
            public int Next(int count, FORMATETC[] values, int[] fetched)
            {
                if (count < 0 || values == null || values.Length < count ||
                    (count != 1 && (fetched == null || fetched.Length == 0))) return InvalidArgument;
                int n = Math.Min(count, items.Length - position);
                Array.Copy(items, position, values, 0, n);
                position += n;
                if (fetched != null && fetched.Length > 0) fetched[0] = n;
                return n == count ? 0 : 1;
            }
            public int Skip(int count)
            {
                if (count < 0) return InvalidArgument;
                int n = Math.Min(count, items.Length - position);
                position += n; return n == count ? 0 : 1;
            }
            public int Reset() { position = 0; return 0; }
            public void Clone(out IEnumFORMATETC result) { result = new FormatEnumerator(items, position); }
        }

        private static short Register(string name)
        {
            uint id = RegisterClipboardFormat(name);
            if (id == 0) throw new InvalidOperationException("无法注册 Windows 剪贴板格式。");
            return unchecked((short)id);
        }

        private static IntPtr AllocateGlobal(byte[] source)
        {
            IntPtr handle = GlobalAlloc(0x0002, (UIntPtr)(uint)source.Length); // GMEM_MOVEABLE
            if (handle == IntPtr.Zero) throw new OutOfMemoryException("可用内存不足，无法复制录屏。");
            try
            {
                IntPtr address = GlobalLock(handle);
                if (address == IntPtr.Zero) throw new OutOfMemoryException("无法锁定剪贴板内存。");
                try { Marshal.Copy(source, 0, address, source.Length); }
                finally { GlobalUnlock(handle); }
                return handle;
            }
            catch { GlobalFree(handle); throw; }
        }

        [DllImport("ole32.dll")] private static extern int OleSetClipboard(ComDataObject data);
        [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(ComDataObject data);
        [DllImport("ole32.dll")] private static extern int OleFlushClipboard();
        [DllImport("shlwapi.dll", ExactSpelling = true)]
        private static extern IntPtr SHCreateMemStream([In] byte[] data, uint count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
    }
}
