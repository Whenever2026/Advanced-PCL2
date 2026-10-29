using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PclBinaryPatchLab
{
    /// <summary>
    /// Three-step, single-window wizard UI. Patching logic remains in PclPatcher.cs.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly StepProgressControl _progress;
        private readonly Panel _contentHost;
        private readonly Label _footerLabel;
        private readonly Image _brandImage;

        private string _inputPath;
        private string _outputPath;
        private bool _isBusy;
        private int _step; // 0 = source, 1 = output, 2 = complete

        private static class Palette
        {
            public static readonly Color Window = Color.FromArgb(31, 32, 34);
            public static readonly Color Surface = Color.FromArgb(42, 43, 46);
            public static readonly Color SurfaceRaised = Color.FromArgb(49, 50, 53);
            public static readonly Color Input = Color.FromArgb(35, 36, 39);
            public static readonly Color Accent = Color.FromArgb(0, 120, 212); // Windows default blue
            public static readonly Color AccentHover = Color.FromArgb(26, 138, 226);
            public static readonly Color AccentPressed = Color.FromArgb(0, 92, 164);
            public static readonly Color Text = Color.FromArgb(245, 245, 245);
            public static readonly Color Muted = Color.FromArgb(174, 177, 181);
            public static readonly Color Subtle = Color.FromArgb(126, 130, 135);
            public static readonly Color Track = Color.FromArgb(17, 18, 20);
            public static readonly Color Border = Color.FromArgb(64, 66, 70);
            public static readonly Color Success = Color.FromArgb(106, 207, 145);
            public static readonly Color Error = Color.FromArgb(255, 135, 135);
        }

        public MainForm()
        {
            Text = "PCL Binary Patch Lab";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 610);
            ClientSize = new Size(920, 700);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Palette.Window;
            _brandImage = LoadEmbeddedImage("PclBinaryPatchLab.Resources.pcl-logo.png");
            Icon = LoadEmbeddedIcon("PclBinaryPatchLab.Resources.pcl-logo.ico");
            ForeColor = Palette.Text;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = true;
            ControlBox = false;
            DoubleBuffered = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Window,
                ColumnCount = 1,
                RowCount = 4,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            Controls.Add(root);

            root.Controls.Add(BuildTitleBar(), 0, 0);

            _progress = new StepProgressControl { Dock = DockStyle.Fill, Margin = new Padding(32, 0, 32, 0) };
            root.Controls.Add(_progress, 0, 1);

            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Window,
                Margin = new Padding(32, 10, 32, 8),
                Padding = Padding.Empty
            };
            root.Controls.Add(_contentHost, 0, 2);

            _footerLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = string.Empty,
                ForeColor = Palette.Subtle,
                BackColor = Palette.Window,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };
            root.Controls.Add(_footerLabel, 0, 3);

            ShowStep(0);
        }

        private static Image LoadEmbeddedImage(string resourceName)
        {
            using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                using (Image source = Image.FromStream(stream))
                    return new Bitmap(source);
            }
        }

        private static Icon LoadEmbeddedIcon(string resourceName)
        {
            using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return SystemIcons.Application;
                using (var source = new Icon(stream))
                    return (Icon)source.Clone();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRoundedCorners();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyRoundedCorners();
        }

        private void ApplyRoundedCorners()
        {
            // Windows 11's native DWM corner preference keeps the edge antialiased.
            if (!IsHandleCreated || Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            try
            {
                const int DwmwaWindowCornerPreference = 33;
                const int DwmwcpRound = 2;
                int preference = DwmwcpRound;
                DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        private Control BuildTitleBar()
        {
            var bar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Window,
                Margin = Padding.Empty
            };

            var title = new Label
            {
                Text = "PCL Binary Patch Lab",
                AutoSize = false,
                Location = new Point(60, 0),
                Size = new Size(420, 44),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Palette.Text,
                BackColor = Palette.Window,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
            title.MouseDown += TitleBarMouseDown;
            bar.MouseDown += TitleBarMouseDown;
            bar.Controls.Add(title);

            if (_brandImage != null)
            {
                var logo = new PictureBox
                {
                    Image = _brandImage,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Palette.Window,
                    Location = new Point(22, 8),
                    Size = new Size(28, 28),
                    TabStop = false
                };
                logo.MouseDown += TitleBarMouseDown;
                bar.Controls.Add(logo);
            }

            var close = new WindowCaptionButton("×", true)
            {
                Size = new Size(44, 44),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(bar.ClientSize.Width - 44, 0)
            };
            close.Click += delegate { Close(); };
            bar.Controls.Add(close);

            var minimize = new WindowCaptionButton("−", false)
            {
                Size = new Size(44, 44),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(bar.ClientSize.Width - 88, 0)
            };
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            bar.Controls.Add(minimize);

            bar.Resize += delegate
            {
                close.Location = new Point(bar.ClientSize.Width - close.Width, 0);
                minimize.Location = new Point(bar.ClientSize.Width - close.Width - minimize.Width, 0);
            };
            return bar;
        }

        private void TitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void ShowStep(int step)
        {
            _step = step;
            _progress.CompletedSteps = step == 2 ? 3 : step;
            _progress.Invalidate();
            _contentHost.SuspendLayout();
            _contentHost.Controls.Clear();

            Control page;
            if (step == 0) page = BuildSourcePage();
            else if (step == 1) page = BuildOutputPage();
            else page = BuildCompletePage();

            page.Dock = DockStyle.Fill;
            _contentHost.Controls.Add(page);
            _contentHost.ResumeLayout(true);
        }

        private Control BuildSourcePage()
        {
            var page = CreatePageSurface();
            AddPageTitle(page, "选择 PCL 程序", "选择需要处理的 PCL 可执行文件（.exe）。");

            var fieldLabel = CreateLabel("PCL 程序路径", 10F, FontStyle.Bold, Palette.Text);
            fieldLabel.SetBounds(30, 118, 250, 24);
            page.Controls.Add(fieldLabel);

            var pathBox = CreatePathBox(_inputPath ?? string.Empty);
            pathBox.SetBounds(30, 150, 520, 44);
            pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(pathBox);

            var browse = CreateButton("选择文件…", false, 142, 44);
            browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browse.Location = new Point(page.ClientSize.Width - 30 - browse.Width, 150);
            browse.Click += BrowseSourceClick;
            page.Controls.Add(browse);
            page.Resize += delegate
            {
                browse.Left = page.ClientSize.Width - 30 - browse.Width;
                pathBox.Width = Math.Max(180, browse.Left - pathBox.Left - 12);
            };

            var note = CreateLabel("原始文件不会被覆盖。处理结果将保存为一个新的 EXE 文件。", 9F, FontStyle.Regular, Palette.Muted);
            note.SetBounds(30, 211, Math.Max(200, page.ClientSize.Width - 60), 24);
            note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(note);

            var error = CreateLabel(string.Empty, 9F, FontStyle.Regular, Palette.Error);
            error.SetBounds(30, 250, page.ClientSize.Width - 60, 28);
            error.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(error);

            var next = CreateButton("继续", true, 142, 44);
            next.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            next.Location = new Point(page.ClientSize.Width - 30 - next.Width, page.ClientSize.Height - 30 - next.Height);
            next.Enabled = !string.IsNullOrWhiteSpace(_inputPath) && File.Exists(_inputPath);
            next.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(_inputPath) || !File.Exists(_inputPath))
                {
                    error.Text = "请选择一个存在的 EXE 文件。";
                    return;
                }
                ShowStep(1);
            };
            page.Controls.Add(next);
            page.Resize += delegate { next.Location = new Point(page.ClientSize.Width - 30 - next.Width, page.ClientSize.Height - 30 - next.Height); };
            return page;
        }

        private Control BuildOutputPage()
        {
            var page = CreatePageSurface();
            AddPageTitle(page, "选择输出位置", "为处理后的程序选择一个新的保存路径。");

            var sourceLabel = CreateLabel("源文件", 9F, FontStyle.Bold, Palette.Muted);
            sourceLabel.SetBounds(30, 116, 250, 22);
            page.Controls.Add(sourceLabel);
            var sourcePath = CreateLabel(_inputPath ?? string.Empty, 9.5F, FontStyle.Regular, Palette.Text);
            sourcePath.SetBounds(30, 140, page.ClientSize.Width - 60, 28);
            sourcePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            sourcePath.AutoEllipsis = true;
            page.Controls.Add(sourcePath);

            var outputLabel = CreateLabel("输出文件", 10F, FontStyle.Bold, Palette.Text);
            outputLabel.SetBounds(30, 192, 250, 24);
            page.Controls.Add(outputLabel);

            var outputBox = CreatePathBox(_outputPath ?? SuggestedOutputPath());
            outputBox.SetBounds(30, 224, 520, 44);
            outputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(outputBox);

            var browse = CreateButton("选择位置…", false, 142, 44);
            browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browse.Location = new Point(page.ClientSize.Width - 30 - browse.Width, 224);
            browse.Click += delegate
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = "选择处理后 PCL 的保存位置";
                    dialog.Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                    dialog.DefaultExt = "exe";
                    dialog.AddExtension = true;
                    dialog.FileName = Path.GetFileName(outputBox.Text);
                    dialog.InitialDirectory = Directory.Exists(Path.GetDirectoryName(outputBox.Text))
                        ? Path.GetDirectoryName(outputBox.Text)
                        : Path.GetDirectoryName(_inputPath);
                    dialog.OverwritePrompt = false;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        outputBox.Text = dialog.FileName;
                        _outputPath = dialog.FileName;
                    }
                }
            };
            page.Controls.Add(browse);
            page.Resize += delegate
            {
                browse.Left = page.ClientSize.Width - 30 - browse.Width;
                outputBox.Width = Math.Max(180, browse.Left - outputBox.Left - 12);
            };

            var error = CreateLabel(string.Empty, 9F, FontStyle.Regular, Palette.Error);
            error.SetBounds(30, 286, page.ClientSize.Width - 60, 36);
            error.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(error);

            var back = CreateButton("返回", false, 112, 44);
            back.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            back.Location = new Point(30, page.ClientSize.Height - 30 - back.Height);
            back.Click += delegate { _outputPath = outputBox.Text; ShowStep(0); };
            page.Controls.Add(back);

            var create = CreateButton("生成副本", true, 160, 44);
            create.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            create.Location = new Point(page.ClientSize.Width - 30 - create.Width, page.ClientSize.Height - 30 - create.Height);
            create.Click += async delegate
            {
                string output = outputBox.Text == null ? string.Empty : outputBox.Text.Trim();
                string validationError = ValidateOutputPath(_inputPath, output);
                if (validationError != null)
                {
                    error.Text = validationError;
                    return;
                }

                _outputPath = Path.GetFullPath(output);
                _isBusy = true;
                create.Enabled = false;
                browse.Enabled = false;
                back.Enabled = false;
                outputBox.Enabled = false;
                error.ForeColor = Palette.Muted;
                error.Text = "正在分析程序结构并生成副本…";
                _progress.CompletedSteps = 2;
                _progress.Invalidate();

                try
                {
                    string input = _inputPath;
                    string destination = _outputPath;
                    await Task.Run(() => PclPatcher.CreatePatchedCopy(input, destination, delegate { }));
                    _isBusy = false;
                    ShowStep(2);
                }
                catch (Exception ex)
                {
                    _isBusy = false;
                    create.Enabled = true;
                    browse.Enabled = true;
                    back.Enabled = true;
                    outputBox.Enabled = true;
                    error.ForeColor = Palette.Error;
                    error.Text = "处理失败：" + ex.Message;
                    _progress.CompletedSteps = 1;
                    _progress.Invalidate();
                }
            };
            page.Controls.Add(create);
            page.Resize += delegate
            {
                back.Location = new Point(30, page.ClientSize.Height - 30 - back.Height);
                create.Location = new Point(page.ClientSize.Width - 30 - create.Width, page.ClientSize.Height - 30 - create.Height);
                error.Width = page.ClientSize.Width - 60;
            };
            return page;
        }

        private Control BuildCompletePage()
        {
            var page = CreatePageSurface();
            var content = new Panel
            {
                BackColor = Palette.Surface,
                Size = new Size(560, 330),
                Anchor = AnchorStyles.None
            };
            page.Controls.Add(content);
            page.Resize += delegate
            {
                content.Location = new Point((page.ClientSize.Width - content.Width) / 2, (page.ClientSize.Height - content.Height) / 2);
            };

            var successMark = new SuccessMark { Location = new Point((content.Width - 76) / 2, 30), Size = new Size(76, 76) };
            content.Controls.Add(successMark);
            var title = CreateLabel("处理完成", 21F, FontStyle.Bold, Palette.Text);
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.SetBounds(20, 124, content.Width - 40, 38);
            content.Controls.Add(title);
            var description = CreateLabel("已生成新的 PCL 程序副本。原始文件保持不变。", 9.5F, FontStyle.Regular, Palette.Muted);
            description.TextAlign = ContentAlignment.MiddleCenter;
            description.SetBounds(24, 165, content.Width - 48, 28);
            content.Controls.Add(description);

            var outputLabel = CreateLabel("输出文件", 9F, FontStyle.Bold, Palette.Muted);
            outputLabel.SetBounds(28, 210, content.Width - 56, 20);
            content.Controls.Add(outputLabel);
            var outputValue = CreateLabel(_outputPath ?? string.Empty, 9F, FontStyle.Regular, Palette.Text);
            outputValue.SetBounds(28, 232, content.Width - 56, 32);
            outputValue.AutoEllipsis = true;
            content.Controls.Add(outputValue);

            var openFolder = CreateButton("打开所在文件夹", false, 176, 42);
            openFolder.Location = new Point(75, 278);
            openFolder.Click += delegate
            {
                try
                {
                    string folder = Path.GetDirectoryName(_outputPath);
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _outputPath + "\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法打开输出文件夹：" + ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            content.Controls.Add(openFolder);

            var done = CreateButton("完成", true, 120, 42);
            done.Location = new Point(270, 278);
            done.Click += delegate { Close(); };
            content.Controls.Add(done);
            content.Location = new Point((page.ClientSize.Width - content.Width) / 2, (page.ClientSize.Height - content.Height) / 2);
            return page;
        }

        private static Panel CreatePageSurface()
        {
            return new Panel
            {
                BackColor = Palette.Surface,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BorderStyle = BorderStyle.None
            };
        }

        private void AddPageTitle(Control page, string titleText, string subtitleText)
        {
            var title = CreateLabel(titleText, 20F, FontStyle.Bold, Palette.Text);
            title.SetBounds(30, 28, page.ClientSize.Width - 60, 38);
            title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(title);

            var subtitle = CreateLabel(subtitleText, 9.5F, FontStyle.Regular, Palette.Muted);
            subtitle.SetBounds(31, 70, page.ClientSize.Width - 62, 24);
            subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(subtitle);
        }

        private static Label CreateLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Microsoft YaHei UI", Math.Max(10F, size), style, GraphicsUnit.Point),
                ForeColor = color,
                BackColor = Palette.Surface,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
        }

        private static TextBox CreatePathBox(string text)
        {
            return new TextBox
            {
                Text = text,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Palette.Input,
                ForeColor = Palette.Text,
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point),
                Dock = DockStyle.None,
                ShortcutsEnabled = true,
                TabStop = true
            };
        }

        private static FluentButton CreateButton(string text, bool primary, int width, int height)
        {
            return new FluentButton
            {
                Text = text,
                IsPrimary = primary,
                Size = new Size(width, height),
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = Padding.Empty
            };
        }

        private void BrowseSourceClick(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 PCL 程序本体";
                dialog.Filter = "Windows 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (!string.IsNullOrEmpty(_inputPath) && File.Exists(_inputPath))
                    dialog.InitialDirectory = Path.GetDirectoryName(_inputPath);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _inputPath = Path.GetFullPath(dialog.FileName);
                    _outputPath = null;
                    ShowStep(0);
                }
            }
        }

        private static string SuggestedOutputPath()
        {
            return string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
                ? string.Empty
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PCL.patched.exe");
        }

        private static string ValidateOutputPath(string input, string output)
        {
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                return "源文件不存在，请返回第一步重新选择。";
            if (string.IsNullOrWhiteSpace(output))
                return "请选择输出文件路径。";

            string fullOutput;
            try { fullOutput = Path.GetFullPath(output); }
            catch (Exception) { return "输出路径无效，请重新选择。"; }

            if (!string.Equals(Path.GetExtension(fullOutput), ".exe", StringComparison.OrdinalIgnoreCase))
                return "输出文件扩展名必须为 .exe。";
            if (string.Equals(Path.GetFullPath(input), fullOutput, StringComparison.OrdinalIgnoreCase))
                return "输出路径不能与原始 PCL 文件相同。";
            if (File.Exists(fullOutput))
                return "该文件已存在。为避免覆盖，请选择新的文件名。";

            string directory = Path.GetDirectoryName(fullOutput);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return "输出文件夹不存在，请选择一个有效的文件夹。";
            return null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isBusy)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }

    internal sealed class WindowCaptionButton : Control
    {
        private readonly bool _isClose;
        private bool _hovered;
        private bool _pressed;

        public WindowCaptionButton(string text, bool isClose)
        {
            Text = text;
            _isClose = isClose;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(31, 32, 34);
            ForeColor = Color.FromArgb(245, 245, 245);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = _pressed
                ? (_isClose ? Color.FromArgb(196, 43, 28) : Color.FromArgb(58, 60, 64))
                : (_hovered ? (_isClose ? Color.FromArgb(196, 43, 28) : Color.FromArgb(48, 50, 54)) : BackColor);
            using (var brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, ClientRectangle);

            using (var font = new Font("Microsoft YaHei UI", 13F, FontStyle.Regular, GraphicsUnit.Point))
                TextRenderer.DrawText(e.Graphics, Text, font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    internal sealed class StepProgressControl : Control
    {
        private int _completedSteps;
        public int CompletedSteps
        {
            get { return _completedSteps; }
            set { _completedSteps = Math.Max(0, Math.Min(3, value)); Invalidate(); }
        }

        public StepProgressControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(31, 32, 34);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(Color.FromArgb(31, 32, 34)))
                e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int left = 24;
            int right = Math.Max(left + 10, ClientSize.Width - 24);
            int span = right - left;
            int y = Math.Max(20, ClientSize.Height / 2);
            int diameter = 28;
            int radius = diameter / 2;
            int[] centers = { left + span / 6, left + span / 2, left + (span * 5) / 6 };

            using (var trackPen = new Pen(Color.FromArgb(17, 18, 20), 4F))
            using (var completedPen = new Pen(Color.FromArgb(0, 120, 212), 4F))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                completedPen.StartCap = LineCap.Round;
                completedPen.EndCap = LineCap.Round;
                e.Graphics.DrawLine(trackPen, left, y, right, y);
                int filledRight = left + (span * _completedSteps) / 3;
                if (_completedSteps > 0) e.Graphics.DrawLine(completedPen, left, y, filledRight, y);
            }

            for (int i = 0; i < 3; i++)
            {
                Rectangle circle = new Rectangle(centers[i] - radius, y - radius, diameter, diameter);
                using (var brush = new SolidBrush(Color.FromArgb(0, 120, 212)))
                    e.Graphics.FillEllipse(brush, circle);
                using (var pen = new Pen(Color.FromArgb(0, 120, 212), 1F))
                    e.Graphics.DrawEllipse(pen, circle);
                using (var numberFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point))
                    TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), numberFont, circle, Color.Black,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }

    internal sealed class FluentButton : Button
    {
        private bool _hovered;
        private bool _pressed;
        public bool IsPrimary { get; set; }

        public FluentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(42, 43, 46);
            ForeColor = Color.FromArgb(245, 245, 245);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill;
            Color border;
            Color text;
            if (!Enabled)
            {
                fill = IsPrimary ? Color.FromArgb(48, 81, 105) : Color.FromArgb(45, 46, 49);
                border = fill;
                text = Color.FromArgb(135, 138, 142);
            }
            else if (IsPrimary)
            {
                fill = _pressed ? Color.FromArgb(0, 92, 164) : (_hovered ? Color.FromArgb(26, 138, 226) : Color.FromArgb(0, 120, 212));
                border = fill;
                text = Color.Black;
            }
            else
            {
                fill = _pressed ? Color.FromArgb(59, 60, 64) : (_hovered ? Color.FromArgb(55, 56, 60) : Color.FromArgb(42, 43, 46));
                border = Color.FromArgb(76, 78, 83);
                text = Color.FromArgb(245, 245, 245);
            }

            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = CreateButtonPath(bounds, 5))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border, 1F))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }

            TextRenderer.DrawText(e.Graphics, Text ?? string.Empty, Font, Rectangle.Inflate(bounds, -10, 0), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
            {
                Rectangle focus = Rectangle.Inflate(bounds, -5, -5);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, text, fill);
            }
        }

        private static GraphicsPath CreateButtonPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (rect.Width < d || rect.Height < d) { path.AddRectangle(rect); return path; }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    internal sealed class SuccessMark : Control
    {
        public SuccessMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(42, 43, 46);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(4, 4, Width - 9, Height - 9);
            using (var brush = new SolidBrush(Color.FromArgb(0, 120, 212))) e.Graphics.FillEllipse(brush, bounds);
            using (var pen = new Pen(Color.Black, 5F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                e.Graphics.DrawLines(pen, new[] { new Point(23, 40), new Point(33, 50), new Point(53, 27) });
            }
        }
    }
}
