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

        private readonly TextBox _txtBackupDb = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtBackupFile = new() { Dock = DockStyle.Fill };
        private readonly RadioButton _rdoFull = new() { Text = "Đầy đủ (Full)", Checked = true, AutoSize = true };
        private readonly RadioButton _rdoDiff = new() { Text = "Chênh lệch (Diff)", AutoSize = true };
        private readonly CheckBox _chkCompression = new() { Text = "Nén backup", Checked = true, AutoSize = true };
        private readonly Button _btnBackup = new() { Text = "Sao lưu ngay", AutoSize = true };

        private readonly TextBox _txtRestoreFile = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreDb = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreDataDir = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtRestoreLogDir = new() { Dock = DockStyle.Fill };
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
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 28F));
            left.Controls.Add(srcGroup, 0, 0);
            left.Controls.Add(backupGroup, 0, 1);
            left.Controls.Add(sqliteGroup, 0, 2);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 28F));
            right.Controls.Add(dstGroup, 0, 0);
            right.Controls.Add(restoreGroup, 0, 1);
            right.Controls.Add(historyGroup, 0, 2);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            top.Controls.Add(left, 0, 0);
            top.Controls.Add(right, 0, 0);

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
                    split.SplitterDistance = Math.Clamp((int)(usable * 0.62),
                        200, Math.Max(200, usable - 140));
                }
                catch { }
            };
        }

        private Control BuildBackupPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var typeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            typeRow.Controls.Add(_rdoFull);
            typeRow.Controls.Add(_rdoDiff);
            typeRow.Controls.Add(_chkCompression);
            panel.Controls.Add(new Label { Text = "Tên DB:", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtBackupDb, 1, 0);
            panel.Controls.Add(new Label { Text = "File .bak (server):", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtBackupFile, 1, 1);
            panel.Controls.Add(new Label { Text = "Loại:", AutoSize = true }, 0, 2);
            panel.Controls.Add(typeRow, 1, 2);
            panel.Controls.Add(_btnBackup, 1, 3);
            return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { panel } };
        }

        private Control BuildRestorePanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var optRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            optRow.Controls.Add(_chkReplace);
            optRow.Controls.Add(_chkRecovery);
            panel.Controls.Add(new Label { Text = "File .bak (server):", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtRestoreFile, 1, 0);
            panel.Controls.Add(new Label { Text = "DB mới:", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtRestoreDb, 1, 1);
            panel.Controls.Add(new Label { Text = "Thư mục data:", AutoSize = true }, 0, 2);
            panel.Controls.Add(_txtRestoreDataDir, 1, 2);
            panel.Controls.Add(new Label { Text = "Thư mục log:", AutoSize = true }, 0, 3);
            panel.Controls.Add(_txtRestoreLogDir, 1, 3);
            panel.Controls.Add(new Label { Text = "Tùy chọn:", AutoSize = true }, 0, 4);
            panel.Controls.Add(optRow, 1, 4);
            var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            btnRow.Controls.Add(_btnFileList);
            btnRow.Controls.Add(_btnRestore);
            panel.Controls.Add(new Label { Text = "File logic:", AutoSize = true }, 0, 5);
            var filePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            filePanel.Controls.Add(_lstFiles, 0, 0);
            filePanel.Controls.Add(btnRow, 0, 1);
            panel.Controls.Add(filePanel, 1, 5);
            return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Controls = { panel } };
        }

        private Control BuildSqlitePanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
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
            var db = _txtBackupDb.Text.Trim();
            if (string.IsNullOrWhiteSpace(db))
            {
                MessageBox.Show("Nhập tên database cần sao lưu (mục Database ở khối nguồn chỉ để lọc, ô này mới là DB sao lưu).",
                    "Sao lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var file = _txtBackupFile.Text.Trim();
            if (string.IsNullOrWhiteSpace(file))
            {
                MessageBox.Show("Nhập đường dẫn file .bak TRÊN SERVER (VD: D:\\Backup\\db.bak).",
                    "Sao lưu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var options = BuildDestOptions(profile);
                var logger = new UiLogger(AppendLog, "Backup");
                var service = new BackupService(options, logger);
                var request = new BackupRequest
                {
                    Database = db,
                    BackupFile = file,
                    FullBackup = _rdoFull.Checked,
                    Compression = _chkCompression.Checked
                };
                AppendLog($"[THÔNG TIN] Bắt đầu sao lưu {db} ra {file} (đường dẫn trên server)...");
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                });
                var result = await service.BackupDatabaseAsync(request, _cts.Token, progress);
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
                _lblStatus.Text = result.Success ? "Sao lưu xong." : "Sao lưu thất bại: " + result.Error;
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
