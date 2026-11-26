using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenStudio
{
    internal sealed class MainForm : Form
    {
        private const int PhotoKey = 11, VideoKey = 12, FrameRate = 20;
        private const int DesignWidth = 656, DesignHeight = 537;
        private float zoom = 1;
        private readonly StudioButton photoTab, videoTab, whole, area, scroll, pointer, attachment, action, retry;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly Timer clock = new Timer();
        private readonly Timer dismissCopyNotice = new Timer();
        private readonly Timer attachmentCleanup = new Timer();
        private readonly RecordingPanel recordingPanel;
        private readonly Stopwatch elapsed = new Stopwatch();
        private bool videoMode, photoRegion = true, scrollPhoto, videoRegion, includePointer;
        private bool useAttachment = ReadAttachmentPreference(), recordAsAttachment, pendingAsAttachment;
        private bool keysStarted, photoRegistered, videoRegistered, capturing, changingRecording, stoppingSoon, closing;
        private bool showingCaptureHint;
        private ShortcutFallback fallback;
        private bool PhotoAvailable { get { return photoRegistered || (fallback != null && fallback.Installed); } }
        private bool VideoAvailable { get { return videoRegistered || (fallback != null && fallback.Installed); } }
        private bool minimizedBeforeRecording, minimizedBeforePhoto;
        private IntPtr returnAfterRecording;
        private VideoRecorder recorder;
        private string clipState = "", notice = "";
        private Bitmap pendingImage;
        private byte[] pendingVideo;
        private string pendingDetail, pendingName, pendingAttachmentPath;
        private readonly Icon appIcon;

        internal MainForm()
        {
            Text = "取景台";
            AccessibleName = "取景台 · 桌面截图与录屏";
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Art.Paper;
            DoubleBuffered = true;
            using (Graphics g = CreateGraphics()) zoom = g.DpiX / 96f;

            photoTab = Make(ButtonKind.Tab, "截图", "photo", delegate { SelectMode(false); });
            videoTab = Make(ButtonKind.Tab, "录屏", "video", delegate { SelectMode(true); });
            photoTab.Detail = "F1"; videoTab.Detail = "F2";
            photoTab.Surface = videoTab.Surface = Art.Rail;
            whole = Make(ButtonKind.Scope, "全屏", "screen", delegate { SetRegion(false); });
            area = Make(ButtonKind.Scope, "框选区域", "region", delegate { SetRegion(true); });
            scroll = Make(ButtonKind.Scope, "滚动截图", "scroll", delegate { SetRegion(true, true); });
            whole.Detail = "鼠标所在屏幕";
            area.Detail = "可跨屏框选";
            scroll.Detail = "向下滚动拼接";
            whole.Surface = area.Surface = scroll.Surface = Color.White;
            pointer = Make(ButtonKind.Switch, "显示鼠标指针", "",
                delegate { includePointer = !includePointer; RefreshState(); });
            pointer.Surface = Color.White;
            attachment = Make(ButtonKind.Switch, "聊天附件（临时 MP4）", "", ToggleAttachment);
            attachment.Surface = Color.White;
            action = Make(ButtonKind.Primary, "", "photo", delegate { RunMode(videoMode); });
            retry = Make(ButtonKind.Link, "重试复制", "", async delegate
            {
                if (pendingVideo != null) await PublishVideoAsync(); else PublishPending();
            });
            retry.Visible = false;

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("停止录屏并复制", null, delegate { RequestRecordingStop(); });
            tray = new NotifyIcon { Icon = appIcon, Text = "取景台", ContextMenuStrip = trayMenu };
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RequestRecordingStop();
            };
            recordingPanel = new RecordingPanel(RequestRecordingStop);
            dismissCopyNotice.Interval = 2300;
            dismissCopyNotice.Tick += delegate { DismissCopyNotice(); };
            attachmentCleanup.Interval = 60 * 60 * 1000;
            attachmentCleanup.Tick += delegate { CleanAttachments(); };
            clock.Interval = 250;
            clock.Tick += delegate
            {
                tray.Text = "取景台 · 录屏中 " + Duration(elapsed.Elapsed) + " · 单击停止";
                recordingPanel.UpdateElapsed(elapsed.Elapsed);
                if (Visible) Invalidate();
            };
            Arrange();
            RefreshState();
        }

        private StudioButton Make(ButtonKind kind, string text, string glyph, Action click)
        {
            StudioButton b = new StudioButton(kind, text, glyph);
            b.Click += delegate { click(); };
            Controls.Add(b);
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyDpi(GetDpiForWindow(Handle));
            keysStarted = true;
            RegisterKeys();
            CleanAttachments();
            attachmentCleanup.Start();
        }

        private static bool ReadAttachmentPreference()
        {
            try
            {
                object choice = Registry.GetValue(@"HKEY_CURRENT_USER\Software\FrameDesk", "ChatAttachment", 1);
                return !(choice is int) || (int)choice != 0;
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        private void ToggleAttachment()
        {
            useAttachment = !useAttachment;
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\FrameDesk", "ChatAttachment",
                                  useAttachment ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            RefreshState();
        }

        private void CleanAttachments()
        {
            string pending = pendingAttachmentPath;
            Task.Run(delegate { VideoAttachmentStore.CleanExpired(pending); });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (keysStarted && !closing) RegisterKeys();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            // A focused window can receive an ordinary keydown when the system does not dispatch a global hotkey.
            if (!closing && keys == Keys.F2 && (message.Msg == 0x0100 || message.Msg == 0x0104))
            {
                if ((message.LParam.ToInt64() & 0x40000000L) == 0) RunMode(true);
                return true;
            }
            return base.ProcessCmdKey(ref message, keys);
        }

        private void RegisterKeys()
        {
            const uint noRepeat = 0x4000;
            if (fallback != null) { fallback.Dispose(); fallback = null; }
            if (!photoRegistered) photoRegistered = RegisterHotKey(Handle, PhotoKey, noRepeat, (uint)Keys.F1);
            if (!videoRegistered) videoRegistered = RegisterHotKey(Handle, VideoKey, noRepeat, (uint)Keys.F2);
            if (!photoRegistered || !videoRegistered)
                fallback = new ShortcutFallback(Handle, !photoRegistered, !videoRegistered);
            if (PhotoAvailable && VideoAvailable) notice = "";
            else
            {
                string keys = !PhotoAvailable && !VideoAvailable ? "F1、F2" : (!PhotoAvailable ? "F1" : "F2");
                notice = keys + " 快捷键不可用。可用界面按钮操作。";
            }
            RefreshState();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (fallback != null) { fallback.Dispose(); fallback = null; }
            if (photoRegistered) UnregisterHotKey(Handle, PhotoKey);
            if (videoRegistered) UnregisterHotKey(Handle, VideoKey);
            photoRegistered = videoRegistered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && !closing)
            {
                int id = m.WParam.ToInt32();
                if (id == PhotoKey || id == VideoKey) RunMode(id == VideoKey);
                return;
            }
            if (m.Msg == Program.ActivateMessage && !closing)
            {
                if (recorder == null && !capturing && !changingRecording) RestoreWindow(false);
                return;
            }
            if (m.Msg == 0x02E0 && photoTab != null)
            {
                Rectangle bounds = RectangleFromNative(m.LParam);
                base.WndProc(ref m);
                ApplyDpi((uint)(m.WParam.ToInt64() & 0xffff));
                Location = bounds.Location;
                return;
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Edges { internal int Left, Top, Right, Bottom; }
        private static Rectangle RectangleFromNative(IntPtr p)
        {
            Edges e = (Edges)Marshal.PtrToStructure(p, typeof(Edges));
            return Rectangle.FromLTRB(e.Left, e.Top, e.Right, e.Bottom);
        }

        private void ApplyDpi(uint dpi)
        {
            if (dpi == 0 || Math.Abs(zoom - dpi / 96f) < .001f) return;
            zoom = dpi / 96f;
            Arrange();
        }

        private void Place(StudioButton b, int x, int y, int w, int h)
        {
            b.Zoom = zoom;
            b.Bounds = new Rectangle((int)Math.Round(x * zoom), (int)Math.Round(y * zoom),
                (int)Math.Round(w * zoom), (int)Math.Round(h * zoom));
        }

        private void Arrange()
        {
            SuspendLayout();
            ClientSize = new Size((int)Math.Round(DesignWidth * zoom), (int)Math.Round(DesignHeight * zoom));
            Place(photoTab, 33, 106, 294, 43);
            Place(videoTab, 329, 106, 294, 43);
            Place(whole, 48, 222, videoMode ? 274 : 180, 68);
            Place(area, videoMode ? 333 : 238, 222, videoMode ? 275 : 180, 68);
            Place(scroll, 428, 222, 180, 68);
            Place(pointer, 49, 305, videoMode ? 260 : 558, 37);
            Place(attachment, 335, 305, 272, 37);
            Place(action, 28, 374, 600, 54);
            Place(retry, 528, 478, 92, 31);
            ResumeLayout();
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Art.Init(g, zoom);
            Art.Box(g, 29, 24, 52, 52, 13, Art.Jade, Color.Transparent);
            Art.Icon(g, "brand", 40, 35, 30, Color.White);
            Art.Label(g, "取景台", 94, 18, 240, 41, 27, Art.Ink, true, StringAlignment.Near);
            Art.Label(g, "桌面截图与录屏", 96, 56, 300, 24, 14, Art.Muted, false, StringAlignment.Near);
            bool active = recorder != null;
            string state = active ? "录屏中" : (notice.Length > 0 ? "快捷键冲突" : "就绪");
            float pill = active ? 87 : (notice.Length > 0 ? 111 : 71);
            Art.Box(g, 627 - pill, 36, pill, 29, 14, active ? Color.FromArgb(255, 231, 226) : Art.Mint, Color.Transparent);
            Art.Dot(g, active ? Color.Firebrick : Art.Jade, 640 - pill, 47, 7);
            Art.Label(g, state, 651 - pill, 36, pill - 28, 29, 12, Art.Ink, true, StringAlignment.Center);

            Art.Box(g, 28, 101, 600, 53, 11, Art.Rail, Color.Transparent);
            Art.Box(g, 28, 172, 600, 186, 13, Color.White, Art.Line);
            Art.Label(g, "捕获范围", 48, 183, 200, 29, 16, Art.Ink, true, StringAlignment.Near);
            Art.Label(g, videoMode ? "MP4  /  无声" : "PNG  /  方框、箭头与文字", 270, 185, 338, 25, 13, Art.Muted, false, StringAlignment.Far);
            using (Pen rule = new Pen(Art.Line))
            {
                g.DrawLine(rule, 49, 301, 606, 301);
                g.DrawLine(rule, 29, 449, 627, 449);
            }
            Art.Label(g, "剪贴板", 29, 461, 190, 25, 15, Art.Ink, true, StringAlignment.Near);
            string message = clipState.Length > 0 ? clipState : (videoMode ?
                (useAttachment ? "停止后，在聊天输入框按 Ctrl + V 添加 MP4 附件。" :
                                 "停止后复制内存中的视频。") : (scrollPhoto ?
                 "框选可滚动的内容，向下滚动，完成后标注并复制长图。" : "完成标注后复制，再到需要的位置按 Ctrl + V。"));
            if (notice.Length > 0) message = message + "  " + notice;
            Art.Label(g, message, 29, 490, retry != null && retry.Visible ? 491 : 600, 28, 13,
                notice.Length > 0 ? Color.FromArgb(131, 81, 18) : Art.Muted, false, StringAlignment.Near);
        }

        private void SelectMode(bool video)
        {
            if (videoMode != video)
            {
                videoMode = video;
                Arrange();
            }
            RefreshState();
        }
        private void SetRegion(bool value, bool scrolling = false)
        {
            if (videoMode) videoRegion = value;
            else { photoRegion = value; scrollPhoto = scrolling; }
            RefreshState();
        }
        private void RefreshState()
        {
            bool region = videoMode ? videoRegion : photoRegion;
            photoTab.Selected = !videoMode; videoTab.Selected = videoMode;
            bool scrolling = !videoMode && scrollPhoto;
            whole.Selected = !region; area.Selected = region && !scrolling;
            scroll.Visible = !videoMode;
            scroll.Selected = scrolling;
            scroll.AccessibleDescription = scrolling ? "已选中，向下滚动拼接长图" : "框选可滚动区域并拼接长图";
            pointer.Selected = includePointer;
            pointer.AccessibleDescription = includePointer ? "已开启" : "已关闭";
            attachment.Visible = videoMode;
            attachment.Selected = useAttachment;
            attachment.AccessibleDescription = useAttachment ? "已开启，复制临时 MP4 文件附件" : "已关闭，视频只存放在内存";
            whole.AccessibleDescription = whole.Selected ? "已选中" : "选择全屏";
            area.AccessibleDescription = area.Selected ? "已选中" : "选择区域";
            action.Text = videoMode ? (recorder == null ? "开始录屏" : "停止录屏并复制") :
                (scrolling ? "开始滚动截图" : (region ? "框选截图并标注" : "全屏截图并标注"));
            action.Glyph = videoMode ? "video" : (scrolling ? "scroll" : "photo");
            action.Detail = videoMode ? (VideoAvailable ? "F2 开始 / 停止" : "使用此按钮") :
                (PhotoAvailable ? "F1 截图" : "使用此按钮");
            Invalidate(true);
        }

        private void RunMode(bool video)
        {
            if (closing) return;
            if (video && (recorder != null || changingRecording))
            {
                RequestRecordingStop();
                return;
            }
            if (capturing && video && !showingCaptureHint)
            {
                Form editor = Form.ActiveForm;
                if (editor != null && editor != this)
                {
                    showingCaptureHint = true;
                    try
                    {
                        MessageBox.Show(editor, "请先完成或取消当前截图，再按 F2 开始录屏。", "截图进行中",
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    finally { showingCaptureHint = false; }
                }
            }
            if (changingRecording || capturing) return;
            DismissCopyNotice();
            SelectMode(video);
            if (video) BeginRecording(); else CapturePhoto();
        }

        private void DismissCopyNotice()
        {
            if (!dismissCopyNotice.Enabled) return;
            dismissCopyNotice.Stop();
            recordingPanel.Hide();
        }

        private void RequestRecordingStop()
        {
            if (closing) return;
            if (changingRecording) stoppingSoon = true;
            else if (recorder != null) EndRecording();
        }

        private async void CapturePhoto()
        {
            capturing = true;
            bool duringRecording = recorder != null;
            minimizedBeforePhoto = WindowState == FormWindowState.Minimized;
            IntPtr previousWindow = GetForegroundWindow();
            Rectangle captureBounds = Screen.FromPoint(Cursor.Position).Bounds;
            try
            {
                Hide();
                await Task.Delay(160);
                if (closing) return;
                DwmFlush();
                Bitmap image;
                bool scrolling = photoRegion && scrollPhoto;
                if (photoRegion)
                {
                    Rectangle? selected;
                    image = RegionPicker.PickPhoto(includePointer, true, ref scrolling, out selected);
                    if (!selected.HasValue) return;
                    captureBounds = selected.Value;
                    if (scrolling)
                        image = await ScrollCaptureSession.CaptureAsync(captureBounds, includePointer);
                }
                else image = ScreenCapture.Grab(captureBounds, includePointer);
                using (Bitmap original = image)
                {
                    if (closing || original == null) return;
                    Bitmap edited = AnnotationEditor.Edit(original, null, scrolling, captureBounds);
                    if (edited == null) return;
                    ClearPending();
                    pendingImage = edited;
                    pendingDetail = "截图已复制  ·  " + edited.Width + " × " + edited.Height + " 像素";
                    PublishPending();
                }
            }
            catch (Exception ex) { ShowError("截图未完成", ex); }
            finally
            {
                capturing = false;
                if (!closing && recorder == null && !changingRecording)
                    RestoreWindow(duringRecording ? minimizedBeforeRecording : minimizedBeforePhoto,
                                  duringRecording ? returnAfterRecording : previousWindow);
            }
        }

        private async void BeginRecording()
        {
            changingRecording = true;
            recordAsAttachment = useAttachment;
            stoppingSoon = false;
            minimizedBeforeRecording = WindowState == FormWindowState.Minimized;
            returnAfterRecording = GetForegroundWindow();
            Rectangle full = Screen.FromPoint(Cursor.Position).Bounds;
            try
            {
                Hide();
                Rectangle region = full;
                if (videoRegion)
                {
                    await Task.Delay(160);
                    if (closing) return;
                    DwmFlush();
                    Rectangle? picked = RegionPicker.Pick();
                    if (!picked.HasValue) return;
                    region = picked.Value;
                }
                bool panelDuringCapture = recordingPanel.TryShowFor(region);
                tray.Text = "取景台 · 正在准备录屏";
                tray.Visible = true;
                if (!panelDuringCapture)
                {
                    // This display cannot exclude the panel. Show the acknowledgement before recording its first frame.
                    recordingPanel.ShowPreparationNotice();
                    await Task.Delay(850);
                    recordingPanel.Hide();
                }
                else await Task.Delay(100);
                if (closing) return;
                DwmFlush();
                VideoRecorder current = new VideoRecorder(region, FrameRate, includePointer);
                recorder = current;
                current.Failed += delegate(Exception ex)
                {
                    if (closing || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (closing || recorder != current) return;
                            if (changingRecording) stoppingSoon = true; else EndRecording();
                        });
                    }
                    catch (InvalidOperationException) { }
                };
                await Task.Run((Action)current.Start);
                if (closing || recorder != current) return;
                elapsed.Restart();
                recordingPanel.UpdateElapsed(TimeSpan.Zero);
                tray.Text = "取景台 · 录屏中 · 按 F2 或单击图标停止";
                if (!panelDuringCapture)
                    tray.ShowBalloonTip(1800, "已开始录屏", "再次按 F2 或单击取景台托盘图标停止。", ToolTipIcon.Info);
                clock.Start();
            }
            catch (Exception ex)
            {
                if (recorder != null) { recorder.Dispose(); recorder = null; }
                recordingPanel.Hide();
                tray.Visible = false;
                ShowError("无法开始录屏", ex);
            }
            finally
            {
                changingRecording = false;
                if (!closing && recorder == null)
                {
                    tray.Visible = false;
                    recordingPanel.Hide();
                    RestoreWindow(minimizedBeforeRecording, returnAfterRecording);
                }
                if (!closing && recorder != null && stoppingSoon) EndRecording();
            }
        }

        private async void EndRecording()
        {
            if (changingRecording || recorder == null) return;
            changingRecording = true;
            VideoRecorder current = recorder;
            clock.Stop();
            elapsed.Stop();
            recordingPanel.ShowFinishing();
            tray.Text = "取景台 · 正在复制录屏";
            try
            {
                byte[] bytes = await Task.Run((Func<byte[]>)current.Stop);
                if (closing) return;
                StageVideo(bytes);
                if (await PublishVideoAsync())
                {
                    recordingPanel.ShowCopied(elapsed.Elapsed);
                    dismissCopyNotice.Start();
                }
            }
            catch (Exception ex) { ShowError("录屏未完成", ex); }
            finally
            {
                current.Dispose();
                recorder = null;
                changingRecording = false;
                tray.Visible = false;
                if (!dismissCopyNotice.Enabled) recordingPanel.Hide();
                if (!closing && !capturing) RestoreWindow(minimizedBeforeRecording, returnAfterRecording);
            }
        }

        private static string Duration(TimeSpan value)
        {
            return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
        }

        private void StageVideo(byte[] bytes)
        {
            ClearPending();
            pendingVideo = bytes;
            pendingAsAttachment = recordAsAttachment;
            pendingName = "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".mp4";
            pendingDetail = "录屏已复制  ·  " + Duration(elapsed.Elapsed) + "  ·  " +
                (bytes.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" +
                (pendingAsAttachment ? "  ·  可粘贴为 MP4 附件" : "");
        }

        private async Task<bool> PublishVideoAsync()
        {
            byte[] video = pendingVideo;
            if (video == null) return false;
            if (!pendingAsAttachment || File.Exists(pendingAttachmentPath)) return PublishPending();
            retry.Enabled = false;
            string name = pendingName;
            try
            {
                string path = await Task.Run(delegate { return VideoAttachmentStore.Create(video, name); });
                if (closing || !Object.ReferenceEquals(pendingVideo, video))
                {
                    VideoAttachmentStore.RemoveUnpublished(path);
                    return false;
                }
                pendingAttachmentPath = path;
                return PublishPending();
            }
            catch (Exception ex)
            {
                if (Object.ReferenceEquals(pendingVideo, video) && !closing) ReportCopyFailure(ex);
                return false;
            }
            finally { if (!IsDisposed) retry.Enabled = true; }
        }

        private bool PublishPending()
        {
            try
            {
                if (pendingImage != null)
                {
                    using (MemoryStream png = new MemoryStream())
                    using (MemoryStream localOnly = new MemoryStream(new byte[] { 1 }))
                    {
                        pendingImage.Save(png, ImageFormat.Png);
                        png.Position = 0;
                        DataObject data = new DataObject();
                        data.SetImage(pendingImage);
                        data.SetData("PNG", false, png);
                        data.SetData("ExcludeClipboardContentFromMonitorProcessing", false, localOnly);
                        Clipboard.SetDataObject(data, true, 10, 50);
                    }
                }
                else if (pendingVideo != null)
                {
                    if (pendingAsAttachment && !File.Exists(pendingAttachmentPath))
                        pendingAttachmentPath = VideoAttachmentStore.Create(pendingVideo, pendingName);
                    string name = pendingAttachmentPath == null ? pendingName : Path.GetFileName(pendingAttachmentPath);
                    VideoClipboard.Copy(pendingVideo, name, pendingAttachmentPath);
                    // The receiving app may read this attachment asynchronously, after the clipboard changes.
                    pendingAttachmentPath = null;
                }
                else return false;
                clipState = pendingDetail;
                ClearPending();
                Invalidate();
                return true;
            }
            catch (Exception ex)
            {
                ReportCopyFailure(ex);
                return false;
            }
        }

        private void ReportCopyFailure(Exception ex)
        {
            clipState = "复制失败，内容已保留，可重试。";
            retry.Visible = true;
            ShowError("复制到剪贴板失败", ex);
            Invalidate();
        }

        private void ClearPending()
        {
            if (pendingImage != null) pendingImage.Dispose();
            if (pendingAttachmentPath != null) VideoAttachmentStore.RemoveUnpublished(pendingAttachmentPath);
            pendingAttachmentPath = null;
            pendingImage = null; pendingVideo = null;
            retry.Visible = false;
        }

        private void ShowError(string title, Exception ex)
        {
            if (!closing) MessageBox.Show(ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void RestoreWindow(bool minimized, IntPtr previousWindow = default(IntPtr))
        {
            WindowState = minimized ? FormWindowState.Minimized : FormWindowState.Normal;
            Show();
            if (previousWindow != IntPtr.Zero && previousWindow != Handle && IsWindowVisible(previousWindow))
                SetForegroundWindow(previousWindow);
            else if (!minimized) Activate();
            if (!PhotoAvailable || !VideoAvailable) RegisterKeys();
            RefreshState();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // A close during asynchronous finalization must not discard the last captured frame.
            if (changingRecording && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            closing = true;
            clock.Stop();
            if (recorder != null)
            {
                try { elapsed.Stop(); StageVideo(recorder.Stop()); PublishPending(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
                finally { recorder.Dispose(); recorder = null; }
            }
            try { VideoClipboard.FlushOwned(); }
            catch (ExternalException ex)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    closing = false;
                    e.Cancel = true;
                    ShowError("剪贴板正在使用", ex);
                    return;
                }
                Debug.WriteLine(ex);
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clock.Dispose();
                dismissCopyNotice.Dispose();
                attachmentCleanup.Dispose();
                if (pendingAttachmentPath != null) VideoAttachmentStore.RemoveUnpublished(pendingAttachmentPath);
                if (recordingPanel != null) recordingPanel.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
                if (pendingImage != null) pendingImage.Dispose();
                if (appIcon != null) appIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
