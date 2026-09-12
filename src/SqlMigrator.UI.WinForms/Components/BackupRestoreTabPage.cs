using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services;
using SqlMigrator.Core.Services.Backup;
using SqlMigrator.UI.Services;

namespace SqlMigrator.UI.Components
{
    /// <summary>
    /// Tab Sao lưu / Khôi phục: SQL Server (BACKUP/RESTORE T-SQL + % tiến trình)
    /// và SQLite (copy file + kiểm tra toàn vẹn). Mọi đường dẫn backup/restore
    /// SQL Server đều là ĐƯỜNG DẪN TRÊN SERVER, không phải máy chạy app.
    /// </summary>
    public sealed class BackupRestoreTabPage : UserControl
    {
        private readonly IDataProtector _protector;
        private readonly IConnectionProfileStore _profileStore;
        private readonly SecureConnectionStringBuilder _builder;

        private readonly ConnectionEditor _sourceEditor;
        private readonly ConnectionEditor _destEditor;

        private readonly TextBox _txtBackupFolder = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtBackupFileName = new() { Dock = DockStyle.Fill };
        private readonly Button _btnBrowseBackupFile = new() { Text = "Chọn…", AutoSize = true };
        private readonly CheckedListBox _lstBackupDbs = new() { Dock = DockStyle.Fill, Height = 84, CheckOnClick = true };
        private readonly Button _btnReloadDbs = new() { Text = "Nạp DS database", AutoSize = true };
        private readonly Button _btnCheckAllDbs = new() { Text = "Chọn hết", AutoSize = true };
        private readonly Button _btnUncheckAllDbs = new() { Text = "Bỏ hết", AutoSize = true };
        private readonly RadioButton _rdoFull = new() { Text = "Đầy đủ (Full)", Checked = true, AutoSize = true };
        private readonly RadioButton _rdoDiff = new() { Text = "Chênh lệch (Diff)", AutoSize = true };
        private readonly CheckBox _chkCompression = new() { Text = "Nén backup", Checked = true, AutoSize = true };
        private readonly Button _btnBackup = new() { Text = "Sao lưu ngay", AutoSize = true };

        private readonly TextBox _txtRestoreFile = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreDb = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreDataDir = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreLogDir = new() { Dock = DockStyle.Fill };
        private readonly Button _btnBrowseRestoreFile = new() { Text = "Chọn…", AutoSize = true };
        private readonly Button _btnBrowseRestoreDataDir = new() { Text = "Chọn…", AutoSize = true };
        private readonly Button _btnBrowseRestoreLogDir = new() { Text = "Chọn…", AutoSize = true };
        private readonly CheckBox _chkReplace = new() { Text = "Ghi đè DB đã tồn tại (ngắt kết nối đang dùng)", AutoSize = true };
        private readonly CheckBox _chkRecovery = new() { Text = "Online ngay sau restore (RECOVERY)", Checked = true, AutoSize = true };
        private readonly Button _btnFileList = new() { Text = "Đọc danh sách file", AutoSize = true };
        private readonly Button _btnRestore = new() { Text = "Khôi phục ngay", AutoSize = true };
        private readonly ListBox _lstFiles = new() { Dock = DockStyle.Fill, Height = 60 };

        private readonly TextBox _txtSqliteSource = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtSqliteDest = new() { Dock = DockStyle.Fill };
        private readonly CheckBox _chkSqliteOverwrite = new() { Text = "Ghi đè file đích", AutoSize = true };
        private readonly Button _btnSqliteBackup = new() { Text = "Sao lưu SQLite", AutoSize = true };

        private readonly DataGridView _dgvHistory = new();
        private readonly Button _btnReloadHistory = new() { Text = "Tải lại lịch sử", AutoSize = true };
        private readonly RichTextBox _txtLog = new();
        private readonly ProgressBar _progressBar = new() { Style = ProgressBarStyle.Continuous, Maximum = 100, Minimum = 0 };
        private readonly Label _lblStatus = new() { Text = "Sẵn sàng.", AutoSize = true };

