using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services.DbProviders;
using SqlMigrator.Core.Services.Manage;
using SqlMigrator.UI.Services;

namespace SqlMigrator.UI.Components
{
    /// <summary>
    /// Tab Quản trị (Manage v1, flow riêng — không can thiệp pipeline migrate):
    /// đọc database/dung lượng, đọc session, kill session có hộp xác nhận +
    /// tự kiểm tra trạng thái đổi sau kill. Hỗ trợ SQL Server và MySQL/MariaDB.
    /// Bảo mật: mật khẩu chỉ trong bộ nhớ khi mở kết nối, không log secret.
    /// </summary>
    public sealed class ManageTabPage : UserControl
    {
        private readonly IDataProtector _protector;
        private readonly IConnectionProfileStore _profileStore;
        private readonly SecureConnectionStringBuilder _builder;

        private readonly ConnectionEditor _editor;
        private readonly DataGridView _dgvDatabases = new();
        private readonly DataGridView _dgvSessions = new();
        private readonly Button _btnLoadDb = new() { Text = "Nạp database", AutoSize = true };
        private readonly Button _btnLoadSessions = new() { Text = "Nạp phiên", AutoSize = true };
        private readonly Button _btnKill = new() { Text = "Kill session đã chọn", AutoSize = true };
        private readonly Label _lblStatus = new() { Text = "Sẵn sàng.", AutoSize = true };
        private readonly DataGridView _dgvRoutines = new();
        private readonly ComboBox _cmbRoutineTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _btnRoutineReport = new() { Text = "Lập báo cáo routine", AutoSize = true };
        private readonly TextBox _txtMongoDump = new() { Dock = DockStyle.Fill, PlaceholderText = "mongodump (qua PATH)" };
        private readonly TextBox _txtMongoRestore = new() { Dock = DockStyle.Fill, PlaceholderText = "mongorestore (qua PATH)" };
        private readonly Button _btnMongoBackup = new() { Text = "Sao lưu MongoDB", AutoSize = true };
        private readonly Button _btnMongoRestore = new() { Text = "Khôi phục MongoDB", AutoSize = true };

        public ManageTabPage(
            IDataProtector protector, IConnectionProfileStore profileStore, SecureConnectionStringBuilder builder)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            Dock = DockStyle.Fill;

            _editor = new ConnectionEditor(protector, profileStore, builder, "manage");
            _editor.DatabaseFetcher = async p => await DatabaseCatalog.GetDatabasesAsync(p, _builder);
            _editor.ConnTester = async p => await DatabaseCatalog.TestConnectionAsync(p, _builder);
            _editor.LogSink = msg => _lblStatus.Text = msg;

            BuildLayout();
            WireEvents();
        }

