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
        private Point contentOrigin;
        private readonly StudioButton photoTab, videoTab, whole, area, scroll, pointer, microphone, action, retry;
        private readonly Panel outputChoices;
        private readonly OutputChoice attachment, memoryOnly;
        private const string IdleTrayStatus = "取景台 · 单击打开，悬停可退出";
        private readonly TrayIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly Timer clock = new Timer();
        private readonly Timer dismissCopyNotice = new Timer();
        private readonly Timer attachmentCleanup = new Timer();
        private readonly CaptureRegionGuide captureGuide = new CaptureRegionGuide();
        private readonly RecordingPanel recordingPanel;
        private readonly Stopwatch elapsed = new Stopwatch();
        private bool videoMode, photoRegion = true, scrollPhoto, videoRegion, includePointer;
        private bool useAttachment = ReadPreference("ChatAttachment"), recordAsAttachment, pendingAsAttachment;
        private bool useMicrophone = ReadPreference("RecordMicrophone"), recordWithMicrophone;
        private bool keysStarted, photoRegistered, videoRegistered, capturing, changingRecording, stoppingSoon, closing;
        private bool showingCaptureHint, hiddenToTray, exitRequested;
        private ShortcutFallback fallback;
        private bool PhotoAvailable { get { return photoRegistered || (fallback != null && fallback.Installed); } }
        private bool VideoAvailable { get { return videoRegistered || (fallback != null && fallback.Installed); } }
        private FormWindowState stateBeforeRecording;
        private IntPtr returnAfterRecording;
        private VideoRecorder recorder;
        private string clipState = "", notice = "";
        private Bitmap pendingImage;
        private byte[] pendingVideo;
        private string pendingDetail, pendingName, pendingAttachmentPath;
        private readonly Icon appIcon;
        private readonly Bitmap cameraSticker;

        internal MainForm()
        {
            Text = "取景台";
            AccessibleName = "取景台 · 桌面截图与录屏";
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            using (Stream embedded = typeof(MainForm).Assembly.GetManifestResourceStream("ScreenStudio.CameraMascot.png"))
            {
                if (embedded != null)
                    using (var image = new Bitmap(embedded)) cameraSticker = new Bitmap(image);
            }
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Art.Paper;
            DoubleBuffered = true;
            using (Graphics g = CreateGraphics())
                ClientSize = new Size((int)Math.Round(DesignWidth * g.DpiX / 96f),
                                      (int)Math.Round(DesignHeight * g.DpiY / 96f));

            photoTab = Make(ButtonKind.Tab, "截图", "photo", delegate { SelectMode(false); });
            videoTab = Make(ButtonKind.Tab, "录屏", "video", delegate { SelectMode(true); });
            photoTab.Surface = videoTab.Surface = Art.Rail;
            whole = Make(ButtonKind.Scope, "全屏", "screen", delegate { SetRegion(false); });
            area = Make(ButtonKind.Scope, "框选区域", "region", delegate { SetRegion(true); });
            scroll = Make(ButtonKind.Scope, "滚动截图", "scroll", delegate { SetRegion(true, true); });
            whole.Detail = "鼠标所在屏幕";
            area.Detail = "可跨屏框选";
            scroll.Detail = "上下滚动拼接";
            whole.Surface = area.Surface = scroll.Surface = Color.White;
            pointer = Make(ButtonKind.Switch, "显示鼠标指针", "cursor",
                delegate { includePointer = !includePointer; RefreshState(); });
            pointer.Surface = Color.White;
            microphone = Make(ButtonKind.Switch, "录制麦克风", "microphone", delegate {
                if (recorder != null || changingRecording) return;
                useMicrophone = !useMicrophone;
                SavePreference("RecordMicrophone", useMicrophone);
                RefreshState();
            });
            microphone.Surface = Color.White;
            outputChoices = new Panel { AccessibleName = "录屏完成后", AccessibleRole = AccessibleRole.Grouping,
                                        BackColor = Color.White, TabStop = false };
            Controls.Add(outputChoices);
            attachment = AddOutputChoice("复制为聊天附件", "可粘贴到 i讯飞，取景台运行时清理超过 24 小时的临时 MP4。", true);
            memoryOnly = AddOutputChoice("仅在内存中复制", "在支持的应用中粘贴，粘贴前请保持取景台运行。", false);
            action = Make(ButtonKind.Primary, "", "photo", delegate { RunMode(videoMode); });
            retry = Make(ButtonKind.Link, "重试复制", "", async delegate
            {
                if (pendingVideo != null) await PublishVideoAsync(); else PublishPending();
            });
            retry.Surface = Art.Note;
            retry.Visible = false;

            trayMenu = new ContextMenuStrip();
            ToolStripItem openItem = trayMenu.Items.Add("打开取景台", null, delegate { OpenFromTray(); });
            ToolStripItem stopItem = trayMenu.Items.Add("停止录屏并复制", null, delegate { RequestRecordingStop(); });
            ToolStripItem exitItem = trayMenu.Items.Add("退出取景台", null, delegate { RequestTrayExit(); });
            trayMenu.Opening += delegate
            {
                openItem.Enabled = !capturing && recorder == null && !changingRecording && !exitRequested;
                stopItem.Available = recorder != null || changingRecording;
                exitItem.Text = TrayExitCaption();
            };
            tray = new TrayIcon(appIcon, trayMenu, delegate {
                if (recorder != null || changingRecording) RequestRecordingStop(); else OpenFromTray();
            }, RequestTrayExit, TrayExitCaption);
            recordingPanel = new RecordingPanel(RequestRecordingStop);
            dismissCopyNotice.Interval = 2300;
            dismissCopyNotice.Tick += delegate { DismissCopyNotice(); };
            attachmentCleanup.Interval = 60 * 60 * 1000;
            attachmentCleanup.Tick += delegate { CleanAttachments(); };
            clock.Interval = 250;
            clock.Tick += delegate
            {
                tray.SetStatus("取景台 · " + (recordWithMicrophone ? "录屏及麦克风" : "无声录屏") + " " + Duration(elapsed.Elapsed) + " · 单击停止");
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

        private OutputChoice AddOutputChoice(string title, string detail, bool asAttachment)
        {
            var choice = new OutputChoice(title, detail);
            choice.CheckedChanged += delegate
            {
                if (!choice.Checked || useAttachment == asAttachment) return;
                useAttachment = asAttachment;
                SavePreference("ChatAttachment", useAttachment);
                RefreshState();
            };
            outputChoices.Controls.Add(choice);
            return choice;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tray.Start(IdleTrayStatus);
            FitToDisplay(GetDpiForWindow(Handle), Screen.FromHandle(Handle).WorkingArea, Bounds);
            keysStarted = true;
            RegisterKeys();
            CleanAttachments();
            attachmentCleanup.Start();
        }

        private static bool ReadPreference(string name)
        {
            try
            {
                object choice = Registry.GetValue(@"HKEY_CURRENT_USER\Software\FrameDesk", name, 1);
                return !(choice is int) || (int)choice != 0;
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        private static void SavePreference(string name, bool value)
        {
            try
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\FrameDesk", name,
                                  value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
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
            if (m.Msg == 0x007E && !closing && captureGuide != null) // WM_DISPLAYCHANGE
            {
                base.WndProc(ref m);
                BeginInvoke((Action)captureGuide.Refresh);
                return;
            }
            if (m.Msg == 0x0312 && !closing)
            {
                int id = m.WParam.ToInt32();
                if (id == PhotoKey || id == VideoKey) RunMode(id == VideoKey);
                return;
            }
            if (m.Msg == Program.ActivateMessage && !closing)
            {
                OpenFromTray();
                return;
            }
            if (m.Msg == 0x02E0 && photoTab != null)
            {
                Rectangle bounds = RectangleFromNative(m.LParam);
                base.WndProc(ref m);
                FitToDisplay((uint)(m.WParam.ToInt64() & 0xffff), Screen.FromRectangle(bounds).WorkingArea, bounds);
                return;
            }
            if (m.Msg == 0x0112 && (m.WParam.ToInt64() & 0xfff0) == 0xf120 && // SC_RESTORE
                WindowState == FormWindowState.Maximized)
            {
                Rectangle normal = RestoreBounds;
                base.WndProc(ref m);
                // WinForms can add the primary screen's frame thickness on a secondary monitor.
                if (WindowState == FormWindowState.Normal && Location == normal.Location && Size != normal.Size)
                    Size = normal.Size;
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

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            FitToDisplay(GetDpiForWindow(Handle), Screen.FromHandle(Handle).WorkingArea, Bounds);
        }

        private void FitToDisplay(uint dpi, Rectangle workArea, Rectangle requested)
        {
            if (dpi == 0 || WindowState == FormWindowState.Minimized) return;
            int chromeWidth = Width - ClientSize.Width, chromeHeight = Height - ClientSize.Height;
            float availableWidth = (workArea.Width - chromeWidth) / (float)DesignWidth;
            float availableHeight = (workArea.Height - chromeHeight) / (float)DesignHeight;
            float scale = Math.Min(.85f * dpi / 96f, Math.Min(availableWidth, availableHeight));
            if (scale <= 0) return;
            MinimumSize = new Size((int)Math.Round(DesignWidth * scale) + chromeWidth,
                                   (int)Math.Round(DesignHeight * scale) + chromeHeight);
            if (WindowState != FormWindowState.Normal) return;
            int w = Math.Min(workArea.Width, Math.Max(MinimumSize.Width, requested.Width));
            int h = Math.Min(workArea.Height, Math.Max(MinimumSize.Height, requested.Height));
            Bounds = new Rectangle(Math.Max(workArea.Left, Math.Min(requested.X, workArea.Right - w)),
                                   Math.Max(workArea.Top, Math.Min(requested.Y, workArea.Bottom - h)), w, h);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            if (retry != null && WindowState != FormWindowState.Minimized) Arrange();
        }

        private void Place(StudioButton b, int x, int y, int w, int h)
        {
            b.Zoom = zoom;
            b.Bounds = ScaledBounds(x, y, w, h);
        }

        private Rectangle ScaledBounds(int x, int y, int w, int h)
        {
            return new Rectangle(contentOrigin.X + (int)Math.Round(x * zoom), contentOrigin.Y + (int)Math.Round(y * zoom),
                                 (int)Math.Round(w * zoom), (int)Math.Round(h * zoom));
        }

        private void Arrange()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            zoom = Math.Min(ClientSize.Width / (float)DesignWidth, ClientSize.Height / (float)DesignHeight);
            contentOrigin = new Point((ClientSize.Width - (int)Math.Round(DesignWidth * zoom)) / 2,
                                      (ClientSize.Height - (int)Math.Round(DesignHeight * zoom)) / 2);
            SuspendLayout();
            Place(photoTab, 32, 108, 96, 28);
            Place(videoTab, 130, 108, 96, 28);
            int scopeY = videoMode ? 185 : 197, scopeHeight = videoMode ? 56 : 77;
            Place(whole, 40, scopeY, videoMode ? 282 : 184, scopeHeight);
            Place(area, videoMode ? 334 : 236, scopeY, videoMode ? 282 : 184, scopeHeight);
            Place(scroll, 432, scopeY, 184, scopeHeight);
            Place(pointer, 40, videoMode ? 253 : 288, videoMode ? 275 : 576, 34);
            Place(microphone, 341, 253, 275, 34);
            outputChoices.Bounds = ScaledBounds(40, 340, 576, 80);
            int choiceHeight = (int)Math.Round(38 * zoom);
            attachment.Zoom = memoryOnly.Zoom = zoom;
            attachment.Bounds = new Rectangle(0, 0, outputChoices.ClientSize.Width, choiceHeight);
            memoryOnly.Bounds = new Rectangle(0, outputChoices.ClientSize.Height - choiceHeight, outputChoices.ClientSize.Width, choiceHeight);
            Place(action, 24, videoMode ? 439 : 351, 608, 55);
            Place(retry, 529, videoMode ? 498 : 449, 87, 28);
            ResumeLayout();
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.TranslateTransform(contentOrigin.X, contentOrigin.Y);
            Art.Init(g, zoom);
            Art.DrawCover(g, cameraSticker);
            Art.Label(g, "FrameDesk", 45, 23, 145, 19, 13, Art.Berry, true, StringAlignment.Near);
            Art.Label(g, "取景台", 43, 40, 107, 44, 30, Art.Ink, true, StringAlignment.Near);
            bool active = recorder != null;
            string state = active ? "录屏中" : (notice.Length > 0 ? "快捷键冲突" : "就绪");
            float pill = active ? 74 : (notice.Length > 0 ? 96 : 60);
            Color signal = active || notice.Length > 0 ? Art.Berry : Art.Leaf;
            Art.Box(g, 157, 53, pill, 24, 12, active || notice.Length > 0 ? Art.Note : Color.FromArgb(230, 247, 236), Color.Transparent);
            Art.Dot(g, signal, 167, 62, 6);
            Art.Label(g, state, 177, 53, pill-26, 24, 12, signal, true, StringAlignment.Center);

            Art.Box(g, 24, 104, 210, 36, 18, Art.Rail, Art.Line);
            string format = videoMode ? (useMicrophone ? "MP4 · 麦克风已开启" : "MP4 · 麦克风已关闭") : "PNG · 支持标注";
            Art.Label(g, notice.Length > 0 ? notice : format, 242, 104, 387, 36, 12.5f,
                      notice.Length > 0 ? Art.Berry : Art.Muted, false, StringAlignment.Far);

            Art.Box(g, 24, 151, 608, videoMode ? 145 : 185, 16, Color.White, Art.Line);
            Art.Label(g, "捕获范围", 40, videoMode ? 154 : 160, 155, 29, 16, Art.Ink, true, StringAlignment.Near);
            using (Pen rule = new Pen(Art.Line))
                g.DrawLine(rule, 42, videoMode ? 247 : 281, 614, videoMode ? 247 : 281);

            if (videoMode)
            {
                Art.Box(g, 24, 307, 608, 122, 16, Color.White, Art.Line);
                Art.Label(g, "录屏完成后", 40, 310, 180, 29, 16, Art.Ink, true, StringAlignment.Near);
                Art.Icon(g, useAttachment && clipState.Length == 0 ? "clip" : "clipboard", 28, 503, 18, Art.Muted);
                string message = clipState.Length > 0 ? clipState :
                    (useAttachment ? "停止录屏后，按 Ctrl + V 粘贴 MP4 附件。" : "停止录屏后，在支持的应用中按 Ctrl + V 粘贴。");
                Art.Label(g, message, 52, 496, retry.Visible ? 468 : 564, 31, 12.5f, Art.Muted, false, StringAlignment.Near);
            }
            else
            {
                Art.Box(g, 24, 421, 608, 86, 16, Art.Note, Color.FromArgb(228, 210, 171));
                Art.Box(g, 40, 445, 38, 38, 12, Color.FromArgb(255, 253, 242), Color.Transparent);
                Art.Icon(g, "clipboard", 48, 453, 22, Color.FromArgb(127, 83, 46));
                Art.Label(g, "剪贴板", 90, 436, 152, 26, 15, Art.Ink, true, StringAlignment.Near);
                string message = clipState.Length > 0 ? clipState : (scrollPhoto ?
                    "框选后可以向上或向下滚动，完成后标注并复制长图。" : "完成标注后按 Enter 复制，再到需要的位置按 Ctrl + V 粘贴。");
                Art.Label(g, message, 90, 465, retry.Visible ? 431 : 524, 25, 13, Art.Muted, false, StringAlignment.Near);
            }
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
            photoTab.AccessibleDescription = videoMode ? "切换到截图" : "已选中截图";
            videoTab.AccessibleDescription = videoMode ? "已选中录屏" : "切换到录屏";
            bool scrolling = !videoMode && scrollPhoto;
            whole.Selected = !region; area.Selected = region && !scrolling;
            scroll.Visible = !videoMode;
            scroll.Selected = scrolling;
            scroll.AccessibleDescription = scrolling ? "已选中，向上或向下滚动拼接长图" : "框选可滚动区域并拼接长图";
            pointer.Selected = includePointer;
            pointer.AccessibleDescription = includePointer ? "已开启" : "已关闭";
            microphone.Visible = videoMode;
            microphone.Enabled = recorder == null && !changingRecording;
            microphone.Selected = useMicrophone;
            microphone.AccessibleDescription = useMicrophone ? "已开启，录制 Windows 默认麦克风" : "已关闭，录制无声视频";
            outputChoices.Visible = videoMode;
            attachment.Checked = useAttachment;
            memoryOnly.Checked = !useAttachment;
            whole.AccessibleDescription = whole.Selected ? "已选中" : "选择全屏";
            area.AccessibleDescription = area.Selected ? "已选中" : "选择区域";
            action.Text = videoMode ? (recorder == null ? "开始录屏" : "停止录屏并复制") :
                (scrolling ? "开始滚动截图" : (region ? "开始框选截图" : "开始全屏截图"));
            action.Glyph = videoMode ? "video" : (scrolling ? "scroll" : "photo");
            action.Detail = videoMode ? (VideoAvailable ? "F2 开始 / 停止" : "使用此按钮") :
                (PhotoAvailable ? "F1" : "使用此按钮");
            Invalidate(true);
        }

        private void RunMode(bool video)
        {
            if (closing || exitRequested) return;
            tray.Dismiss();
            if (trayMenu.Visible) trayMenu.Close();
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
            FormWindowState stateBeforePhoto = WindowState;
            IntPtr previousWindow = GetForegroundWindow();
            Rectangle captureBounds = Screen.FromPoint(Cursor.Position).Bounds;
            try
            {
                captureGuide.SetTakingPhoto(true);
                Hide();
                await Task.Delay(160);
                if (closing || exitRequested) return;
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
                    {
                        try
                        {
                            captureGuide.SetScrolling(captureBounds);
                            image = await ScrollCaptureSession.CaptureAsync(captureBounds, includePointer);
                        }
                        finally { captureGuide.SetScrolling(null); }
                    }
                }
                else image = ScreenCapture.Grab(captureBounds, includePointer);
                using (Bitmap original = image)
                {
                    if (closing || exitRequested || original == null) return;
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
                captureGuide.SetTakingPhoto(false);
                if (!closing && recorder == null && !changingRecording)
                    RestoreWindow(duringRecording ? stateBeforeRecording : stateBeforePhoto,
                                  duringRecording ? returnAfterRecording : previousWindow);
                FinishTrayExitWhenReady();
            }
        }

        private async void BeginRecording()
        {
            changingRecording = true;
            recordAsAttachment = useAttachment;
            recordWithMicrophone = useMicrophone;
            RefreshState();
            stoppingSoon = false;
            stateBeforeRecording = WindowState;
            returnAfterRecording = GetForegroundWindow();
            Rectangle full = Screen.FromPoint(Cursor.Position).Bounds;
            try
            {
                Hide();
                Rectangle region = full;
                if (videoRegion)
                {
                    await Task.Delay(160);
                    if (closing || exitRequested) return;
                    DwmFlush();
                    Rectangle? picked = RegionPicker.Pick();
                    if (!picked.HasValue) return;
                    region = picked.Value;
                }
                captureGuide.SetRecording(region, videoRegion);
                bool panelDuringCapture = recordingPanel.TryShowFor(region, recordWithMicrophone);
                tray.SetStatus("取景台 · 正在准备录屏");
                if (!panelDuringCapture)
                {
                    // This display cannot exclude the panel. Show the acknowledgement before recording its first frame.
                    recordingPanel.ShowPreparationNotice();
                    await Task.Delay(850);
                    recordingPanel.Hide();
                }
                else await Task.Delay(100);
                if (closing || exitRequested) { captureGuide.SetRecording(null); return; }
                DwmFlush();
                VideoRecorder current = new VideoRecorder(region, FrameRate, includePointer, recordWithMicrophone);
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
                tray.SetStatus("取景台 · " + (recordWithMicrophone ? "正在录制麦克风" : "无声录屏") + " · 按 F2 停止");
                if (!panelDuringCapture)
                    tray.ShowRecordingNotice();
                clock.Start();
            }
            catch (Exception ex)
            {
                captureGuide.SetRecording(null);
                if (recorder != null) { recorder.Dispose(); recorder = null; }
                recordingPanel.Hide();
                ShowError("无法开始录屏", ex);
            }
            finally
            {
                changingRecording = false;
                if (!closing && recorder == null)
                {
                    tray.SetStatus(IdleTrayStatus);
                    recordingPanel.Hide();
                    RestoreWindow(stateBeforeRecording, returnAfterRecording);
                }
                if (!closing && recorder != null && stoppingSoon) EndRecording();
                FinishTrayExitWhenReady();
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
            tray.SetStatus("取景台 · 正在复制录屏");
            try
            {
                byte[] bytes;
                try { bytes = await Task.Run((Func<byte[]>)current.Stop); }
                finally { captureGuide.SetRecording(null); }
                if (closing) return;
                StageVideo(bytes);
                if (await PublishVideoAsync())
                {
                    recordingPanel.ShowCopied(elapsed.Elapsed);
                    dismissCopyNotice.Start();
                }
                if (current.MicrophoneError != null)
                    ShowError("麦克风录音中断，录屏已提前结束", current.MicrophoneError);
            }
            catch (Exception ex) { ShowError("录屏未完成", ex); }
            finally
            {
                current.Dispose();
                recorder = null;
                changingRecording = false;
                tray.SetStatus(IdleTrayStatus);
                if (!dismissCopyNotice.Enabled) recordingPanel.Hide();
                if (!closing && !capturing) RestoreWindow(stateBeforeRecording, returnAfterRecording);
                FinishTrayExitWhenReady();
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

        private void RestoreWindow(FormWindowState state, IntPtr previousWindow = default(IntPtr))
        {
            if (exitRequested) return;
            if (!hiddenToTray)
            {
                if (WindowState != state) WindowState = state;
                if (!Visible) Show();
                if (previousWindow != IntPtr.Zero && previousWindow != Handle && IsWindowVisible(previousWindow))
                    SetForegroundWindow(previousWindow);
                else if (state != FormWindowState.Minimized) Activate();
            }
            if (!PhotoAvailable || !VideoAvailable) RegisterKeys();
            RefreshState();
        }

        private void OpenFromTray()
        {
            if (closing || exitRequested || capturing || changingRecording || recorder != null) return;
            tray.Dismiss();
            hiddenToTray = false;
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9); // SW_RESTORE
            RestoreWindow(WindowState);
        }

        private string TrayExitCaption()
        {
            if (capturing) return "取消截图并退出";
            return recorder != null || changingRecording ? "结束录屏并退出" : "退出取景台";
        }

        private void RequestTrayExit()
        {
            if (closing || exitRequested) return;
            exitRequested = true;
            if (capturing || changingRecording)
            {
                // The picker and annotation editor are modal. The scroll controller is modeless.
                foreach (Form form in Application.OpenForms)
                    if (form.Modal) { form.DialogResult = DialogResult.Cancel; break; }
                if (capturing) ScrollCaptureSession.CancelActive();
            }
            if (recorder != null || changingRecording) RequestRecordingStop();
            FinishTrayExitWhenReady();
        }

        private void FinishTrayExitWhenReady()
        {
            if (!exitRequested || closing || capturing || changingRecording || recorder != null) return;
            // Let the tray's click/notification handler return before disposing its window.
            BeginInvoke((Action)delegate { if (exitRequested && !closing) Close(); });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                if (!exitRequested)
                {
                    e.Cancel = true;
                    hiddenToTray = true;
                    tray.Dismiss();
                    Hide();
                    return;
                }
                if (capturing || changingRecording || recorder != null) { e.Cancel = true; return; }
                if ((pendingVideo != null || pendingImage != null) && !PublishPending())
                {
                    e.Cancel = true;
                    exitRequested = false;
                    OpenFromTray();
                    return;
                }
            }
            closing = true;
            clock.Stop();
            if (recorder != null)
            {
                try { elapsed.Stop(); StageVideo(recorder.Stop()); PublishPending(); }
                catch (Exception ex) { Debug.WriteLine(ex); }
                finally { recorder.Dispose(); recorder = null; captureGuide.SetRecording(null); }
            }
            try { VideoClipboard.FlushOwned(); }
            catch (ExternalException ex)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    closing = false;
                    exitRequested = false;
                    e.Cancel = true;
                    OpenFromTray();
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
                captureGuide.Dispose();
                if (pendingAttachmentPath != null) VideoAttachmentStore.RemoveUnpublished(pendingAttachmentPath);
                if (recordingPanel != null) recordingPanel.Dispose();
                if (tray != null) tray.Dispose();
                if (trayMenu != null) trayMenu.Dispose();
                if (pendingImage != null) pendingImage.Dispose();
                if (appIcon != null) appIcon.Dispose();
                if (cameraSticker != null) cameraSticker.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