        private CancellationTokenSource? _cts;

        public BackupRestoreTabPage(
            IDataProtector protector, IConnectionProfileStore profileStore, SecureConnectionStringBuilder builder)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            Dock = DockStyle.Fill;

            _sourceEditor = new ConnectionEditor(protector, profileStore, builder, "source");
            _destEditor = new ConnectionEditor(protector, profileStore, builder, "dest");
            WireEditors();
            BuildLayout();
            WireEvents();
            _ = RefreshHistoryAsync();
        }

        private void WireEditors()
        {
            Func<ConnectionProfile, Task<List<string>>> fetcher =
                async p => await DatabaseCatalog.GetDatabasesAsync(p, _builder, log: AppendLog);
            Func<ConnectionProfile, Task> tester =
                async p => await DatabaseCatalog.TestConnectionAsync(p, _builder, log: AppendLog);
            _sourceEditor.DatabaseFetcher = fetcher;
            _sourceEditor.ConnTester = tester;
            _destEditor.DatabaseFetcher = fetcher;
            _destEditor.ConnTester = tester;
            _sourceEditor.LogSink = AppendLog;
            _destEditor.LogSink = AppendLog;
        }

        private void BuildLayout()
        {
            var srcGroup = new GroupBox { Text = "1. Server chứa DB cần sao lưu", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _sourceEditor.Dock = DockStyle.Fill;
            srcGroup.Controls.Add(_sourceEditor);

            var dstGroup = new GroupBox { Text = "2. Server đích khôi phục", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _destEditor.Dock = DockStyle.Fill;
            dstGroup.Controls.Add(_destEditor);

            var backupGroup = new GroupBox { Text = "3. Sao lưu SQL Server (đường dẫn TRÊN SERVER)", Dock = DockStyle.Fill, Padding = new Padding(6) };
            backupGroup.Controls.Add(BuildBackupPanel());

            var restoreGroup = new GroupBox { Text = "4. Khôi phục SQL Server", Dock = DockStyle.Fill, Padding = new Padding(6) };
            restoreGroup.Controls.Add(BuildRestorePanel());

            var sqliteGroup = new GroupBox { Text = "5. Sao lưu SQLite (file local)", Dock = DockStyle.Fill, Padding = new Padding(6) };
            sqliteGroup.Controls.Add(BuildSqlitePanel());

            var historyGroup = new GroupBox { Text = "6. Lịch sử", Dock = DockStyle.Fill, Padding = new Padding(6) };
            var historyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            historyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            historyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _dgvHistory.Dock = DockStyle.Fill;
            _dgvHistory.AllowUserToAddRows = false;
            _dgvHistory.AllowUserToDeleteRows = false;
            _dgvHistory.ReadOnly = true;
            _dgvHistory.MultiSelect = false;
            _dgvHistory.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            _dgvHistory.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dgvHistory.RowHeadersVisible = false;
            historyPanel.Controls.Add(_dgvHistory, 0, 0);
            historyPanel.Controls.Add(_btnReloadHistory, 0, 1);
            historyGroup.Controls.Add(historyPanel);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 26F));
            left.Controls.Add(srcGroup, 0, 0);
            left.Controls.Add(backupGroup, 0, 1);
            left.Controls.Add(sqliteGroup, 0, 2);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 24F));
            right.Controls.Add(dstGroup, 0, 0);
            right.Controls.Add(restoreGroup, 0, 1);
            right.Controls.Add(historyGroup, 0, 2);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            top.Controls.Add(left, 0, 0);
            top.Controls.Add(right, 1, 0);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6
            };
            split.Panel1.Controls.Add(top);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Height = 18;
            _lblStatus.Dock = DockStyle.Fill;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.ReadOnly = true;
            _txtLog.Multiline = true;
            _txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            _txtLog.Font = new Font("Consolas", 9.5F);
            bottom.Controls.Add(_progressBar, 0, 0);
            bottom.Controls.Add(_lblStatus, 0, 1);
            bottom.Controls.Add(_txtLog, 0, 2);
            split.Panel2.Controls.Add(bottom);

            Controls.Add(split);
            Load += (_, _) =>
            {
                try
                {
                    var usable = ClientSize.Height - split.SplitterWidth;
                    // Trên 75% / dưới 25%: khung log nhỏ hơn ~2/3 so với cũ,
                    // nhường không gian cho 6 group box bên trên hiển thị rộng rãi hơn.
                    split.SplitterDistance = Math.Clamp((int)(usable * 0.75),
                        280, Math.Max(280, usable - 100));
                }
                catch { }
            };
        }

        private Control BuildBackupPanel()
        {
            // Dock=Top (không phải Fill) để panel cao hơn khung nhìn thì
            // thanh cuộn của Panel ngoài mới hiện — nội dung không bao giờ bị xén.
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var typeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            typeRow.Controls.Add(_rdoFull);
            typeRow.Controls.Add(_rdoDiff);
            typeRow.Controls.Add(_chkCompression);
            // Hàng chọn DB: lưới checkbox + 3 nút nạp/chọn/bỏ (dọc).
            var dbButtons = new FlowLayoutPanel
            {
                AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown
            };
            dbButtons.Controls.Add(_btnReloadDbs);
            dbButtons.Controls.Add(_btnCheckAllDbs);
            dbButtons.Controls.Add(_btnUncheckAllDbs);
            panel.Controls.Add(new Label { Text = "Database cần sao lưu:", AutoSize = true });
            panel.SetColumnSpan(panel.Controls[panel.Controls.Count - 1], 3);
            panel.Controls.Add(_lstBackupDbs, 1, 1);
            panel.Controls.Add(dbButtons, 2, 1);
            panel.Controls.Add(new Label { Text = "Thư mục lưu (server):", AutoSize = true }, 0, 2);
            panel.Controls.Add(_txtBackupFolder, 1, 2);
            panel.Controls.Add(_btnBrowseBackupFile, 2, 2);
            panel.Controls.Add(new Label { Text = "Tên file (.bak):", AutoSize = true }, 0, 3);
            panel.Controls.Add(_txtBackupFileName, 1, 3);
            panel.SetColumnSpan(_txtBackupFileName, 2);
            panel.Controls.Add(new Label { Text = "Loại:", AutoSize = true }, 0, 4);
            panel.Controls.Add(typeRow, 1, 4);
            panel.SetColumnSpan(typeRow, 2);
            panel.Controls.Add(_btnBackup, 1, 5);
            return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { panel } };
        }

        /// <summary>
        /// Duyệt disk TRÊN SERVER nguồn để chọn THƯ MỤC lưu file .bak
        /// (không phải disk máy chạy app). Tên file xử lý riêng theo từng DB.
        /// </summary>
        private void PickBackupFolderOnServer()
        {
            PickServerFolder(_sourceEditor, "nguồn", _txtBackupFolder, keepFileName: false);
        }

        /// <summary>Database hệ thống: vẫn liệt kê nhưng mặc định không check.</summary>
        private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        {
            "master", "model", "msdb", "tempdb"
        };

        /// <summary>
        /// Nạp toàn bộ database trên server nguồn vào lưới checkbox (DB hệ thống
        /// mặc định bỏ check). Dùng chung kết nối ở khối nguồn.
        /// </summary>
        private async Task LoadSourceDatabasesForBackupAsync()
        {
            var profile = _sourceEditor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.SqlServer)
            {
                MessageBox.Show("Sao lưu multi-database chỉ dùng cho SQL Server.",
                    "Không hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _btnReloadDbs.Enabled = false;
            try
            {
                var dbs = await DatabaseCatalog.GetDatabasesAsync(profile, _builder, log: AppendLog);
                _lstBackupDbs.Items.Clear();
                foreach (var db in dbs)
                    _lstBackupDbs.Items.Add(db, !SystemDatabases.Contains(db));
                RefreshBackupFileNameBox();
                AppendLog($"[THÔNG TIN] Server nguồn {profile.Server} có {dbs.Count} database " +
                    $"({_lstBackupDbs.CheckedItems.Count} đã chọn).");
            }
            catch (Exception ex)
            {
                AppendLog("[LỖI] Không nạp được danh sách database: " + ex.Message);
                MessageBox.Show("Không nạp được danh sách database:\n" + ex.Message,
                    "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnReloadDbs.Enabled = true;
            }
        }

        private List<string> GetCheckedBackupDbs()
        {
            var result = new List<string>();
            foreach (var item in _lstBackupDbs.CheckedItems)
            {
                if (item is string db && !string.IsNullOrWhiteSpace(db))
                    result.Add(db);
            }
            return result;
        }

        /// <summary>Tên file mặc định: &lt;TênDB&gt;_yyyyMMdd_HHmmss[_Diff].bak (không dấu cách, dễ sắp xếp).</summary>
        private string SuggestBackupFileName(string db) =>
            $"{db}_{DateTime.Now:yyyyMMdd_HHmmss}{(_rdoDiff.Checked ? "_Diff" : "")}.bak";

        /// <summary>
        /// Chỉ cho sửa tên file khi đúng 1 DB được check; nhiều DB thì khóa ô
        /// và dùng tên tự sinh để khỏi trùng. Gợi ý tên khi ô đang trống.
        /// </summary>
        private void RefreshBackupFileNameBox()
        {
            var checkedDbs = GetCheckedBackupDbs();
            var single = checkedDbs.Count == 1;
            _txtBackupFileName.Enabled = single;
            if (single && string.IsNullOrWhiteSpace(_txtBackupFileName.Text))
                _txtBackupFileName.Text = SuggestBackupFileName(checkedDbs[0]);
        }

        /// <summary>
        /// Duyệt disk TRÊN SERVER (nguồn/đích) để chọn thư mục, điền vào ô nhập liệu.
        /// keepFileName=true thì giữ tên file đã gõ (dùng cho ô .bak), false thì lấy nguyên thư mục.
        /// </summary>
        private void PickServerFolder(ConnectionEditor editor, string role, TextBox target,
            bool keepFileName, string defaultFileName = "")
        {
            var profile = editor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.SqlServer)
            {
                MessageBox.Show("Duyệt disk server chỉ dùng cho SQL Server. " +
                    "Sao lưu SQLite dùng nút 'Chọn…'/'Lưu…' ở mục 5.",
                    "Không hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string connectionString;
            try
            {
                connectionString = _builder.Build(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không dựng được kết nối server " + role + ": " + ex.Message,
                    "Lỗi kết nối", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var service = new ServerFolderService(m => AppendLog("[THƯ MỤC] " + m));
            using var dialog = new ServerFolderPickerDialog(service, connectionString, CancellationToken.None, role);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var folder = dialog.SelectedPath.Trim().TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (!keepFileName)
            {
                target.Text = folder;
            }
            else
            {
                var fileName = ExtractFileName(target.Text);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = defaultFileName;
                target.Text = string.IsNullOrWhiteSpace(fileName)
                    ? folder + "\\"
                    : folder + "\\" + fileName;
            }
            AppendLog("[THÔNG TIN] Đã chọn trên server " + role + ": " + target.Text);
        }

        /// <summary>Tách tên file khỏi đường dẫn đã gõ (rỗng nếu ô chỉ có thư mục hoặc chưa nhập).</summary>
        private static string ExtractFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var name = path.Replace('/', '\\');
            var idx = name.LastIndexOf('\\');
            name = idx >= 0 ? name.Substring(idx + 1) : name;
            return name.Contains('.') ? name : string.Empty;
        }

        private Control BuildRestorePanel()
        {
            // Dock=Top để thanh cuộn hoạt động khi nội dung (nhất là hàng File logic)
            // cao hơn group — xem chú thích ở BuildBackupPanel.
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var optRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            optRow.Controls.Add(_chkReplace);
            optRow.Controls.Add(_chkRecovery);
            panel.Controls.Add(new Label { Text = "File .bak (server):", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtRestoreFile, 1, 0);
            panel.Controls.Add(_btnBrowseRestoreFile, 2, 0);
            panel.Controls.Add(new Label { Text = "DB mới:", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtRestoreDb, 1, 1);
            panel.SetColumnSpan(_txtRestoreDb, 2);
            panel.Controls.Add(new Label { Text = "Thư mục data:", AutoSize = true }, 0, 2);
            panel.Controls.Add(_txtRestoreDataDir, 1, 2);
            panel.Controls.Add(_btnBrowseRestoreDataDir, 2, 2);
            panel.Controls.Add(new Label { Text = "Thư mục log:", AutoSize = true }, 0, 3);
            panel.Controls.Add(_txtRestoreLogDir, 1, 3);
            panel.Controls.Add(_btnBrowseRestoreLogDir, 2, 3);
            panel.Controls.Add(new Label { Text = "Tùy chọn:", AutoSize = true }, 0, 4);
            panel.Controls.Add(optRow, 1, 4);
            panel.SetColumnSpan(optRow, 2);
            var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            btnRow.Controls.Add(_btnFileList);
            btnRow.Controls.Add(_btnRestore);
            panel.Controls.Add(new Label { Text = "File logic:", AutoSize = true }, 0, 5);
            var filePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            filePanel.Controls.Add(_lstFiles, 0, 0);
            filePanel.Controls.Add(btnRow, 0, 1);
            panel.Controls.Add(filePanel, 1, 5);
            panel.SetColumnSpan(filePanel, 2);
            return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { panel } };
        }

        private Control BuildSqlitePanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var btnSrc = new Button { Text = "Chọn…", AutoSize = true };
            var btnDst = new Button { Text = "Lưu…", AutoSize = true };
            btnSrc.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Chọn file SQLite nguồn",
                    Filter = "SQLite (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|Tất cả (*.*)|*.*"
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _txtSqliteSource.Text = dialog.FileName;
            };
            btnDst.Click += (_, _) =>
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Lưu bản sao SQLite",
                    Filter = "SQLite (*.db)|*.db|Tất cả (*.*)|*.*",
                    FileName = "backup.db"
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _txtSqliteDest.Text = dialog.FileName;
            };
            panel.Controls.Add(new Label { Text = "File nguồn:", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtSqliteSource, 1, 0);
            panel.Controls.Add(btnSrc, 2, 0);
            panel.Controls.Add(new Label { Text = "File đích:", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtSqliteDest, 1, 1);
            panel.Controls.Add(btnDst, 2, 1);
            var runRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            runRow.Controls.Add(_chkSqliteOverwrite);
            runRow.Controls.Add(_btnSqliteBackup);
            panel.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 2);
            panel.Controls.Add(runRow, 1, 2);
            return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { panel } };
        }

        private void WireEvents()
        {
            _btnBackup.Click += async (_, _) => await RunBackupAsync();
            _btnBrowseBackupFile.Click += (_, _) => PickBackupFolderOnServer();
            _btnReloadDbs.Click += async (_, _) => await LoadSourceDatabasesForBackupAsync();
            _btnCheckAllDbs.Click += (_, _) =>
            {
                for (var i = 0; i < _lstBackupDbs.Items.Count; i++) _lstBackupDbs.SetItemChecked(i, true);
                RefreshBackupFileNameBox();
            };
            _btnUncheckAllDbs.Click += (_, _) =>
            {
                for (var i = 0; i < _lstBackupDbs.Items.Count; i++) _lstBackupDbs.SetItemChecked(i, false);
                RefreshBackupFileNameBox();
            };
            _lstBackupDbs.ItemCheck += (_, _) => BeginInvoke(new Action(RefreshBackupFileNameBox));
            _rdoFull.CheckedChanged += (_, _) => RefreshBackupFileNameBox();
            _rdoDiff.CheckedChanged += (_, _) => RefreshBackupFileNameBox();
            _btnBrowseRestoreFile.Click += (_, _) =>
                PickServerFolder(_destEditor, "đích", _txtRestoreFile, keepFileName: true);
            _btnBrowseRestoreDataDir.Click += (_, _) =>
                PickServerFolder(_destEditor, "đích", _txtRestoreDataDir, keepFileName: false);
            _btnBrowseRestoreLogDir.Click += (_, _) =>
                PickServerFolder(_destEditor, "đích", _txtRestoreLogDir, keepFileName: false);
            _btnFileList.Click += async (_, _) => await LoadBackupFileListAsync();
            _btnRestore.Click += async (_, _) => await RunRestoreAsync();
            _btnSqliteBackup.Click += async (_, _) => await RunSqliteBackupAsync();
            _btnReloadHistory.Click += async (_, _) => await RefreshHistoryAsync();
        }

        private void SetBusy(bool busy)
        {
            _btnBackup.Enabled = !busy;
            _btnFileList.Enabled = !busy;
            _btnRestore.Enabled = !busy;
            _btnSqliteBackup.Enabled = !busy;
            _btnReloadHistory.Enabled = !busy;
            if (!busy)
                _progressBar.Value = 0;
        }

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLogCore(line)));
                return;
            }
            AppendLogCore(line);
        }

        private void AppendLogCore(string line)
        {
            var color = line.Contains("[LỖI]")
                ? Color.Firebrick
                : line.Contains("[CẢNH BÁO]")
                    ? Color.DarkOrange
                    : Color.FromArgb(30, 41, 59);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
            _txtLog.SelectionColor = color;
            _txtLog.AppendText(line + Environment.NewLine);
            _txtLog.ScrollToCaret();
        }

        private MigrationOptions BuildDestOptions(ConnectionProfile profile)
        {
            return new MigrationOptions
            {
                DestinationConnectionString = _builder.Build(profile),
                CommandTimeoutSeconds = 600
            };
        }

        private async Task RunBackupAsync()
        {
            var profile = _sourceEditor.ReadProfile();
            if (profile == null) return;
            var dbs = GetCheckedBackupDbs();
            if (dbs.Count == 0)
            {
                MessageBox.Show("Hãy bấm 'Nạp DS database' rồi check chọn ít nhất 1 database cần sao lưu.",
                    "Sao lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var folder = _txtBackupFolder.Text.Trim().TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show("Chọn thư mục lưu file .bak TRÊN SERVER (nút 'Chọn…' hoặc gõ tay VD: D:\\Backup).",
                    "Sao lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Chốt tên file cho từng DB: 1 DB thì theo ô nhập (trống → gợi ý),
            // nhiều DB thì tự sinh để khỏi trùng. Cùng một mốc giờ cho cả đợt.
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var diffSuffix = _rdoDiff.Checked ? "_Diff" : "";
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var db in dbs)
            {
                string name;
                if (dbs.Count == 1)
                {
                    name = _txtBackupFileName.Text.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = $"{db}_{stamp}{diffSuffix}.bak";
                        _txtBackupFileName.Text = name;
                    }
                    else if (!name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        name += ".bak";
                    }
                }
                else
                {
                    name = $"{db}_{stamp}{diffSuffix}.bak";
                }
                files[db] = folder + "\\" + name;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var options = BuildDestOptions(profile);
                var logger = new UiLogger(AppendLog, "Backup");
                var service = new BackupService(options, logger);
                var done = 0;
                var failed = 0;
                foreach (var db in dbs)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var file = files[db];
                    var request = new BackupRequest
                    {
                        Database = db,
                        BackupFile = file,
                        FullBackup = _rdoFull.Checked,
                        Compression = _chkCompression.Checked
                    };
                    AppendLog($"[THÔNG TIN] Sao lưu {db} ({done + 1}/{dbs.Count}) ra {file}...");
                    var result = await service.BackupDatabaseAsync(request, _cts.Token);
                    done++;
                    if (!result.Success) failed++;
                    _progressBar.Value = (int)(done * 100.0 / dbs.Count);
                    _lblStatus.Text = $"Đã sao lưu {done}/{dbs.Count} database...";
                    await BackupHistoryStore.AppendAsync(new BackupHistoryEntry
                    {
                        AtUtc = DateTime.UtcNow,
                        Kind = request.FullBackup ? "Sao lưu Full" : "Sao lưu Diff",
                        Server = profile.Server,
                        Database = db,
                        File = file,
                        Success = result.Success,
                        Message = result.Success ? $"xong trong {result.Elapsed:mm\\:ss}" : result.Error ?? "lỗi"
                    });
                }
                _lblStatus.Text = failed == 0
                    ? $"Sao lưu xong {dbs.Count} database."
                    : $"Sao lưu xong {dbs.Count - failed}/{dbs.Count} database ({failed} lỗi).";
                await RefreshHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy sao lưu.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task LoadBackupFileListAsync()
        {
            var profile = _destEditor.ReadProfile();
            if (profile == null) return;
            var file = _txtRestoreFile.Text.Trim();
            if (string.IsNullOrWhiteSpace(file))
            {
                MessageBox.Show("Nhập đường dẫn file .bak TRÊN SERVER trước.",
                    "Khôi phục", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                var service = new BackupService(BuildDestOptions(profile),
                    new UiLogger(AppendLog, "Backup"));
                var entries = await service.GetBackupFileListAsync(file);
                _lstFiles.Items.Clear();
                foreach (var e in entries)
                    _lstFiles.Items.Add($"{e.LogicalName} ({e.Type}) → {e.PhysicalName}");
                if (entries.Count == 0)
                    AppendLog("[CẢNH BÁO] File backup không chứa file logic nào.");
            }
            catch (Exception ex)
            {
                AppendLog("[LỖI] Không đọc được file backup: " + ex.Message);
            }
        }

        private async Task RunRestoreAsync()
        {
            var profile = _destEditor.ReadProfile();
            if (profile == null) return;
            var file = _txtRestoreFile.Text.Trim();
            var db = _txtRestoreDb.Text.Trim();
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(db))
            {
                MessageBox.Show("Nhập đủ đường dẫn file .bak (trên server) và tên database mới.",
                    "Khôi phục", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirm = MessageBox.Show(
                $"Khôi phục database '{db}' từ file:\n{file}\n\n"
                + (_chkReplace.Checked
                    ? "CẢNH BÁO: sẽ GHI ĐÈ database đã tồn tại (ngắt mọi kết nối đang dùng nó).\n\n"
                    : "Database đã tồn tại sẽ báo lỗi (không ghi đè).\n\n")
                + "Tiếp tục?",
                "Xác nhận khôi phục", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
                return;

            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var service = new BackupService(BuildDestOptions(profile),
                    new UiLogger(AppendLog, "Backup"));
                var request = new RestoreRequest
                {
                    Database = db,
                    BackupFile = file,
                    DataDirectory = string.IsNullOrWhiteSpace(_txtRestoreDataDir.Text) ? null : _txtRestoreDataDir.Text.Trim(),
                    LogDirectory = string.IsNullOrWhiteSpace(_txtRestoreLogDir.Text) ? null : _txtRestoreLogDir.Text.Trim(),
                    ReplaceExisting = _chkReplace.Checked,
                    WithRecovery = _chkRecovery.Checked
                };
                AppendLog($"[THÔNG TIN] Bắt đầu khôi phục {db}...");
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                });
                var result = await service.RestoreDatabaseAsync(request, _cts.Token, progress);
                await BackupHistoryStore.AppendAsync(new BackupHistoryEntry
                {
                    AtUtc = DateTime.UtcNow,
                    Kind = "Khôi phục",
                    Server = profile.Server,
                    Database = db,
                    File = file,
                    Success = result.Success,
                    Message = result.Success ? $"xong trong {result.Elapsed:mm\\:ss}" : result.Error ?? "lỗi"
                });
                _lblStatus.Text = result.Success
                    ? "Khôi phục xong."
                    : "Khôi phục thất bại: " + result.Error;

                // Đối chiếu nhanh: đếm đối tượng trên DB vừa restore.
                if (result.Success)
                    await VerifyRestoredAsync(profile, db);
                await RefreshHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy khôi phục.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>Đối chiếu nhanh sau restore: đếm bảng/view/SP trên DB vừa tạo.</summary>
        private async Task VerifyRestoredAsync(ConnectionProfile profile, string db)
        {
            try
            {
                var reader = new DatabaseInventoryReader(new UiLogger(AppendLog, "Backup"));
                var cs = _builder.Build(profile);
                var inv = await reader.ReadAsync(WithDatabase(cs, db));
                if (inv.Ok)
                    AppendLog($"[THÔNG TIN] Kiểm tra nhanh {db}: {inv.Tables.Count:N0} bảng, "
                        + $"{inv.Views.Count:N0} view, {inv.Procedures.Count:N0} SP, ~{inv.EstimatedRows:N0} dòng.");
                else
                    AppendLog("[CẢNH BÁO] Không đọc được hồ sơ DB vừa restore: " + inv.Error);
            }
            catch (Exception ex)
            {
                AppendLog("[CẢNH BÁO] Không đối chiếu được sau restore: " + ex.Message);
            }
        }

        private static string WithDatabase(string connectionString, string db)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = db
            };
            return builder.ConnectionString;
        }

        private async Task RunSqliteBackupAsync()
        {
            var source = _txtSqliteSource.Text.Trim();
            var dest = _txtSqliteDest.Text.Trim();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                MessageBox.Show("Chọn đủ file SQLite nguồn và file đích.",
                    "Sao lưu SQLite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var service = new SqliteBackupService(new UiLogger(AppendLog, "Backup"));
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                });
                var result = await service.BackupFileAsync(
                    source, dest, _chkSqliteOverwrite.Checked, _cts.Token, progress);
                await BackupHistoryStore.AppendAsync(new BackupHistoryEntry
                {
                    AtUtc = DateTime.UtcNow,
                    Kind = "Sao lưu SQLite",
                    Server = "(file local)",
                    Database = source,
                    File = dest,
                    Success = result.Success,
                    Message = result.Success ? $"xong trong {result.Elapsed:mm\\:ss}" : result.Error ?? "lỗi"
                });
                _lblStatus.Text = result.Success ? "Sao lưu SQLite xong." : "Thất bại: " + result.Error;
                await RefreshHistoryAsync();
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy sao lưu.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task RefreshHistoryAsync()
        {
            try
            {
                var history = await BackupHistoryStore.LoadAsync();
                _dgvHistory.Rows.Clear();
                if (_dgvHistory.Columns.Count == 0)
                {
                    _dgvHistory.Columns.Add("Time", "Thời gian");
                    _dgvHistory.Columns.Add("Kind", "Loại");
                    _dgvHistory.Columns.Add("Server", "Server");
                    _dgvHistory.Columns.Add("Database", "Database");
                    _dgvHistory.Columns.Add("File", "File");
                    _dgvHistory.Columns.Add("Result", "Kết quả");
                }
                foreach (var h in history)
                {
                    var rowIdx = _dgvHistory.Rows.Add(
                        h.AtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                        h.Kind, h.Server, h.Database, h.File,
                        h.Success ? "✔" : "✘ " + h.Message);
                    _dgvHistory.Rows[rowIdx].DefaultCellStyle.ForeColor =
                        h.Success ? Color.Black : Color.Red;
                }
            }
            catch
            {
            }
        }
    }
}
