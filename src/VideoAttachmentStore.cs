using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ScreenStudio
{
    internal static class VideoAttachmentStore
    {
        private const string FolderName = "FrameDesk-VideoAttachments";
        private const string TimestampFormat = "yyyyMMdd_HHmmss";
        private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
        private static readonly Regex OwnedName = new Regex(
            @"\A录屏_[0-9]{8}_[0-9]{6}_[0-9a-f]{12}\.mp4(?:\.part)?\z", RegexOptions.CultureInvariant);

        private static string Folder
        {
            get { return Path.GetFullPath(Path.Combine(Path.GetTempPath(), FolderName)); }
        }

        internal static string Create(byte[] bytes, string fileName)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("没有可复制的录屏数据。", "bytes");
            if (String.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !String.Equals(Path.GetExtension(fileName), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("录屏文件名无效。", "fileName");

            string folder = Folder;
            Directory.CreateDirectory(folder);
            if (!IsRegularDirectory(folder)) throw new IOException("无法访问录屏临时目录。");

            DateTime recorded;
            string leaf = Path.GetFileNameWithoutExtension(fileName);
            if (!leaf.StartsWith("录屏_", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(leaf.Substring(3), TimestampFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out recorded))
                recorded = DateTime.Now;
            string stem = "录屏_" + recorded.ToString(TimestampFormat, CultureInfo.InvariantCulture) +
                          "_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string path = Path.Combine(folder, stem + ".mp4");
            string partial = path + ".part";
            bool createdPartial = false;
            try
            {
                using (FileStream output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    createdPartial = true;
                    output.Write(bytes, 0, bytes.Length);
                }
                File.Move(partial, path);
                return path;
            }
            catch
            {
                if (createdPartial) RemoveUnpublished(partial);
                throw;
            }
        }

        internal static void RemoveUnpublished(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                string folder = Folder;
                string absolute = Path.GetFullPath(path);
                if (String.Equals(Path.GetDirectoryName(absolute), folder, StringComparison.OrdinalIgnoreCase) &&
                    OwnedName.IsMatch(Path.GetFileName(absolute)) && IsRegularDirectory(folder))
                    TryDelete(absolute, null);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        internal static void CleanExpired() { CleanExpired(null); }

        internal static void CleanExpired(string protectedPath)
        {
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder) || !IsRegularDirectory(folder)) return;
                DateTime cutoff = DateTime.UtcNow - Retention;
                foreach (string path in Directory.EnumerateFiles(folder, "录屏_*.mp4*", SearchOption.TopDirectoryOnly))
                {
                    if (String.Equals(path, protectedPath, StringComparison.OrdinalIgnoreCase) ||
                        !OwnedName.IsMatch(Path.GetFileName(path))) continue;
                    if (!IsRegularDirectory(folder)) return;
                    TryDelete(path, cutoff);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsRegularDirectory(string folder)
        {
            FileAttributes attributes = File.GetAttributes(folder);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }

        private static void TryDelete(string path, DateTime? cutoff)
        {
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    return;
                if (!cutoff.HasValue || file.LastWriteTimeUtc <= cutoff.Value) file.Delete();
            }
            catch (IOException) { } // An upload may still have the file open. A later pass will retry.
            catch (UnauthorizedAccessException) { }
        }
    }
}