        private void BuildLayout()
        {
            var connGroup = new GroupBox { Text = "1. Kết nối server cần quản trị", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _editor.Dock = DockStyle.Fill;
            connGroup.Controls.Add(_editor);

            var dbGroup = new GroupBox { Text = "2. Database + dung lượng", Dock = DockStyle.Fill, Padding = new Padding(6) };
            SetupGrid(_dgvDatabases, "Tên database", "Trạng thái", "Đã cấp (MB)", "Đã dùng (MB)");
            var dbPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            dbPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            dbPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dbPanel.Controls.Add(_dgvDatabases, 0, 0);
            dbPanel.Controls.Add(_btnLoadDb, 0, 1);
            dbGroup.Controls.Add(dbPanel);

            var sessGroup = new GroupBox { Text = "3. Phiên đang chạy", Dock = DockStyle.Fill, Padding = new Padding(6) };
            SetupGrid(_dgvSessions, "Id", "Đăng nhập", "Host", "DB", "Trạng thái", "Lệnh", "Chờ");
            var sessPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            sessPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            sessPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            sessPanel.Controls.Add(_dgvSessions, 0, 0);
            var sessButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            sessButtons.Controls.Add(_btnLoadSessions);
            sessButtons.Controls.Add(_btnKill);
            sessPanel.Controls.Add(sessButtons, 0, 1);
            sessGroup.Controls.Add(sessPanel);

            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            middle.Controls.Add(dbGroup, 0, 0);
            middle.Controls.Add(sessGroup, 1, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(connGroup, 0, 0);
            root.Controls.Add(middle, 0, 1);
            root.Controls.Add(BuildRoutineGroup(), 0, 2);
            root.Controls.Add(BuildMongoDumpGroup(), 0, 3);
            root.Controls.Add(_lblStatus, 0, 4);
            Controls.Add(root);
        }

        /// <summary>
        /// Nhóm 5: sao lưu/khôi phục MongoDB bằng binary mongodump/mongorestore.
        /// Tool không bundle binary nên người dùng cấu hình đường dẫn (để trống tên
        /// file thì tìm qua PATH). Mật khẩu không bao giờ hiện trong log.
        /// </summary>
        private Control BuildMongoDumpGroup()
        {
            var group = new GroupBox
            {
                Text = "5. Sao lưu / khôi phục MongoDB (mongodump / mongorestore)",
                Dock = DockStyle.Fill,
                Padding = new Padding(6)
            };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var btnBrowseDump = new Button { Text = "Chọn…", AutoSize = true };
            var btnBrowseRestore = new Button { Text = "Chọn…", AutoSize = true };
            btnBrowseDump.Click += (_, _) => PickToolPath(_txtMongoDump);
            btnBrowseRestore.Click += (_, _) => PickToolPath(_txtMongoRestore);
            panel.Controls.Add(new Label { Text = "mongodump:", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtMongoDump, 1, 0);
            panel.Controls.Add(btnBrowseDump, 2, 0);
            panel.Controls.Add(new Label { Text = "mongorestore:", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtMongoRestore, 1, 1);
            panel.Controls.Add(btnBrowseRestore, 2, 1);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row.Controls.Add(_btnMongoBackup);
            row.Controls.Add(_btnMongoRestore);
            panel.Controls.Add(new Label { Text = "Chạy:", AutoSize = true }, 0, 2);
            panel.Controls.Add(row, 1, 2);
            panel.SetColumnSpan(row, 2);
            group.Controls.Add(panel);
            return group;
        }

        private void PickToolPath(TextBox target)
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Chương trình (*.exe)|*.exe|Tất cả (*.*)|*.*",
                Title = "Chọn binary tool MongoDB"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                target.Text = dlg.FileName;
        }

        private static string PickFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Chọn thư mục dump MongoDB (máy chạy app)"
            };
            return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : "";
        }

        /// <summary>Sao lưu database MongoDB đang chọn ở ô Database ra thư mục.</summary>
        private async Task RunMongoBackupAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.MongoDb)
            {
                MessageBox.Show("Chức năng này chỉ dùng cho MongoDB.",
                    "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(profile.Database) || profile.Database == ConnectionEditor.DbPlaceholder)
            {
                MessageBox.Show("Hãy Connect DB và chọn database MongoDB cần sao lưu ở ô Database.",
                    "Sao lưu MongoDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var folder = PickFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;
            SetBusy(true);
            try
            {
                _lblStatus.Text = $"Đang mongodump {profile.Database}...";
                var result = await new MongoDumpService().BackupAsync(
                    string.IsNullOrWhiteSpace(_txtMongoDump.Text) ? "mongodump" : _txtMongoDump.Text,
                    BuildProbe(profile), profile.Database, folder);
                _lblStatus.Text = result.Success
                    ? $"Sao lưu MongoDB xong trong {result.Elapsed}."
                    : "Sao lưu MongoDB thất bại: " + result.Error;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi sao lưu MongoDB: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Khôi phục database MongoDB từ thư mục dump.</summary>
        private async Task RunMongoRestoreAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.MongoDb)
            {
                MessageBox.Show("Chức năng này chỉ dùng cho MongoDB.",
                    "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(profile.Database) || profile.Database == ConnectionEditor.DbPlaceholder)
            {
                MessageBox.Show("Hãy nhập/chọn tên database MongoDB đích ở ô Database.",
                    "Khôi phục MongoDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var folder = PickFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;
            var confirm = MessageBox.Show(
                $"Khôi phục database '{profile.Database}' từ thư mục:\n{folder}\n\nDữ liệu trùng _id có thể lỗi — tiếp tục?",
                "Xác nhận khôi phục", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            SetBusy(true);
            try
            {
                _lblStatus.Text = $"Đang mongorestore {profile.Database}...";
                var result = await new MongoDumpService().RestoreAsync(
                    string.IsNullOrWhiteSpace(_txtMongoRestore.Text) ? "mongorestore" : _txtMongoRestore.Text,
                    BuildProbe(profile), profile.Database, folder);
                _lblStatus.Text = result.Success
                    ? $"Khôi phục MongoDB xong trong {result.Elapsed}."
                    : "Khôi phục MongoDB thất bại: " + result.Error;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi khôi phục MongoDB: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// Nhóm 4: báo cáo routine cần xử lý tay (mover chỉ chép bảng + dữ liệu).
        /// Dùng cùng kết nối ở nhóm 1 làm nguồn, đích chọn ở combobox.
        /// </summary>
        private Control BuildRoutineGroup()
        {
            var group = new GroupBox
            {
                Text = "4. Báo cáo routine cần xử lý tay (view/procedure/function/trigger)",
                Dock = DockStyle.Fill,
                Padding = new Padding(6)
            };
            SetupGrid(_dgvRoutines, "Schema", "Routine", "Loại", "Độ dài", "Hướng dẫn");
            _cmbRoutineTarget.Items.Add("SQL Server");
            _cmbRoutineTarget.Items.Add("PostgreSQL");
            _cmbRoutineTarget.Items.Add("MySQL / MariaDB");
            _cmbRoutineTarget.Items.Add("SQLite");
            _cmbRoutineTarget.SelectedIndex = 2;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(_dgvRoutines, 0, 0);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row.Controls.Add(new Label { Text = "Engine đích:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            row.Controls.Add(_cmbRoutineTarget);
            row.Controls.Add(_btnRoutineReport);
            panel.Controls.Add(row, 0, 1);
            group.Controls.Add(panel);
            return group;
        }

        private static void SetupGrid(DataGridView grid, params string[] columns)
        {
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            foreach (var col in columns)
                grid.Columns.Add(col, col);
        }

        private void WireEvents()
        {
            _btnLoadDb.Click += async (_, _) => await LoadDatabasesAsync();
            _btnLoadSessions.Click += async (_, _) => await LoadSessionsAsync();
            _btnKill.Click += async (_, _) => await KillSelectedSessionAsync();
            _btnRoutineReport.Click += async (_, _) => await LoadRoutineReportAsync();
            _btnMongoBackup.Click += async (_, _) => await RunMongoBackupAsync();
            _btnMongoRestore.Click += async (_, _) => await RunMongoRestoreAsync();
        }

        private static DatabaseEngine RoutineTargetFromIndex(int index) => index switch
        {
            0 => DatabaseEngine.SqlServer,
            1 => DatabaseEngine.PostgreSql,
            3 => DatabaseEngine.Sqlite,
            _ => DatabaseEngine.MySql
        };

        /// <summary>Lập báo cáo routine nguồn + hướng dẫn viết lại theo engine đích.</summary>
        private async Task LoadRoutineReportAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            var src = EngineInfo.ParseEngine(profile.Engine);
            var dst = RoutineTargetFromIndex(_cmbRoutineTarget.SelectedIndex);
            if (src != DatabaseEngine.SqlServer && src != DatabaseEngine.PostgreSql && src != DatabaseEngine.MySql)
            {
                MessageBox.Show("Báo cáo routine mới hỗ trợ nguồn SQL Server, PostgreSQL, MySQL/MariaDB.",
                    "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                var service = new RoutineGuideService();
                var items = await service.ListRoutinesAsync(BuildProbe(profile), src, dst);
                _dgvRoutines.Rows.Clear();
                foreach (var item in items)
                {
                    _dgvRoutines.Rows.Add(item.Schema, item.Name, item.Kind,
                        item.DefinitionLength > 0 ? item.DefinitionLength.ToString("N0") : "-",
                        item.Guidance);
                }
                _lblStatus.Text = $"Báo cáo routine: {items.Count} mục cần xử lý tay (mover không tự chép routine).";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi lập báo cáo routine: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private IManageProvider? ResolveProvider(ConnectionProfile profile, out string refuseReason)
        {
            refuseReason = "";
            var engine = EngineInfo.ParseEngine(profile.Engine);
            if (engine == DatabaseEngine.SqlServer)
                return new SqlServerManageProvider();
            if (engine == DatabaseEngine.MySql)
                return new MySqlManageProvider();
            if (engine == DatabaseEngine.Sqlite)
                return new SqliteManageProvider();
            if (engine == DatabaseEngine.MongoDb)
                return new MongoManageProvider();
            refuseReason = $"Tab Quản trị bản này mới hỗ trợ SQL Server, MySQL/MariaDB, SQLite, MongoDB (đọc) "
                + $"(engine hiện tại: {EngineChoices.DisplayOf(profile.Engine)}).";
            return null;
        }

        private DbProbe BuildProbe(ConnectionProfile profile) => new()
        {
            Host = profile.Server,
            Port = profile.Port,
            Database = profile.Database,
            User = profile.UserName,
            Password = _editor.GetPlainPassword(),
            UseWindowsAuth = profile.Authentication == AuthenticationMode.Windows,
            TimeoutSeconds = 15
        };

        private async Task LoadDatabasesAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            var provider = ResolveProvider(profile, out var refuse);
            if (provider == null)
            {
                MessageBox.Show(refuse, "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                var dbs = await provider.ListDatabasesAsync(BuildProbe(profile));
                _dgvDatabases.Rows.Clear();
                foreach (var db in dbs)
                {
                    _dgvDatabases.Rows.Add(db.Name, db.State,
                        db.AllocatedMb.ToString("N1"), db.UsedMb < 0 ? "?" : db.UsedMb.ToString("N1"));
                }
                _lblStatus.Text = $"Đã nạp {dbs.Count} database.";
                // SQLite: kèm kiểm tra toàn vẹn file ngay sau khi nạp.
                if (provider is SqliteManageProvider lite)
                {
                    try
                    {
                        var verdict = await lite.CheckIntegrityAsync(BuildProbe(profile));
                        _lblStatus.Text += $" Toàn vẹn file: {verdict}.";
                    }
                    catch (Exception ex)
                    {
                        _lblStatus.Text += " Không kiểm tra được toàn vẹn: " + ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi nạp database: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LoadSessionsAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            var provider = ResolveProvider(profile, out var refuse);
            if (provider == null)
            {
                MessageBox.Show(refuse, "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                var sessions = await provider.ListSessionsAsync(BuildProbe(profile));
                _dgvSessions.Rows.Clear();
                foreach (var s in sessions)
                {
                    _dgvSessions.Rows.Add(s.Id, s.Login, s.Host, s.Database,
                        s.Status, s.Command, s.WaitInfo);
                }
                _lblStatus.Text = $"Đã nạp {sessions.Count} phiên.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi nạp phiên: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task KillSelectedSessionAsync()
        {
            if (_dgvSessions.SelectedRows.Count == 0)
            {
                MessageBox.Show("Hãy chọn một phiên trong lưới trước khi kill.",
                    "Kill session", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var cells = _dgvSessions.SelectedRows[0].Cells;
            var id = cells[0].Value?.ToString() ?? "";
            var login = cells[1].Value?.ToString() ?? "";
            var host = cells[2].Value?.ToString() ?? "";
            var db = cells[3].Value?.ToString() ?? "";
            var confirm = MessageBox.Show(
                $"Chắc chắn kill phiên Id={id} (đăng nhập {login}, máy {host}, DB {db})?\n"
                + "Phiên bị kill sẽ mất kết nối ngay.",
                "Xác nhận kill session", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            var profile = _editor.ReadProfile();
            if (profile == null) return;
            var provider = ResolveProvider(profile, out var refuse);
            if (provider == null)
            {
                MessageBox.Show(refuse, "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                var result = await provider.KillSessionAsync(BuildProbe(profile), id);
                if (!string.IsNullOrEmpty(result.Error))
                {
                    MessageBox.Show("Kill thất bại: " + result.Error,
                        "Kill session", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _lblStatus.Text = "Kill thất bại: " + result.Error;
                    return;
                }
                var verdict = result.Confirmed
                    ? $"đã xác nhận trạng thái đổi ({result.BeforeStatus} → {result.AfterStatus})."
                    : "lệnh đã gửi nhưng CHƯA xác nhận được trạng thái đổi — hãy nạp lại phiên để kiểm tra.";
                MessageBox.Show($"Đã gửi kill phiên {id}, {verdict}",
                    "Kill session", MessageBoxButtons.OK,
                    result.Confirmed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                _lblStatus.Text = $"Kill phiên {id}: {verdict}";
                await LoadSessionsAsync();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi kill session: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _btnLoadDb.Enabled = !busy;
            _btnLoadSessions.Enabled = !busy;
            _btnKill.Enabled = !busy;
            _btnRoutineReport.Enabled = !busy;
            _btnMongoBackup.Enabled = !busy;
            _btnMongoRestore.Enabled = !busy;
            _editor.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
