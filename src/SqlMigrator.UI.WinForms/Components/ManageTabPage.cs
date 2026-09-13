using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services;
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
        private readonly TextBox _txtSvcMachine = new() { Dock = DockStyle.Fill };
        private readonly ComboBox _cmbService = new() { Width = 320 };
        private readonly Button _btnSvcQuery = new() { Text = "Xem trạng thái", AutoSize = true };
        private readonly Label _lblSvcState = new() { Text = "Chưa kiểm tra.", AutoSize = true };
        private readonly Button _btnSvcStart = new() { Text = "Start", AutoSize = true };
        private readonly Button _btnSvcStop = new() { Text = "Stop", AutoSize = true };
        private readonly Button _btnSvcRestart = new() { Text = "Restart", AutoSize = true };
        private readonly TextBox _txtQuery = new();
        private readonly DataGridView _dgvQueryResult = new();
        private readonly Button _btnQueryRun = new() { Text = "Chạy truy vấn", AutoSize = true };
        private readonly Label _lblQueryStatus = new() { Text = "Tối đa 5000 dòng.", AutoSize = true };
        private readonly ConnectionEditor _cmpSource;
        private readonly ConnectionEditor _cmpDest;
        private readonly Button _btnCompare = new() { Text = "Đối chiếu ngay", AutoSize = true };
        private readonly DataGridView _dgvCompare = new();
        private readonly TextBox _txtSrvFolder = new() { Dock = DockStyle.Fill };
        private readonly Button _btnSrvBrowse = new() { Text = "Chọn…", AutoSize = true };
        private readonly Button _btnSrvList = new() { Text = "Liệt kê file", AutoSize = true };
        private readonly DataGridView _dgvSrvFiles = new();
        private readonly TextBox _txtLocalFolder = new() { Dock = DockStyle.Fill };
        private readonly Button _btnLocalBrowse = new() { Text = "Chọn…", AutoSize = true };
        private readonly Button _btnLocalList = new() { Text = "Nạp file", AutoSize = true };
        private readonly Button _btnLocalDelete = new() { Text = "Xóa file đã chọn", AutoSize = true };
        private readonly DataGridView _dgvLocalFiles = new();
        private readonly DataGridView _dgvOpsLog = new();
        private readonly Button _btnOpsReload = new() { Text = "Tải lại", AutoSize = true };

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

            _cmpSource = new ConnectionEditor(protector, profileStore, builder, "cmp-src");
            _cmpDest = new ConnectionEditor(protector, profileStore, builder, "cmp-dst");
            foreach (var cmp in new[] { _cmpSource, _cmpDest })
            {
                cmp.DatabaseFetcher = async p => await DatabaseCatalog.GetDatabasesAsync(p, _builder);
                cmp.ConnTester = async p => await DatabaseCatalog.TestConnectionAsync(p, _builder);
                cmp.LogSink = msg => _lblStatus.Text = msg;
                cmp.Dock = DockStyle.Fill;
            }

            BuildLayout();
            WireEvents();
            _ = RefreshOpsLogAsync();
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

            var middle = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 300 };
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            middle.Controls.Add(dbGroup, 0, 0);
            middle.Controls.Add(sessGroup, 1, 0);

            // Chồng các nhóm theo chiều dọc trong panel cuộn: thêm bao nhiêu nhóm
            // cũng không xén (bài học từ group Khôi phục tab Backup).
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            stack.Controls.Add(middle, 0, 0);
            var routine = BuildRoutineGroup();
            routine.Height = 260;
            routine.Dock = DockStyle.Top;
            stack.Controls.Add(routine, 0, 1);
            stack.Controls.Add(BuildServiceGroup(), 0, 2);
            stack.Controls.Add(BuildQueryGroup(), 0, 3);
            stack.Controls.Add(BuildMongoDumpGroup(), 0, 4);
            stack.Controls.Add(BuildCompareGroup(), 0, 5);
            stack.Controls.Add(BuildFileGroup(), 0, 6);
            stack.Controls.Add(BuildOpsLogGroup(), 0, 7);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroller.Controls.Add(stack);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(connGroup, 0, 0);
            root.Controls.Add(scroller, 0, 1);
            root.Controls.Add(_lblStatus, 0, 2);
            Controls.Add(root);
        }

        /// <summary>
        /// Nhóm vận hành service database: Start/Stop/Restart service Windows của
        /// SQL Server/PostgreSQL/MySQL/MongoDB (qua sc.exe, local hoặc \\máy).
        /// Cần quyền admin trên máy chứa service; lỗi hiện nguyên văn.
        /// </summary>
        private Control BuildServiceGroup()
        {
            var group = new GroupBox
            {
                Text = "6. Vận hành service database (Start / Stop / Restart)",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6)
            };
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.Controls.Add(new Label { Text = "Máy (trống = máy này):", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtSvcMachine, 1, 0);
            panel.Controls.Add(new Label { Text = "Service:", AutoSize = true }, 0, 1);
            var svcRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            foreach (var known in ServiceControlService.KnownServices)
                _cmbService.Items.Add(known.Name + " — " + known.Label);
            if (_cmbService.Items.Count > 0)
                _cmbService.SelectedIndex = 0;
            svcRow.Controls.Add(_cmbService);
            svcRow.Controls.Add(_btnSvcQuery);
            panel.Controls.Add(svcRow, 1, 1);
            panel.Controls.Add(new Label { Text = "Trạng thái:", AutoSize = true }, 0, 2);
            panel.Controls.Add(_lblSvcState, 1, 2);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row.Controls.Add(_btnSvcStart);
            row.Controls.Add(_btnSvcStop);
            row.Controls.Add(_btnSvcRestart);
            panel.Controls.Add(new Label { Text = "Điều khiển:", AutoSize = true }, 0, 3);
            panel.Controls.Add(row, 1, 3);
            group.Controls.Add(panel);
            return group;
        }

        /// <summary>Nhóm hộp truy vấn SQL + lưới kết quả (tối đa 5000 dòng).</summary>
        private Control BuildQueryGroup()
        {
            var group = new GroupBox
            {
                Text = "7. Truy vấn SQL",
                Dock = DockStyle.Top,
                Padding = new Padding(6),
                Height = 320
            };
            _txtQuery.Multiline = true;
            _txtQuery.ScrollBars = ScrollBars.Both;
            _txtQuery.Font = new Font("Consolas", 9.5F);
            _txtQuery.Dock = DockStyle.Fill;
            _txtQuery.Text = "SELECT 1;";
            _dgvQueryResult.Dock = DockStyle.Fill;
            _dgvQueryResult.AllowUserToAddRows = false;
            _dgvQueryResult.ReadOnly = true;
            _dgvQueryResult.RowHeadersVisible = false;
            _dgvQueryResult.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            split.Panel1.Controls.Add(_txtQuery);
            split.Panel2.Controls.Add(_dgvQueryResult);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(split, 0, 0);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            row.Controls.Add(_btnQueryRun);
            row.Controls.Add(_lblQueryStatus);
            panel.Controls.Add(row, 0, 1);
            group.Controls.Add(panel);
            return group;
        }

        /// <summary>
        /// Nhóm 8: đối chiếu nhanh 2 database (kể cả khác engine) — liệt kê bảng
        /// bên nguồn rồi đếm dòng từng bảng 2 bên. Chỉ đọc, không chép dữ liệu.
        /// </summary>
        private Control BuildCompareGroup()
        {
            var group = new GroupBox
            {
                Text = "8. Đối chiếu nhanh 2 database (số dòng từng bảng)",
                Dock = DockStyle.Top,
                Padding = new Padding(6),
                Height = 340
            };
            var srcBox = new GroupBox { Text = "Nguồn", Dock = DockStyle.Fill };
            srcBox.Controls.Add(_cmpSource);
            var dstBox = new GroupBox { Text = "Đích", Dock = DockStyle.Fill };
            dstBox.Controls.Add(_cmpDest);
            var editors = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 150 };
            editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            editors.Controls.Add(srcBox, 0, 0);
            editors.Controls.Add(dstBox, 1, 0);
            SetupGrid(_dgvCompare, "Bảng", "Dòng nguồn", "Dòng đích", "Khớp");
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(editors, 0, 0);
            panel.Controls.Add(_dgvCompare, 0, 1);
            panel.Controls.Add(_btnCompare, 0, 2);
            group.Controls.Add(panel);
            return group;
        }

        /// <summary>Chạy đối chiếu: DB lấy theo ô Database của từng khối kết nối.</summary>
        private async Task RunCompareAsync()
        {
            var src = _cmpSource.ReadProfile();
            var dst = _cmpDest.ReadProfile();
            if (src == null || dst == null) return;
            if (string.IsNullOrWhiteSpace(src.Database) || string.IsNullOrWhiteSpace(dst.Database)
                || src.Database == ConnectionEditor.DbPlaceholder
                || dst.Database == ConnectionEditor.DbPlaceholder)
            {
                MessageBox.Show("Hãy Connect DB và chọn database ở cả 2 khối nguồn/đích.",
                    "Đối chiếu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var srcEngine = EngineInfo.ParseEngine(src.Engine);
            var dstEngine = EngineInfo.ParseEngine(dst.Engine);
            if (MigrationGuard.EnsureSupportedEngines(src.Engine, dst.Engine) != null)
            {
                MessageBox.Show("Cặp engine này chưa hỗ trợ đối chiếu.",
                    "Đối chiếu", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                _lblStatus.Text = "Đang đối chiếu...";
                var service = new InventoryCompareService();
                var progress = new Progress<MigrationProgress>(p => _lblStatus.Text = p.Message);
                var rows = await service.CompareAsync(
                    BuildProbe(src, _cmpSource.GetPlainPassword()), srcEngine,
                    BuildProbe(dst, _cmpDest.GetPlainPassword()), dstEngine,
                    progress);
                _dgvCompare.Rows.Clear();
                var mismatched = 0;
                foreach (var row in rows)
                {
                    if (!row.Match) mismatched++;
                    _dgvCompare.Rows.Add(row.Table,
                        row.SourceRows < 0 ? "?" : row.SourceRows.ToString("N0"),
                        row.DestRows < 0 ? "?" : row.DestRows.ToString("N0"),
                        row.Match ? "Khớp" : "LỆCH");
                }
                _lblStatus.Text = $"Đối chiếu xong {rows.Count} bảng, {mismatched} lệch.";
                await LogOpsAsync("Đối chiếu nhanh",
                    $"{src.Server}/{src.Database} vs {dst.Server}/{dst.Database}",
                    $"{rows.Count} bảng, {mismatched} lệch.", mismatched == 0);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi đối chiếu: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// Nhóm 9: quản lý file backup — liệt kê file trên server (chỉ xem; xóa
        /// file server cần xp_cmdshell nên app khóa để an toàn) + file local đầy đủ.
        /// </summary>
        private Control BuildFileGroup()
        {
            var group = new GroupBox
            {
                Text = "9. Quản lý file backup",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6)
            };
            SetupGrid(_dgvSrvFiles, "File trên server");
            SetupGrid(_dgvLocalFiles, "File local", "Dung lượng (MB)", "Sửa đổi");
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label { Text = "Thư mục server:", AutoSize = true }, 0, 0);
            panel.Controls.Add(_txtSrvFolder, 1, 0);
            var srvRow = new FlowLayoutPanel { AutoSize = true };
            srvRow.Controls.Add(_btnSrvBrowse);
            srvRow.Controls.Add(_btnSrvList);
            panel.Controls.Add(srvRow, 2, 0);
            panel.Controls.Add(new Label { Text = "Thư mục máy này:", AutoSize = true }, 0, 1);
            panel.Controls.Add(_txtLocalFolder, 1, 1);
            var localRow = new FlowLayoutPanel { AutoSize = true };
            localRow.Controls.Add(_btnLocalBrowse);
            localRow.Controls.Add(_btnLocalList);
            localRow.Controls.Add(_btnLocalDelete);
            panel.Controls.Add(localRow, 2, 1);
            var grids = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 150 };
            grids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _dgvSrvFiles.Dock = DockStyle.Fill;
            _dgvLocalFiles.Dock = DockStyle.Fill;
            grids.Controls.Add(_dgvSrvFiles, 0, 0);
            grids.Controls.Add(_dgvLocalFiles, 1, 0);
            var outer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };
            outer.Controls.Add(panel, 0, 0);
            outer.Controls.Add(grids, 0, 1);
            group.Controls.Add(outer);
            return group;
        }

        /// <summary>Nhóm 10: lịch sử thao tác quản trị (kill/dump/truy vấn/service...).</summary>
        private Control BuildOpsLogGroup()
        {
            var group = new GroupBox
            {
                Text = "10. Lịch sử thao tác quản trị",
                Dock = DockStyle.Top,
                Padding = new Padding(6),
                Height = 220
            };
            SetupGrid(_dgvOpsLog, "Thời gian", "Thao tác", "Đối tượng", "Chi tiết", "Kết quả");
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(_dgvOpsLog, 0, 0);
            panel.Controls.Add(_btnOpsReload, 0, 1);
            group.Controls.Add(panel);
            return group;
        }

        private async Task RefreshOpsLogAsync()
        {
            try
            {
                var entries = await OpsLogStore.LoadAsync();
                _dgvOpsLog.Rows.Clear();
                foreach (var e in entries)
                {
                    _dgvOpsLog.Rows.Add(
                        e.AtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                        e.Action, e.Target, e.Detail,
                        e.Success ? "OK" : "LỖI");
                }
            }
            catch
            {
            }
        }

        /// <summary>Liệt kê file .bak trên server qua xp_dirtree (chỉ xem).</summary>
        private async Task ListServerFilesAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.SqlServer)
            {
                MessageBox.Show("Duyệt file server mới hỗ trợ SQL Server.",
                    "Chưa hỗ trợ", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var folder = _txtSrvFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show("Nhập hoặc bấm 'Chọn…' để lấy thư mục trên server.",
                    "File backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                string cs;
                try
                {
                    cs = _builder.Build(profile);
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = "Không dựng được kết nối: " + ex.Message;
                    return;
                }
                var files = await new ServerFolderService().GetChildFilesAsync(cs, folder);
                _dgvSrvFiles.Rows.Clear();
                foreach (var f in files)
                    _dgvSrvFiles.Rows.Add(f);
                _lblStatus.Text = $"Thư mục server có {files.Count} file (chỉ xem — app không xóa file server).";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi liệt kê file server: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Nạp file local (kèm dung lượng/ngày sửa) + xóa file đã chọn.</summary>
        private void LoadLocalFiles()
        {
            var folder = _txtLocalFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
            {
                MessageBox.Show("Chọn thư mục local tồn tại trước.",
                    "File backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _dgvLocalFiles.Rows.Clear();
            foreach (var file in System.IO.Directory.GetFiles(folder))
            {
                try
                {
                    var info = new System.IO.FileInfo(file);
                    _dgvLocalFiles.Rows.Add(info.Name,
                        (info.Length / 1048576.0).ToString("N1"),
                        info.LastWriteTime.ToString("dd/MM/yyyy HH:mm"));
                    _dgvLocalFiles.Rows[_dgvLocalFiles.Rows.Count - 1].Tag = info.FullName;
                }
                catch
                {
                }
            }
            _lblStatus.Text = $"Đã nạp {_dgvLocalFiles.Rows.Count} file local.";
        }

        private void DeleteLocalFile()
        {
            if (_dgvLocalFiles.SelectedRows.Count == 0) return;
            var path = _dgvLocalFiles.SelectedRows[0].Tag as string ?? "";
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
            if (MessageBox.Show($"Chắc chắn xóa file local?\n{path}",
                "Xóa file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                System.IO.File.Delete(path);
                _ = LogOpsAsync("Xóa file backup", path, "Xóa file local.", true);
                LoadLocalFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không xóa được: " + ex.Message,
                    "Xóa file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
                await LogOpsAsync("Sao lưu MongoDB", $"{profile.Server}/{profile.Database}",
                    result.Success ? $"xong trong {result.Elapsed}." : result.Error ?? "lỗi",
                    result.Success);
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
                await LogOpsAsync("Khôi phục MongoDB", $"{profile.Server}/{profile.Database}",
                    result.Success ? $"xong trong {result.Elapsed}." : result.Error ?? "lỗi",
                    result.Success);
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
                Dock = DockStyle.Top,
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
            _btnSvcQuery.Click += async (_, _) => await RefreshServiceStateAsync();
            _btnSvcStart.Click += async (_, _) => await ControlServiceAsync("start");
            _btnSvcStop.Click += async (_, _) => await ControlServiceAsync("stop");
            _btnSvcRestart.Click += async (_, _) => await ControlServiceAsync("restart");
            _btnQueryRun.Click += async (_, _) => await RunQueryAsync();
            _btnCompare.Click += async (_, _) => await RunCompareAsync();
            _btnSrvBrowse.Click += (_, _) => BrowseServerFolder();
            _btnSrvList.Click += async (_, _) => await ListServerFilesAsync();
            _btnLocalBrowse.Click += (_, _) => BrowseLocalFolder();
            _btnLocalList.Click += (_, _) => LoadLocalFiles();
            _btnLocalDelete.Click += (_, _) => DeleteLocalFile();
            _btnOpsReload.Click += async (_, _) => await RefreshOpsLogAsync();
        }

        /// <summary>Chọn thư mục trên server bằng dialog duyệt disk (điền vào ô).</summary>
        private void BrowseServerFolder()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            if (EngineInfo.ParseEngine(profile.Engine) != DatabaseEngine.SqlServer) return;
            string cs;
            try
            {
                cs = _builder.Build(profile);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Không dựng được kết nối: " + ex.Message;
                return;
            }
            var service = new ServerFolderService(m => _lblStatus.Text = m);
            using var dlg = new ServerFolderPickerDialog(service, cs, CancellationToken.None, "đích");
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                _txtSrvFolder.Text = dlg.SelectedPath.Trim();
        }

        private void BrowseLocalFolder()
        {
            using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục local chứa file backup" };
            if (dlg.ShowDialog() == DialogResult.OK)
                _txtLocalFolder.Text = dlg.SelectedPath;
        }

        private string SelectedServiceName()
        {
            var text = _cmbService.Text.Trim();
            var sep = text.IndexOf(" — ", StringComparison.Ordinal);
            return sep > 0 ? text.Substring(0, sep).Trim() : text;
        }

        /// <summary>Xem trạng thái service hiện tại (local hoặc máy remote).</summary>
        private async Task RefreshServiceStateAsync()
        {
            var name = SelectedServiceName();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Nhập/chọn tên service.", "Service",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                var info = await new ServiceControlService().QueryAsync(_txtSvcMachine.Text, name);
                _lblSvcState.Text = info.State == WindowsServiceState.Unknown
                    ? $"Không đọc được service '{name}' (sai tên/máy hoặc thiếu quyền)."
                    : $"{info.DisplayName}: {info.State}.";
            }
            catch (Exception ex)
            {
                _lblSvcState.Text = "Lỗi: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Start/Stop/Restart service (luôn hỏi xác nhận vì ảnh hưởng toàn server).</summary>
        private async Task ControlServiceAsync(string action)
        {
            var name = SelectedServiceName();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Nhập/chọn tên service.", "Service",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var verb = action == "start" ? "START" : action == "stop" ? "STOP" : "RESTART";
            var machine = _txtSvcMachine.Text.Trim();
            var where = string.IsNullOrWhiteSpace(machine) ? "máy này" : "máy " + machine;
            if (MessageBox.Show($"Chắc chắn {verb} service '{name}' trên {where}?\n"
                + "Mọi kết nối tới service sẽ bị ảnh hưởng.",
                $"Xác nhận {verb} service", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes)
                return;
            SetBusy(true);
            try
            {
                var svc = new ServiceControlService();
                ServiceCommandResult result = action == "start"
                    ? await svc.StartAsync(machine, name)
                    : action == "stop"
                    ? await svc.StopAsync(machine, name)
                    : await svc.RestartAsync(machine, name);
                _lblSvcState.Text = result.Success
                    ? $"Đã gửi lệnh {verb} '{name}'."
                    : $"{verb} thất bại: {result.Output}";
                if (!result.Success)
                    MessageBox.Show(result.Output, verb + " thất bại",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    await RefreshServiceStateAsync();
                await LogOpsAsync(verb + " service", (string.IsNullOrWhiteSpace(machine) ? "máy này" : machine) + "/" + name,
                    result.Success ? "đã gửi lệnh." : result.Output, result.Success);
            }
            catch (Exception ex)
            {
                _lblSvcState.Text = "Lỗi: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Chạy truy vấn trên engine của kết nối nhóm 1 (trừ MongoDB).</summary>
        private async Task RunQueryAsync()
        {
            var profile = _editor.ReadProfile();
            if (profile == null) return;
            var engine = EngineInfo.ParseEngine(profile.Engine);
            if (engine == DatabaseEngine.MongoDb)
            {
                MessageBox.Show("MongoDB không dùng SQL — hộp truy vấn không áp dụng.",
                    "Truy vấn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (engine != DatabaseEngine.SqlServer && engine != DatabaseEngine.MySql
                && engine != DatabaseEngine.PostgreSql && engine != DatabaseEngine.Sqlite)
            {
                MessageBox.Show("Engine này chưa hỗ trợ truy vấn.",
                    "Truy vấn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                _lblQueryStatus.Text = "Đang chạy...";
                var result = await new QueryRunnerService().ExecuteAsync(
                    BuildProbe(profile), engine, _txtQuery.Text);
                if (!result.Success)
                {
                    _lblQueryStatus.Text = "Lỗi: " + result.Error;
                    return;
                }
                if (result.Table != null)
                {
                    _dgvQueryResult.Columns.Clear();
                    _dgvQueryResult.DataSource = result.Table;
                    _lblQueryStatus.Text = $"Xong trong {result.Elapsed.TotalSeconds:N1}s: "
                        + $"{result.Table.Rows.Count} dòng"
                        + (result.Truncated ? " (đã cắt ở 5000 dòng)" : "") + ".";
                    await LogOpsAsync("Truy vấn", profile.Server,
                        $"SELECT {result.Table.Rows.Count} dòng trong {result.Elapsed.TotalSeconds:N1}s.", true);
                }
                else
                {
                    _lblQueryStatus.Text = $"Xong trong {result.Elapsed.TotalSeconds:N1}s: "
                        + $"{result.RowsAffected} dòng ảnh hưởng.";
                    await LogOpsAsync("Truy vấn", profile.Server,
                        $"lệnh ghi: {result.RowsAffected} dòng ảnh hưởng.", true);
                }
            }
            catch (Exception ex)
            {
                _lblQueryStatus.Text = "Lỗi: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
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

        private DbProbe BuildProbe(ConnectionProfile profile, string? plainPassword = null) => new()
        {
            Host = profile.Server,
            Port = profile.Port,
            Database = profile.Database,
            User = profile.UserName,
            Password = plainPassword ?? _editor.GetPlainPassword(),
            UseWindowsAuth = profile.Authentication == AuthenticationMode.Windows,
            TimeoutSeconds = 15
        };

        /// <summary>Ghi một dòng lịch sử thao tác (không secret, không toàn văn SQL).</summary>
        private static Task LogOpsAsync(string action, string target, string detail, bool success)
        {
            return OpsLogStore.AppendAsync(new OpsLogEntry
            {
                Action = action,
                Target = target,
                Detail = detail,
                Success = success
            });
        }

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
                await LogOpsAsync("Kill session", $"{profile.Server} Id={id} ({login})",
                    verdict, result.Confirmed);
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
            _btnSvcQuery.Enabled = !busy;
            _btnSvcStart.Enabled = !busy;
            _btnSvcStop.Enabled = !busy;
            _btnSvcRestart.Enabled = !busy;
            _btnQueryRun.Enabled = !busy;
            _btnCompare.Enabled = !busy;
            _btnSrvList.Enabled = !busy;
            _btnLocalList.Enabled = !busy;
            _btnLocalDelete.Enabled = !busy;
            _editor.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
