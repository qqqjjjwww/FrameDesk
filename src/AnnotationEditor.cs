using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenStudio
{
    public static partial class AnnotationEditor
    {
        public static Bitmap Edit(Bitmap original, IWin32Window owner)
        {
            return Edit(original, owner, false);
        }

        internal static Bitmap Edit(Bitmap original, IWin32Window owner, bool longImage)
        {
            return Edit(original, owner, longImage, new Rectangle(Cursor.Position, new Size(1, 1)));
        }

        internal static Bitmap Edit(Bitmap original, IWin32Window owner, bool longImage, Rectangle captureBounds)
        {
            if (original == null) throw new ArgumentNullException("original");
            using (var dialog = new EditorWindow(original, longImage, Screen.FromRectangle(captureBounds).WorkingArea))
            {
                DialogResult answer = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                return answer == DialogResult.OK ? dialog.Canvas.RenderOriginalSize() : null;
            }
        }
        private static readonly Color MarkRed = Color.FromArgb(224, 33, 47);
        private enum Tool { Select, Box, Arrow, Text }

        private sealed class EditorWindow : Form
        {
            internal readonly EditorCanvas Canvas;
            private readonly Button selectButton, boxButton, arrowButton, textButton, undoButton;

            internal EditorWindow(Bitmap original, bool longImage, Rectangle available)
            {
                Text = "截图标注";
                Font = new Font("Microsoft YaHei UI", 9F);
                BackColor = Art.Paper;
                ForeColor = Art.Ink;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = true;
                TopMost = true;
                KeyPreview = true;
                AutoScaleMode = AutoScaleMode.None;
                // Create the window on the capture monitor before measuring its DPI.
                Location = new Point(available.Left + available.Width / 2, available.Top + available.Height / 2);
                float dpi = Math.Max(1F, GetDpiForWindow(Handle) / 96F);
                int preferredWidth = (int)Math.Round(1030 * dpi), preferredHeight = (int)Math.Round(700 * dpi);
                Size = new Size(Math.Min(preferredWidth, available.Width), Math.Min(preferredHeight, available.Height));
                MinimumSize = new Size(Math.Min((int)(552 * dpi), available.Width), Math.Min((int)(340 * dpi), available.Height));
                Location = new Point(available.X + (available.Width - Width) / 2, available.Y + (available.Height - Height) / 2);

                Canvas = new EditorCanvas(original, longImage) { Dock = DockStyle.Fill };
                var toolbar = new TableLayoutPanel {
                    Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                    Padding = new Padding((int)(10 * dpi), (int)(7 * dpi), (int)(10 * dpi), (int)(5 * dpi)),
                    Margin = Padding.Empty, BackColor = Art.Paper
                };
                toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var tools = new FlowLayoutPanel { Dock = DockStyle.Fill,
                    WrapContents = true, Margin = Padding.Empty, AccessibleName = "标注工具" };
                var commands = new FlowLayoutPanel { Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
                selectButton = CreateButton("移动/调整", "move", dpi, delegate { SelectMode(Tool.Select); });
                selectButton.AccessibleDescription = "拖动标注移动，拖动控制点调整，双击文字修改内容";
                boxButton = CreateButton("方框", "box", dpi, delegate { SelectMode(Tool.Box); });
                arrowButton = CreateButton("箭头", "arrow", dpi, delegate { SelectMode(Tool.Arrow); });
                textButton = CreateButton("文字", "text", dpi, delegate { SelectMode(Tool.Text); });
                undoButton = CreateButton("撤销", "undo", dpi, delegate { Canvas.Undo(); });
                Button copy = CreateButton("完成并复制  ↵", "check", dpi, delegate { Confirm(); });
                copy.AccessibleName = "完成并复制";
                copy.AccessibleDescription = "按 Enter 将标注后的截图复制到剪贴板";
                copy.BackColor = Art.Berry;
                copy.ForeColor = Color.White;
                copy.FlatAppearance.BorderColor = Art.Berry;
                copy.Margin = new Padding(0, 0, 0, (int)(2 * dpi));
                Button cancel = CreateButton("取消", null, dpi, delegate { DialogResult = DialogResult.Cancel; });
                tools.Controls.AddRange(new Control[] { selectButton, boxButton, arrowButton, textButton, undoButton });
                commands.Controls.AddRange(new Control[] { cancel, copy });
                toolbar.Controls.Add(tools, 0, 0);
                toolbar.Controls.Add(commands, 1, 0);
                var status = new Label {
                    AutoSize = true, Padding = new Padding((int)(12 * dpi), (int)(6 * dpi), (int)(8 * dpi), (int)(6 * dpi)),
                    ForeColor = Art.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
                };
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var toolRow = new RowStyle(SizeType.Absolute);
                layout.RowStyles.Add(toolRow);
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(toolbar, 0, 0);
                layout.Controls.Add(Canvas, 0, 1);
                layout.Controls.Add(status, 0, 2);
                Controls.Add(layout);
                // Measure the wrapped tools at their actual width. Measuring this row at zero
                // width reserves a full row for each tool even after the window is widened.
                Action fitTools = delegate {
                    Size commandSize = commands.GetPreferredSize(Size.Empty);
                    int toolsWidth = Math.Max(1, layout.ClientSize.Width - toolbar.Padding.Horizontal - commandSize.Width);
                    int toolsHeight = tools.GetPreferredSize(new Size(toolsWidth, 0)).Height;
                    toolRow.Height = toolbar.Padding.Vertical + Math.Max(toolsHeight, commandSize.Height);
                };
                layout.SizeChanged += delegate { fitTools(); };
                fitTools();
                Canvas.StateChanged += delegate {
                    undoButton.Enabled = Canvas.CanUndo;
                    SetActive(selectButton, Canvas.ActiveTool == Tool.Select);
                    SetActive(boxButton, Canvas.ActiveTool == Tool.Box);
                    SetActive(arrowButton, Canvas.ActiveTool == Tool.Arrow);
                    SetActive(textButton, Canvas.ActiveTool == Tool.Text);
                    string hint;
                    if (Canvas.EditingText) hint = "文字笔画随滚轮向上变粗、向下变细　·　Shift + Enter 换行　·　Esc 取消";
                    else if (Canvas.SelectionKind == ShapeKind.Box) hint = "拖动方框移动，拖动四角调整大小";
                    else if (Canvas.SelectionKind == ShapeKind.Arrow) hint = "拖动箭身移动，拖动两端调整方向和长度";
                    else if (Canvas.SelectionKind == ShapeKind.Text) hint = "拖动文字移动，双击修改内容";
                    else switch (Canvas.ActiveTool)
                    {
                        case Tool.Select: hint = "拖动标注移动，拖动控制点调整，双击文字修改"; break;
                        case Tool.Arrow: hint = "从起点拖到箭头指向的位置"; break;
                        case Tool.Text: hint = "单击输入文字，Shift + Enter 换行"; break;
                        default: hint = "拖动绘制方框"; break;
                    }
                    string wheel = Canvas.EditingText ? "" : "指向标注，滚轮向上加粗、向下变细　·　";
                    status.Text = wheel + hint + (longImage ? "　·　空白处滚轮浏览" : "") +
                        "　·　Enter 复制　·　" + original.Width + " × " + original.Height + " 像素";
                };
                SelectMode(Tool.Box);
                Shown += delegate {
                    BringToFront();
                    Activate();
                    Canvas.Focus();
                    // Only the initial reveal needs priority over other applications.
                    Deactivate += delegate { TopMost = false; };
                };
            }

            [DllImport("user32.dll")]
            private static extern uint GetDpiForWindow(IntPtr window);

            private static Button CreateButton(string label, string glyph, float dpi, EventHandler click)
            {
                var b = new Button {
                    Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                    BackColor = Color.White, ForeColor = Art.Ink, Cursor = Cursors.Hand,
                    Padding = new Padding((int)(6 * dpi), (int)(3 * dpi), (int)(6 * dpi), (int)(3 * dpi)),
                    MinimumSize = new Size(0, (int)(32 * dpi)), Margin = new Padding(0, 0, (int)(5 * dpi), (int)(2 * dpi))
                };
                if (glyph != null)
                {
                    int size = (int)Math.Round(19 * dpi);
                    var icon = new Bitmap(size, size);
                    Color ink = glyph == "check" ? Color.White :
                                (glyph == "box" || glyph == "arrow" || glyph == "text" ? MarkRed : Art.Muted);
                    using (Graphics g = Graphics.FromImage(icon))
                    {
                        Art.Init(g, 1);
                        Art.Icon(g, glyph, 0, 0, size, ink);
                    }
                    b.Image = icon;
                    b.TextImageRelation = TextImageRelation.ImageBeforeText;
                    b.Disposed += delegate { icon.Dispose(); };
                }
                b.FlatAppearance.BorderColor = Art.Line;
                b.Click += click;
                return b;
            }

            private static void SetActive(Button b, bool active)
            {
                b.BackColor = active ? Art.Lilac : Color.White;
                b.ForeColor = active ? Art.Violet : Art.Ink;
                b.FlatAppearance.BorderColor = active ? Art.Violet : Art.Line;
            }

            private void SelectMode(Tool mode)
            {
                Canvas.SwitchTool(mode);
                Canvas.Focus();
            }

            private void Confirm()
            {
                Canvas.CommitText();
                DialogResult = DialogResult.OK;
            }

            protected override bool ProcessCmdKey(ref Message message, Keys keys)
            {
                if (Canvas != null && !Canvas.Composing)
                {
                    if (keys == Keys.Escape)
                    {
                        if (!Canvas.CancelInteraction()) DialogResult = DialogResult.Cancel;
                        return true;
                    }
                    if (keys == (Keys.Control | Keys.Enter) || keys == Keys.Enter ||
                        (keys == (Keys.Control | Keys.C) && !Canvas.EditingText))
                    {
                        Confirm(); return true;
                    }
                    if (keys == (Keys.Control | Keys.Z) && !Canvas.EditingText)
                    {
                        Canvas.Undo(); return true;
                    }
                    if (keys == Keys.Delete && !Canvas.EditingText)
                    {
                        Canvas.DeleteSelected(); return true;
                    }
                }
                return base.ProcessCmdKey(ref message, keys);
            }
        }

    }
}
