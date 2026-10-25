using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GbOs86
{
    internal sealed class MainForm : Form
    {
        private sealed class DiskChoice
        {
            public DiskInfo Disk;
            public override string ToString()
            {
                ulong free = Disk.LargestFree == null ? 0 : Disk.LargestFree.Length;
                return "Disk " + Disk.Number + ": " + Disk.Model + "   (" + Fmt.Bytes(Disk.Size) +
                       ", " + Disk.PartitionStyle + ")   -   " + Fmt.Bytes(free) + " free";
            }
        }

        private Panel _header;
        private Panel _content;
        private Panel _logPanel;
        private Panel _progressPanel;
        private GroupBox _imageGroup;
        private GroupBox _diskGroup;
        private GroupBox _installGroup;
        private RadioButton _buildRadio;
        private RadioButton _existingRadio;
        private TextBox _sourceBox;
        private TextBox _imageBox;
        private NumericUpDown _dataSize;
        private Button _browseSourceButton;
        private Button _browseImageButton;
        private Button _buildButton;
        private Label _imageStatus;
        private ComboBox _diskCombo;
        private Button _refreshButton;
        private Label _diskDetails;
        private Label _envLabel;
        private TextBox _preflightBox;
        private Button _installButton;
        private Button _uninstallButton;
        private Button _openLogsButton;
        private ProgressBar _progress;
        private Label _statusLabel;
        private Button _cancelButton;
        private TextBox _logBox;

        private List<DiskInfo> _disks = new List<DiskInfo>();
        private string _imagePath = "";
        private PreflightResult _preflight;
        private InstallState _state;
        private volatile bool _cancelRequested;
        private bool _busy;
        private bool _fillingDisks;
        private int _preflightGen;

        public MainForm()
        {
            Text = "gbOS86 Installer";
            ClientSize = new Size(1020, 900);
            MinimumSize = new Size(900, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BackColor = Color.FromArgb(243, 244, 246);
            DoubleBuffered = true;

            BuildUi();
            Log.Line += OnLogLine;
            Shown += delegate { Initialize(); };
            FormClosing += OnFormClosing;
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            Controls.Add(root);

            _header = new Panel();
            _header.Dock = DockStyle.Fill;
            _header.BackColor = Color.FromArgb(31, 41, 55);
            root.Controls.Add(_header, 0, 0);

            Label title = new Label();
            title.Text = "gbOS86 Installer";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI", 17F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(18, 8);
            _header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Dual-boot installer for Windows PCs  -  builds and flashes the persistent gbOS86 loopback image";
            subtitle.ForeColor = Color.FromArgb(203, 213, 225);
            subtitle.Font = new Font("Segoe UI", 9.5F);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(20, 43);
            _header.Controls.Add(subtitle);

            _content = new Panel();
            _content.Dock = DockStyle.Fill;
            _content.AutoScroll = true;
            _content.Padding = new Padding(12, 6, 12, 6);
            root.Controls.Add(_content, 0, 1);

            _imageGroup = MakeGroup("1.  gbOS86 image", 0, 190);
            _content.Controls.Add(_imageGroup);

            _buildRadio = new RadioButton();
            _buildRadio.Text = "Build from gbos-x86 source";
            _buildRadio.Location = new Point(12, 24);
            _buildRadio.Size = new Size(240, 22);
            _buildRadio.Checked = true;
            _buildRadio.CheckedChanged += delegate { UpdateImageMode(); };
            _imageGroup.Controls.Add(_buildRadio);

            _existingRadio = new RadioButton();
            _existingRadio.Text = "Use an existing looper image";
            _existingRadio.Location = new Point(262, 24);
            _existingRadio.Size = new Size(240, 22);
            _existingRadio.CheckedChanged += delegate { UpdateImageMode(); };
            _imageGroup.Controls.Add(_existingRadio);

            Label sourceLabel = MakeLabel("gbos-x86 folder", 12, 59);
            _imageGroup.Controls.Add(sourceLabel);

            _sourceBox = new TextBox();
            _sourceBox.Location = new Point(126, 56);
            _sourceBox.Size = new Size(708, 24);
            _sourceBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _imageGroup.Controls.Add(_sourceBox);

            _browseSourceButton = new Button();
            _browseSourceButton.Text = "Browse...";
            _browseSourceButton.Location = new Point(848, 54);
            _browseSourceButton.Size = new Size(120, 26);
            _browseSourceButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseSourceButton.Click += delegate { BrowseSource(); };
            _imageGroup.Controls.Add(_browseSourceButton);

            Label dataLabel = MakeLabel("Data partition", 12, 92);
            _imageGroup.Controls.Add(dataLabel);

            _dataSize = new NumericUpDown();
            _dataSize.Location = new Point(126, 89);
            _dataSize.Size = new Size(64, 24);
            _dataSize.Minimum = 4;
            _dataSize.Maximum = 512;
            _dataSize.Value = 64;
            _dataSize.ThousandsSeparator = false;
            _imageGroup.Controls.Add(_dataSize);

            Label dataHint = MakeLabel("GB, sparse (grows as apps are installed)", 198, 92);
            dataHint.ForeColor = Color.FromArgb(75, 85, 99);
            _imageGroup.Controls.Add(dataHint);

            _buildButton = new Button();
            _buildButton.Text = "Build image";
            _buildButton.Location = new Point(848, 87);
            _buildButton.Size = new Size(120, 28);
            _buildButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _buildButton.Click += delegate { StartBuild(); };
            _imageGroup.Controls.Add(_buildButton);

            Label imageLabel = MakeLabel("Looper image", 12, 125);
            _imageGroup.Controls.Add(imageLabel);

            _imageBox = new TextBox();
            _imageBox.Location = new Point(126, 122);
            _imageBox.Size = new Size(708, 24);
            _imageBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _imageBox.Leave += delegate { SetImageFromBox(); };
            _imageBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { SetImageFromBox(); e.SuppressKeyPress = true; }
            };
            _imageGroup.Controls.Add(_imageBox);

            _browseImageButton = new Button();
            _browseImageButton.Text = "Browse...";
            _browseImageButton.Location = new Point(848, 120);
            _browseImageButton.Size = new Size(120, 26);
            _browseImageButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseImageButton.Click += delegate { BrowseImage(); };
            _imageGroup.Controls.Add(_browseImageButton);

            _imageStatus = new Label();
            _imageStatus.Location = new Point(12, 153);
            _imageStatus.Size = new Size(956, 20);
            _imageStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _imageStatus.ForeColor = Color.FromArgb(75, 85, 99);
            _imageStatus.Text = "No image selected.";
            _imageGroup.Controls.Add(_imageStatus);

            _diskGroup = MakeGroup("2.  Target disk", 198, 158);
            _content.Controls.Add(_diskGroup);

            Label diskLabel = MakeLabel("Disk", 12, 28);
            _diskGroup.Controls.Add(diskLabel);

            _diskCombo = new ComboBox();
            _diskCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _diskCombo.Location = new Point(58, 25);
            _diskCombo.Size = new Size(776, 24);
            _diskCombo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _diskCombo.SelectedIndexChanged += delegate
            {
                if (_fillingDisks) return;
                RefreshDiskDetails();
                RefreshPreflight();
            };
            _diskGroup.Controls.Add(_diskCombo);

            _refreshButton = new Button();
            _refreshButton.Text = "Refresh";
            _refreshButton.Location = new Point(848, 23);
            _refreshButton.Size = new Size(120, 26);
            _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshButton.Click += delegate { RefreshDisks(); };
            _diskGroup.Controls.Add(_refreshButton);

            _diskDetails = new Label();
            _diskDetails.Location = new Point(12, 56);
            _diskDetails.Size = new Size(956, 90);
            _diskDetails.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _diskDetails.ForeColor = Color.FromArgb(55, 65, 81);
            _diskDetails.Text = "Reading disks...";
            _diskGroup.Controls.Add(_diskDetails);

            _installGroup = MakeGroup("3.  Install", 364, 190);
            _content.Controls.Add(_installGroup);
            _content.AutoScrollMinSize = new Size(0, 560);

            _envLabel = new Label();
            _envLabel.Location = new Point(12, 22);
            _envLabel.Size = new Size(956, 34);
            _envLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _envLabel.ForeColor = Color.FromArgb(55, 65, 81);
            _installGroup.Controls.Add(_envLabel);

            _preflightBox = new TextBox();
            _preflightBox.Location = new Point(12, 58);
            _preflightBox.Size = new Size(956, 72);
            _preflightBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _preflightBox.Multiline = true;
            _preflightBox.ReadOnly = true;
            _preflightBox.ScrollBars = ScrollBars.Vertical;
            _preflightBox.BackColor = Color.FromArgb(249, 250, 251);
            _preflightBox.Text = "Select a target disk and an image.";
            _installGroup.Controls.Add(_preflightBox);

            _openLogsButton = new Button();
            _openLogsButton.Text = "Open logs";
            _openLogsButton.Location = new Point(12, 144);
            _openLogsButton.Size = new Size(110, 30);
            _openLogsButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _openLogsButton.Click += delegate { OpenLogs(); };
            _installGroup.Controls.Add(_openLogsButton);

            _installButton = new Button();
            _installButton.Text = "Install gbOS86";
            _installButton.Location = new Point(692, 144);
            _installButton.Size = new Size(140, 30);
            _installButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _installButton.Enabled = false;
            _installButton.Click += delegate { StartInstall(); };
            _installGroup.Controls.Add(_installButton);

            _uninstallButton = new Button();
            _uninstallButton.Text = "Uninstall";
            _uninstallButton.Location = new Point(840, 144);
            _uninstallButton.Size = new Size(128, 30);
            _uninstallButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _uninstallButton.Enabled = false;
            _uninstallButton.Click += delegate { StartUninstall(); };
            _installGroup.Controls.Add(_uninstallButton);

            _logPanel = new Panel();
            _logPanel.Dock = DockStyle.Fill;
            _logPanel.Padding = new Padding(12, 4, 12, 4);
            root.Controls.Add(_logPanel, 0, 2);

            _logBox = new TextBox();
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Both;
            _logBox.WordWrap = false;
            _logBox.Font = new Font("Consolas", 8.25F);
            _logBox.BackColor = Color.FromArgb(17, 24, 39);
            _logBox.ForeColor = Color.FromArgb(209, 213, 219);
            _logBox.BorderStyle = BorderStyle.FixedSingle;
            _logPanel.Controls.Add(_logBox);

            _progressPanel = new Panel();
            _progressPanel.Dock = DockStyle.Fill;
            _progressPanel.Padding = new Padding(12, 4, 12, 4);
            root.Controls.Add(_progressPanel, 0, 3);

            _progress = new ProgressBar();
            _progress.Location = new Point(12, 6);
            _progress.Size = new Size(820, 20);
            _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            _progressPanel.Controls.Add(_progress);

            _cancelButton = new Button();
            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(848, 3);
            _cancelButton.Size = new Size(120, 26);
            _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cancelButton.Enabled = false;
            _cancelButton.Click += delegate { RequestCancel(); };
            _progressPanel.Controls.Add(_cancelButton);

            _statusLabel = new Label();
            _statusLabel.Location = new Point(12, 32);
            _statusLabel.Size = new Size(956, 20);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusLabel.ForeColor = Color.FromArgb(55, 65, 81);
            _statusLabel.Text = "Ready.";
            _progressPanel.Controls.Add(_statusLabel);
        }

        private static GroupBox MakeGroup(string text, int y, int height)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Location = new Point(0, y);
            g.Size = new Size(980, height);
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            g.Padding = new Padding(8);
            g.Font = new Font("Segoe UI", 9F);
            return g;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            l.Font = new Font("Segoe UI", 9F);
            return l;
        }

        private void Initialize()
        {
            Log.Write("gbOS86 installer " + Application.ProductVersion + " started.");
            Log.Write("Log file: " + Log.FilePath);
            Log.Write("Administrator: " + (Native.IsAdministrator() ? "yes" : "NO"));
            Log.Write("Firmware: " + (Native.IsUefi() ? "UEFI" : "legacy BIOS (unsupported)"));

            _sourceBox.Text = Paths.FindDefaultGbOsSource();
            if (_sourceBox.Text.Length == 0)
                _sourceBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gbos-x86");

            _state = InstallState.Load();
            _uninstallButton.Enabled = _state != null;

            UpdateImageMode();
            RefreshEnvironment();
            RefreshDisks();
        }

        private void RefreshEnvironment()
        {
            bool uefi = Native.IsUefi();
            bool admin = Native.IsAdministrator();
            bool? secureBoot = DiskService.SecureBootEnabled;
            string sb = secureBoot == null ? "unknown" : (secureBoot.Value ? "ON" : "off");
            string docker = DockerService.IsInstalled ? "checking..." : "not installed";
            _envLabel.Text = "Firmware: " + (uefi ? "UEFI" : "Legacy BIOS (gbOS86 requires UEFI)") +
                             "     Secure Boot: " + sb +
                             "     Administrator: " + (admin ? "yes" : "NO") +
                             "     Docker: " + docker;
            _envLabel.ForeColor = (!uefi || !admin || secureBoot == true)
                ? Color.Firebrick : Color.FromArgb(55, 65, 81);

            if (!DockerService.IsInstalled) return;
            Task.Factory.StartNew(delegate
            {
                ProcessResult r = DockerService.CheckDaemon();
                Ui(delegate
                {
                    bool running = r != null && r.Success;
                    _envLabel.Text = "Firmware: " + (uefi ? "UEFI" : "Legacy BIOS (gbOS86 requires UEFI)") +
                                     "     Secure Boot: " + sb +
                                     "     Administrator: " + (admin ? "yes" : "NO") +
                                     "     Docker: " + (running ? "running" : "not running");
                });
            });
        }

        private void RefreshDisks()
        {
            if (_busy) return;
            string wantSerial = "";
            int wantNumber = -1;
            DiskInfo current = SelectedDisk();
            if (current != null) { wantSerial = current.Serial; wantNumber = current.Number; }

            SetStatus("Reading disks...");
            Task.Factory.StartNew(delegate
            {
                try
                {
                    List<DiskInfo> disks = DiskService.LoadDisks();
                    Ui(delegate
                    {
                        _disks = disks;
                        FillDiskCombo(wantSerial, wantNumber);
                        SetStatus("Ready.");
                        RefreshPreflight();
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        SetStatus("Cannot read disks: " + Fmt.OneLine(ex.Message, 120));
                        MessageBox.Show(this, ex.Message, "Cannot read disks",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void FillDiskCombo(string wantSerial, int wantNumber)
        {
            _fillingDisks = true;
            try
            {
                _diskCombo.Items.Clear();
                int select = -1;
                for (int i = 0; i < _disks.Count; i++)
                {
                    DiskChoice c = new DiskChoice();
                    c.Disk = _disks[i];
                    _diskCombo.Items.Add(c);
                    if (select < 0)
                    {
                        if (!string.IsNullOrEmpty(wantSerial) && !string.IsNullOrEmpty(c.Disk.Serial) &&
                            string.Equals(wantSerial, c.Disk.Serial, StringComparison.OrdinalIgnoreCase))
                            select = i;
                        else if (wantNumber >= 0 && c.Disk.Number == wantNumber)
                            select = i;
                    }
                }
                if (_diskCombo.Items.Count > 0)
                    _diskCombo.SelectedIndex = select >= 0 ? select : 0;
            }
            finally { _fillingDisks = false; }
            RefreshDiskDetails();
        }

        private void RefreshDiskDetails()
        {
            DiskInfo disk = SelectedDisk();
            if (disk == null)
            {
                _diskDetails.Text = "No suitable internal disk found.";
                return;
            }

            ulong free = disk.LargestFree == null ? 0 : disk.LargestFree.Length;
            string text = disk.Model + "   serial " + disk.SerialOrUnknown + "   " +
                          Fmt.Bytes(disk.Size) + "   " + disk.PartitionStyle + "   " +
                          (disk.BusType.Length > 0 ? disk.BusType : disk.InterfaceType) + "\r\n" +
                          "Unallocated: " + Fmt.Bytes(free) + " (largest contiguous area)" +
                          (disk.Partitions.Count == 0 ? "   -   disk is empty" : "") + "\r\n";
            if (disk.Partitions.Count > 0)
                text += DiskService.DescribePartitions(disk).Replace("\r\n", "   ").Trim();
            _diskDetails.Text = text;
        }

        private DiskInfo SelectedDisk()
        {
            DiskChoice c = _diskCombo == null ? null : _diskCombo.SelectedItem as DiskChoice;
            return c == null ? null : c.Disk;
        }

        private void SetImageFromBox()
        {
            string path = _imageBox.Text.Trim();
            if (path.Length == 0)
            {
                _imagePath = "";
                _imageStatus.Text = "No image selected.";
                _imageStatus.ForeColor = Color.FromArgb(75, 85, 99);
                RefreshPreflight();
                return;
            }
            _imagePath = path;
            RefreshPreflight();
        }

        private void BrowseSource()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Select the gbos-x86 source folder (the one containing build.sh)";
                d.ShowNewFolderButton = false;
                string current = _sourceBox.Text.Trim();
                if (Directory.Exists(current)) d.SelectedPath = current;
                if (d.ShowDialog(this) == DialogResult.OK)
                    _sourceBox.Text = d.SelectedPath;
            }
        }

        private void BrowseImage()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select a gbOS86 looper image";
                d.Filter = "gbOS86 images (*.img)|*.img|All files (*.*)|*.*";
                d.CheckFileExists = true;
                string current = _imageBox.Text.Trim();
                if (File.Exists(current))
                {
                    d.InitialDirectory = Path.GetDirectoryName(current);
                    d.FileName = Path.GetFileName(current);
                }
                else if (Directory.Exists(Paths.ImagesDir))
                {
                    d.InitialDirectory = Paths.ImagesDir;
                }
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _imagePath = d.FileName;
                    _imageBox.Text = d.FileName;
                    _existingRadio.Checked = true;
                    RefreshPreflight();
                }
            }
        }

        private void RefreshPreflight()
        {
            if (_busy) return;
            int gen = ++_preflightGen;
            string path = _imagePath;
            DiskInfo disk = SelectedDisk();

            if (path.Length == 0 || disk == null)
            {
                ApplyPreflight(null);
                return;
            }

            _installButton.Enabled = false;
            _preflightBox.BackColor = Color.FromArgb(249, 250, 251);
            _preflightBox.Text = "Checking the image and the target disk...";

            Task.Factory.StartNew(delegate
            {
                LooperImage image = null;
                try
                {
                    image = LooperImage.Open(path);
                    // Cache the allocated ranges so the install and later
                    // preflights do not rescan a large non-sparse image.
                    image.SaveExtentMap();
                    DiskService.ReadLabels(disk);
                    PreflightResult p = InstallService.Preflight(disk, image);
                    string imageText = DescribeImage(image);
                    string details = DescribeDisk(disk);
                    Ui(delegate
                    {
                        if (gen != _preflightGen || _busy) return;
                        _imageStatus.Text = imageText;
                        _imageStatus.ForeColor = Color.FromArgb(22, 101, 52);
                        _diskDetails.Text = details;
                        ApplyPreflight(p);
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        if (gen != _preflightGen) return;
                        _imagePath = "";
                        _imageStatus.Text = "Image problem: " + Fmt.OneLine(ex.Message, 180);
                        _imageStatus.ForeColor = Color.Firebrick;
                        ApplyPreflight(null);
                    });
                }
                finally
                {
                    if (image != null) image.Dispose();
                }
            });
        }

        private static string DescribeImage(LooperImage image)
        {
            return "Image OK:  EFI " + Fmt.Bytes(image.Partitions[0].Size) +
                   ",  system " + Fmt.Bytes(image.Partitions[1].Size) +
                   ",  metadata " + Fmt.Bytes(image.Partitions[2].Size) +
                   ",  data " + image.DataGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB" +
                   "   -   " + Fmt.Bytes((ulong)image.AllocatedBytes) + " to write" +
                   (image.Warnings.Count > 0 ? "  (" + image.Warnings.Count + " warning(s))" : "");
        }

        private static string DescribeDisk(DiskInfo disk)
        {
            ulong free = disk.LargestFree == null ? 0 : disk.LargestFree.Length;
            string text = disk.Model + "   serial " + disk.SerialOrUnknown + "   " +
                          Fmt.Bytes(disk.Size) + "   " + disk.PartitionStyle + "   " +
                          (disk.BusType.Length > 0 ? disk.BusType : disk.InterfaceType) + "\r\n" +
                          "Unallocated: " + Fmt.Bytes(free) + " (largest contiguous area)\r\n";
            text += DiskService.DescribePartitions(disk).Trim();
            return text;
        }

        private void ApplyPreflight(PreflightResult p)
        {
            _preflight = p;
            if (p == null)
            {
                _preflightBox.BackColor = Color.FromArgb(249, 250, 251);
                _preflightBox.Text = "Select a target disk and an image.";
                _installButton.Enabled = false;
                return;
            }

            if (!p.Ok)
            {
                _preflightBox.BackColor = Color.FromArgb(254, 226, 226);
                _preflightBox.Text = p.Summary;
                _installButton.Enabled = false;
                return;
            }

            string text = "Ready to install.  Windows and all existing partitions are left alone.\r\n";
            foreach (string w in p.Warnings) text += "WARNING: " + w + "\r\n";
            _preflightBox.BackColor = p.Warnings.Count > 0
                ? Color.FromArgb(254, 249, 195) : Color.FromArgb(220, 252, 231);
            _preflightBox.Text = text.TrimEnd();
            _installButton.Enabled = !_busy;
        }

        private void UpdateImageMode()
        {
            bool build = _buildRadio.Checked;
            _sourceBox.Enabled = build && !_busy;
            _browseSourceButton.Enabled = build && !_busy;
            _dataSize.Enabled = build && !_busy;
            _buildButton.Enabled = build && !_busy;
            _imageBox.Enabled = !build && !_busy;
            _browseImageButton.Enabled = !build && !_busy;
        }

        private void StartBuild()
        {
            if (_busy) return;
            string source = _sourceBox.Text.Trim();
            if (!Paths.IsGbOsSource(source))
            {
                MessageBox.Show(this,
                    "This is not a complete gbos-x86 checkout:\n" + source +
                    "\n\nExpected build.sh, usb/make_super.py and usb/out/BOOTX64.EFI.",
                    "gbOS86 source", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!DockerService.IsInstalled)
            {
                MessageBox.Show(this,
                    "Docker Desktop is not installed.\n\n" +
                    "Building the gbOS86 image needs Docker Desktop for Windows with Linux containers. " +
                    "Install it from https://www.docker.com/products/docker-desktop/, start it, then click Build image again.\n\n" +
                    "You can also use an image that was built on another machine (\"Use an existing looper image\").",
                    "Docker Desktop required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int dataGb = (int)_dataSize.Value;
            DialogResult answer = MessageBox.Show(this,
                "Build the gbOS86 loopback image?\n\n" +
                "The first build downloads about 7.8 GB from Google and the project releases, " +
                "extracts about 8 GB, and can take 20-60 minutes depending on the connection.\n\n" +
                "Data partition: " + dataGb + " GB (sparse)\n" +
                "Output: " + Paths.ImagesDir,
                "Build gbOS86 image", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _cancelRequested = false;
            _preflight = null;
            SetBusy(true, "Building the gbOS86 image...");
            _progress.Style = ProgressBarStyle.Marquee;
            Log.WriteBlank();
            Log.Write("=== build started ===");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    string path = BuildService.BuildViaDocker(source, dataGb, Paths.ImagesDir,
                        Paths.ScriptsDir,
                        delegate(string line) { Log.Write(line); },
                        delegate { return _cancelRequested; });

                    Ui(delegate
                    {
                        SetBusy(false, "Image built.");
                        _imagePath = path;
                        _imageBox.Text = path;
                        _existingRadio.Checked = true;
                        RefreshPreflight();
                        MessageBox.Show(this,
                            "The gbOS86 image is ready:\n" + path +
                            "\n\nSelect a target disk and click Install gbOS86.",
                            "Image built", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (OperationCanceledException)
                {
                    Ui(delegate
                    {
                        SetBusy(false, "Build cancelled.");
                        Log.Write("Build cancelled.");
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        SetBusy(false, "Build failed.");
                        Log.Write("Build failed: " + ex.Message);
                        MessageBox.Show(this, ex.Message, "Build failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void StartInstall()
        {
            if (_busy || _preflight == null || !_preflight.Ok) return;
            DiskInfo disk = SelectedDisk();
            string imagePath = _imagePath;
            if (disk == null || imagePath.Length == 0) return;

            PreflightResult p = _preflight;
            string existing = p.HasExisting ? "\n\nThe existing gbOS86 installation on this disk will be replaced." : "";
            DialogResult answer = MessageBox.Show(this,
                "Install gbOS86 on this disk?\n\n" +
                "Disk " + disk.Number + ": " + disk.Model + "\n" +
                "Serial: " + disk.SerialOrUnknown + "\n" +
                "Free area: " + Fmt.Bytes(p.Free.Length) + " at offset " + Fmt.Bytes(p.Free.Offset) + "\n" +
                "gbOS86 will use: " + Fmt.Bytes(p.Required) + existing +
                "\n\nFour partitions are created in unallocated space. Windows and all existing " +
                "partitions are not touched. Do not close this window or power off during the write.\n\n" +
                "Continue?",
                "Install gbOS86", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _cancelRequested = false;
            SetBusy(true, "Installing gbOS86...");
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            Log.WriteBlank();
            Log.Write("=== install started ===");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    InstallReport report;
                    using (LooperImage image = LooperImage.Open(imagePath))
                    {
                        report = InstallService.Install(disk, image, p,
                            delegate(int percent, string text)
                            {
                                Ui(delegate
                                {
                                    if (!_busy) return;
                                    if (percent >= 0 && percent <= 100) _progress.Value = percent;
                                    SetStatus(text);
                                });
                            },
                            delegate { return _cancelRequested; });
                    }

                    Ui(delegate
                    {
                        _preflight = null;
                        SetBusy(false, "gbOS86 installed.");
                        _state = InstallState.Load();
                        _uninstallButton.Enabled = _state != null;
                        RefreshDisks();
                        string how = report.BootEntry != null && report.BootEntry.Native
                            ? "Restart the PC and press F12 (or your boot-menu key), then choose gbOS86."
                            : "A gbOS86 entry was added to the Windows Boot Manager. If the Windows boot " +
                              "menu is enabled, choose it there; otherwise press F12 at startup and pick gbOS86.";
                        MessageBox.Show(this,
                            "gbOS86 was installed on disk " + disk.Number + ".\n\n" + how +
                            "\n\nKeep Secure Boot off: gbOS86's boot loader is not signed.\n" +
                            "Windows remains the default boot option.\n\nLog: " + Log.FilePath,
                            "gbOS86 installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (OperationCanceledException)
                {
                    Ui(delegate
                    {
                        _preflight = null;
                        SetBusy(false, "Install cancelled.");
                        RefreshDisks();
                        MessageBox.Show(this,
                            "Installation cancelled. The new gbOS86 partitions were removed; " +
                            "existing Windows partitions were not changed.",
                            "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        _preflight = null;
                        SetBusy(false, "Install failed.");
                        RefreshDisks();
                        Log.Write("Install failed: " + ex.Message);
                        MessageBox.Show(this,
                            ex.Message + "\n\nIf gbOS86 partitions remain, use Uninstall or Disk Management.\n" +
                            "Log: " + Log.FilePath,
                            "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void StartUninstall()
        {
            if (_busy) return;
            InstallState state = InstallState.Load();
            if (state == null)
            {
                MessageBox.Show(this, "There is no recorded gbOS86 installation to remove.",
                    "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string when = state.InstalledAt.Length > 0 ? "\nInstalled: " + state.InstalledAt : "";
            DialogResult answer = MessageBox.Show(this,
                "Remove gbOS86 from disk " + state.DiskNumber + "?" + when +
                "\n\nThe gbOS86 partitions and its boot entry will be deleted. " +
                "Windows and all other partitions are not touched.",
                "Uninstall gbOS86", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _cancelRequested = false;
            SetBusy(true, "Removing gbOS86...");
            // Uninstall is a short sequence of partition removals; letting it
            // stop halfway would leave the installation in an unknown state.
            _cancelButton.Enabled = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;

            Task.Factory.StartNew(delegate
            {
                try
                {
                    InstallService.Uninstall(state, delegate(int percent, string text)
                    {
                        Ui(delegate
                        {
                            if (!_busy) return;
                            if (percent >= 0 && percent <= 100) _progress.Value = percent;
                            SetStatus(text);
                        });
                    });
                    Ui(delegate
                    {
                        _preflight = null;
                        SetBusy(false, "gbOS86 removed.");
                        _state = null;
                        _uninstallButton.Enabled = false;
                        RefreshDisks();
                        MessageBox.Show(this, "gbOS86 was removed.", "Uninstall",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate
                    {
                        SetBusy(false, "Uninstall failed.");
                        MessageBox.Show(this, ex.Message, "Uninstall failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void RequestCancel()
        {
            if (!_busy) return;
            _cancelRequested = true;
            _cancelButton.Enabled = false;
            SetStatus("Cancelling... the current step will stop and new partitions will be removed.");
            Log.Write("Cancel requested.");
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _diskCombo.Enabled = !busy;
            _refreshButton.Enabled = !busy;
            _buildRadio.Enabled = !busy;
            _existingRadio.Enabled = !busy;
            _cancelButton.Enabled = busy;
            _installButton.Enabled = !busy && _preflight != null && _preflight.Ok;
            _uninstallButton.Enabled = !busy && _state != null;
            UpdateImageMode();
            if (status != null) SetStatus(status);
            UseWaitCursor = busy;
            if (!busy && _progress.Style == ProgressBarStyle.Marquee)
                _progress.Style = ProgressBarStyle.Continuous;
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
        }

        private void OpenLogs()
        {
            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                System.Diagnostics.Process.Start("explorer.exe", Paths.LogsDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnLogLine(string line)
        {
            Ui(delegate
            {
                if (_logBox.IsDisposed) return;
                if (_logBox.TextLength > 1500000)
                    _logBox.Text = _logBox.Text.Substring(500000);
                _logBox.AppendText(line + "\r\n");
            });
        }

        private void Ui(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this,
                "An operation is still running. Click Cancel, wait for it to stop, then close the installer.",
                "gbOS86 Installer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Log.Line -= OnLogLine;
            base.Dispose(disposing);
        }
    }
}
