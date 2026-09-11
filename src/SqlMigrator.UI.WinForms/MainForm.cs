using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services;
using SqlMigrator.UI.Components;
using SqlMigrator.UI.Models;
using SqlMigrator.UI.Services;

namespace SqlMigrator.UI
{
    /// <summary>
    /// Cửa sổ chính — mọi thao tác trên MỘT form tổng thể:
    /// kết nối nguồn/đích, tùy chọn, chọn bảng và khung nhật ký real-time.
    /// Tuân thủ bảo mật: không hiển thị chuỗi kết nối hay mật khẩu; mật khẩu mã hóa DPAPI.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly IDataProtector _protector;
        private readonly IConnectionProfileStore _profileStore;
        private readonly SecureConnectionStringBuilder _builder;

        private readonly ConnectionEditor _sourceEditor;
        private readonly ConnectionEditor _destEditor;

        private readonly RadioButton _rdoFull = new() { Text = "Toàn bộ" };
        private readonly RadioButton _rdoSchema = new() { Text = "Chỉ cấu trúc" };
        private readonly RadioButton _rdoData = new() { Text = "Chỉ dữ liệu" };
        private readonly CheckBox _chkDisableFk = new() { Text = "Vô hiệu hóa khóa ngoại khi đổ dữ liệu", Checked = true };
        private readonly CheckBox _chkDisableCheck = new() { Text = "Vô hiệu hóa ràng buộc CHECK", Checked = true };
        private readonly CheckBox _chkDisableTriggers = new() { Text = "Vô hiệu hóa trigger của bảng" };
        private readonly CheckBox _chkDisableIndexes = new() { Text = "Vô hiệu hóa index phụ khi đổ (rebuid sau)", Checked = true };
        private readonly CheckBox _chkPreserveIdentity = new() { Text = "Giữ nguyên giá trị identity nguồn" };
        private readonly CheckBox _chkCheckData = new() { Text = "Kiểm tra dữ liệu khi bật ràng buộc", Checked = true };
        private readonly CheckBox _chkContinueOnError = new() { Text = "Tiếp tục khi lỗi không nghiêm trọng", Checked = true };
        private readonly CheckBox _chkFailFast = new() { Text = "Dừng ngay khi gặp lỗi đầu tiên" };
        private readonly CheckBox _chkTruncate = new() { Text = "Xóa dữ liệu hiện có trên đích trước khi đổ" };
        private readonly CheckBox _chkUseInternalTransaction = new() { Text = "Mỗi đợt dùng transaction riêng" };
        private readonly CheckBox _chkCopyPermissions = new() { Text = "Sao chép quyền (permissions)" };
        private readonly CheckBox _chkServerTriggers = new() { Text = "Sao chép trigger cấp server", Checked = true };
        private readonly CheckBox _chkCleanup = new() { Text = "Tự dọn bảng đổ dở khi gặp lỗi", Checked = true };
        private readonly CheckBox _chkForceUnsupported = new() { Text = "Ép thử tạo đối tượng feature đích không hỗ trợ (warning + skip nếu thất bại)" };
        private readonly CheckBox _chkCheckAll = new() { Text = "Chọn hết tùy chọn", Checked = false, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        private readonly NumericUpDown _numBatchSize = new();
        private readonly NumericUpDown _numCmdTimeout = new();
        private readonly NumericUpDown _numBulkTimeout = new();

        // Engine dữ liệu lớn
        private readonly NumericUpDown _numMaxParallelism = new();
        private readonly NumericUpDown _numMaxBufferMB = new();
        private readonly NumericUpDown _numChunkRowCount = new();
        private readonly CheckBox _chkKeysetChunking = new() { Text = "Dùng chunk keyset (seek index)", Checked = true };
        private readonly CheckBox _chkEnableRcsi = new() { Text = "Bật READ_COMMITTED_SNAPSHOT trên nguồn (ghi lên nguồn!)", Checked = false };
        private readonly CheckBox _chkSwitchRecovery = new() { Text = "Chuyển đích sang BULK_LOGGED khi đổ", Checked = true };
        private readonly CheckBox _chkTransferCheckpoint = new() { Text = "Bật checkpoint hồi tục", Checked = true };
        private readonly CheckBox _chkEstimatedCounts = new() { Text = "Ước lượng số dòng (sys.partitions)", Checked = true };
        private readonly CheckBox _chkVerifyAfterMigration = new() { Text = "Tự kiểm tra toàn vẹn dữ liệu đích sau khi di chuyển", Checked = true };

        private readonly DataGridView _dgvTables = new();
        private readonly Label _lblTablesStatus = new() { Text = "Chưa có dữ liệu bảng." };
        private readonly Button _btnLoadTables = new() { Text = "Connect DB" };
        private readonly Button _btnSelectAll = new() { Text = "Chọn tất cả" };
        private readonly Button _btnSelectNone = new() { Text = "Bỏ chọn" };

        private readonly DataGridView _dgvObjects = new();
        private readonly Label _lblObjectsStatus = new() { Text = "Chưa nạp danh sách đối tượng (SP/function/view/trigger)." };
        private readonly Button _btnLoadObjects = new() { Text = "Nạp danh sách đối tượng" };
        private readonly Button _btnObjectsSelectAll = new() { Text = "Chọn tất cả" };
        private readonly Button _btnObjectsSelectNone = new() { Text = "Bỏ chọn" };
        private readonly TabControl _objectsTab = new();

        private readonly Button _btnUpdateNew = new() { Text = "Cập nhật dữ liệu mới (New)", Enabled = false };
        private readonly Button _btnUpdateFull = new() { Text = "Cập nhật đầy đủ (Full)", Enabled = false };
        private readonly Button _btnPreflight = new() { Text = "Kiểm tra trước khi chạy" };
        private readonly Button _btnVerify = new() { Text = "Verify DB" };
        private readonly Button _btnReconcile = new() { Text = "Đồng bộ 100%" };
        private readonly Button _btnStart = new() { Text = "Bắt đầu di chuyển" };
        private readonly Button _btnCancel = new() { Text = "Hủy", Enabled = false };
        private readonly Button _btnExportLog = new() { Text = "Xuất nhật ký…" };
        private readonly Button _btnClearLog = new() { Text = "Xóa nhật ký" };
        private readonly Button _btnCheckUpdate = new() { Text = "Kiểm tra cập nhật" };
        private readonly Label _lblVersion = new()
        {
            Text = "",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 100)
        };
        private readonly RichTextBox _txtLog = new();
        private readonly ProgressBar _progressBar = new() { Style = ProgressBarStyle.Continuous, Maximum = 100, Minimum = 0 };
        private readonly Label _lblStatus = new() { Text = "Sẵn sàng.", AutoSize = true };
        private readonly Label _lblPreflight = new()
        {
            Text = "Chưa kiểm tra trước khi chạy.",
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 90, 0)
        };

        private readonly Label _lblSpeed = new()
        {
            Text = "",
            AutoSize = true,
            ForeColor = Color.FromArgb(60, 60, 60),
            Font = new Font("Consolas", 9F)
        };

        private PreflightResult? _lastPreflight;
        private SyncCheckResult? _lastSyncCheck;
        private CancellationTokenSource? _cts;

        public MainForm(IDataProtector protector, IConnectionProfileStore profileStore, SecureConnectionStringBuilder builder)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));

            _sourceEditor = new ConnectionEditor(protector, profileStore, builder, "source");
            _destEditor = new ConnectionEditor(protector, profileStore, builder, "dest");

            // Gán delegate để nút "Connect DB" và "Kiểm tra kết nối" hoạt động, có log realtime.
            var fetcher = new Func<ConnectionProfile, Task<List<string>>>(
                async p => await DatabaseCatalog.GetDatabasesAsync(p, _builder, log: AppendLog));
            var tester = new Func<ConnectionProfile, Task>(
                async p => await DatabaseCatalog.TestConnectionAsync(p, _builder, log: AppendLog));

            _sourceEditor.DatabaseFetcher = fetcher;
            _sourceEditor.ConnTester = tester;
            _destEditor.DatabaseFetcher = fetcher;
            _destEditor.ConnTester = tester;
            _sourceEditor.LogSink = AppendLog;
            _destEditor.LogSink = AppendLog;

            // Chỉ server đích có thêm 2 ô chọn thư mục chứa file database khi tạo DB mới.
            _destEditor.ShowDbFileFields = true;
            _destEditor.FolderPicker = (editor, ct) =>
            {
                var profile = editor.ReadProfile();
                if (profile == null)
                {
                    MessageBox.Show("Vui lòng nhập đầy đủ thông tin server đích trước khi chọn thư mục.",
                        "Chọn thư mục", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return Task.FromResult(string.Empty);
                }

                var service = new ServerFolderService(m => AppendLog("[THƯ MỤC] " + m));
                var dialog = new ServerFolderPickerDialog(service, editor.Builder.Build(profile), ct);
                return Task.FromResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : string.Empty);
            };

            // Nút "Tạo DB" tạo database đích ngay trên server đích (dùng Core DatabaseProvisioner),
            // trả về chuỗi rỗng khi thành công; mọi secret đều được che lại trước khi hiện.
            _destEditor.CreateDatabaseHandler = async (editor, ct) =>
            {
                var destProfile = editor.ReadProfile();
                if (destProfile == null)
                    return "Vui lòng nhập đầy đủ thông tin server đích (server, xác thực, tên database mới).";
                var sourceProfile = _sourceEditor.ReadProfile();
                if (sourceProfile == null)
                    return "Vui lòng nhập thông tin server nguồn (dùng để lấy collation cho database đích).";

                var options = new WizardState
                {
                    Source = sourceProfile,
                    Destination = destProfile,
                    CreateDestinationDatabase = true,
                    DestinationDataFileDirectory = string.IsNullOrWhiteSpace(editor.DestinationDataPath) ? null : editor.DestinationDataPath,
                    DestinationLogFileDirectory = string.IsNullOrWhiteSpace(editor.DestinationLogPath) ? null : editor.DestinationLogPath,
                    CommandTimeoutSeconds = (int)_numCmdTimeout.Value
                }.BuildMigrationOptions();
                options.SourceConnectionString = _builder.Build(sourceProfile);
                options.DestinationConnectionString = _builder.Build(destProfile);

                try
                {
                    var ok = await new DatabaseProvisioner(options, new UiLogger(AppendLog, "Migrator"))
                        .CreateIfMissingAsync(ct);
                    return ok
                        ? string.Empty
                        : "Không tạo được database đích (xem chi tiết trong nhật ký).";
                }
                catch (Exception ex)
                {
                    return SqlConnectionFactory.ScrubConnectionStringSecrets(ex.Message);
                }
            };

            ConfigureForm();
            StyleButtons();
            BuildLayout();
            WireEvents();
        }

        /// <summary>
        /// Đồng bộ kích thước/nền cho mọi nút bấm: chiều cao cố định 32, tự co giãn theo
        /// văn bản để không bao giờ bị cắt chữ, và nền hệ thống thống nhất.
        /// </summary>
        private void StyleButtons()
        {
            foreach (var button in new[]
            {
                _btnUpdateNew, _btnUpdateFull, _btnPreflight, _btnVerify, _btnReconcile, _btnStart, _btnCancel,
                _btnExportLog, _btnClearLog, _btnCheckUpdate, _btnLoadTables, _btnSelectAll, _btnSelectNone,
                _btnLoadObjects, _btnObjectsSelectAll, _btnObjectsSelectNone
            })
            {
                button.Height = 32;
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.Padding = new Padding(10, 0, 10, 0);
                button.Margin = new Padding(3);
                button.UseVisualStyleBackColor = true;
            }
        }

        private void ConfigureForm()
        {
            Text = "SQL Migrator — Di chuyển database SQL Server";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1120, 760);
            Size = new Size(1280, 900);
            Font = new Font("Segoe UI", 9.25F);
            Icon = LoadAppIcon();
        }

        private void BuildLayout()
        {
            // SplitContainer ngang: vùng nhập liệu/thiết lập ở trên, nhật ký ở dưới —
            // thanh kéo giữa để người dùng phóng to khung nhật ký tùy ý.
            // Không dùng FixedPanel: cả 2 panel đều grow theo form để luôn cân đối.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.None,
                SplitterWidth = 6,
                Panel1MinSize = 0,
                Panel2MinSize = 0
            };
            split.Panel1.Controls.Add(BuildInputsPanel());
            split.Panel2.Controls.Add(BuildLogPanel());

            Controls.Add(split);
            _logSplitter = split;

            // Tính SplitterDistance theo tỉ lệ 60/40 (Panel1 = 60% vùng trên).
            Shown += (_, _) =>
            {
                split.Panel1MinSize = 420;
                split.Panel2MinSize = 160;
                var usable = ClientSize.Height - split.SplitterWidth;
                var target = (int)(usable * 0.60);
                var min = split.Panel1MinSize;
                var max = Math.Max(min, usable - split.Panel2MinSize);
                split.SplitterDistance = Math.Clamp(target, min, max);
            };
        }

        private SplitContainer? _logSplitter;

        private Control BuildInputsPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8) };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));

            var connections = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            connections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            connections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            var sourceGroup = new GroupBox { Text = "1. Server nguồn", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _sourceEditor.Dock = DockStyle.Fill;
            sourceGroup.Controls.Add(_sourceEditor);

            var destGroup = new GroupBox { Text = "2. Server đích", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _destEditor.Dock = DockStyle.Fill;
            destGroup.Controls.Add(_destEditor);

            connections.Controls.Add(sourceGroup, 0, 0);
            connections.Controls.Add(destGroup, 1, 0);

            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            middle.Controls.Add(BuildOptionsGroup(), 0, 0);
            middle.Controls.Add(BuildTablesGroup(), 1, 0);

            panel.Controls.Add(connections, 0, 0);
            panel.Controls.Add(middle, 0, 1);
            return panel;
        }

        private Control BuildOptionsGroup()
        {
            var group = new GroupBox { Text = "3. Tùy chọn di chuyển", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var tooltip = new ToolTip();

            var modePanel = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = false, AutoSize = true };
            modePanel.Controls.Add(new Label { Text = "Chế độ:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            _rdoFull.Checked = true;
            _rdoFull.AutoSize = true;
            _rdoSchema.AutoSize = true;
            _rdoData.AutoSize = true;
            modePanel.Controls.Add(_rdoFull);
            modePanel.Controls.Add(_rdoSchema);
            modePanel.Controls.Add(_rdoData);

            var flags = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            flags.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            flags.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            var leftChecks = new Control[] { _chkDisableFk, _chkDisableCheck, _chkDisableTriggers, _chkDisableIndexes, _chkPreserveIdentity, _chkCheckData, _chkTruncate };
            var rightChecks = new Control[] { _chkContinueOnError, _chkFailFast, _chkUseInternalTransaction, _chkCopyPermissions, _chkServerTriggers, _chkCleanup, _chkForceUnsupported };

            for (var i = 0; i < leftChecks.Length; i++)
            {
                leftChecks[i].AutoSize = true;
                flags.Controls.Add(leftChecks[i], 0, i);
            }
            for (var i = 0; i < rightChecks.Length; i++)
            {
                rightChecks[i].AutoSize = true;
                flags.Controls.Add(rightChecks[i], 1, i);
            }

var numbers = new FlowLayoutPanel { Dock = DockStyle.Bottom, WrapContents = false, AutoSize = true };
            numbers.Controls.Add(new Label { Text = "Số dòng/đợt:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            _numBatchSize.Minimum = 100;
            _numBatchSize.Maximum = 5000000;
            _numBatchSize.Value = 5000;
            _numBatchSize.Increment = 1000;
            _numBatchSize.Width = 110;
            numbers.Controls.Add(_numBatchSize);

            numbers.Controls.Add(new Label { Text = "Thời gian chờ lệnh (giây):", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
            _numCmdTimeout.Minimum = 1;
            _numCmdTimeout.Maximum = 86400;
            _numCmdTimeout.Value = 600;
            _numCmdTimeout.Width = 90;
            numbers.Controls.Add(_numCmdTimeout);

            numbers.Controls.Add(new Label { Text = "Chờ bulk copy (giây):", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
            _numBulkTimeout.Maximum = 86400;
            _numBulkTimeout.Width = 90;
            numbers.Controls.Add(_numBulkTimeout);

            // Engine dữ liệu lớn
            var engine = new FlowLayoutPanel { Dock = DockStyle.Bottom, WrapContents = true, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            engine.Controls.Add(new Label { Text = "— Engine dữ liệu lớn —", AutoSize = true, Padding = new Padding(0, 10, 0, 0), Font = new Font("Segoe UI", 9.25F, FontStyle.Bold) });

            engine.Controls.Add(new Label { Text = "Luồng song song (0=tự động):", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
            _numMaxParallelism.Minimum = -1;
            _numMaxParallelism.Maximum = 16;
            _numMaxParallelism.Value = -1;
            _numMaxParallelism.Width = 70;
            engine.Controls.Add(_numMaxParallelism);

            engine.Controls.Add(new Label { Text = "Đệm RAM (MB):", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
            _numMaxBufferMB.Minimum = 16;
            _numMaxBufferMB.Maximum = 4096;
            _numMaxBufferMB.Value = 128;
            _numMaxBufferMB.Width = 70;
            engine.Controls.Add(_numMaxBufferMB);

            engine.Controls.Add(new Label { Text = "Dòng/chunk (0=tự):", AutoSize = true, Padding = new Padding(10, 5, 0, 0) });
            _numChunkRowCount.Minimum = 0;
            _numChunkRowCount.Maximum = 1000000;
            _numChunkRowCount.Value = 0;
            _numChunkRowCount.Width = 70;
            engine.Controls.Add(_numChunkRowCount);

            _chkKeysetChunking.AutoSize = true;
            _chkKeysetChunking.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkKeysetChunking);

            _chkEnableRcsi.AutoSize = true;
            _chkEnableRcsi.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkEnableRcsi);

            _chkSwitchRecovery.AutoSize = true;
            _chkSwitchRecovery.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkSwitchRecovery);

            _chkTransferCheckpoint.AutoSize = true;
            _chkTransferCheckpoint.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkTransferCheckpoint);

            _chkEstimatedCounts.AutoSize = true;
            _chkEstimatedCounts.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkEstimatedCounts);

            _chkVerifyAfterMigration.AutoSize = true;
            _chkVerifyAfterMigration.Padding = new Padding(10, 2, 0, 0);
            engine.Controls.Add(_chkVerifyAfterMigration);

            // Check All: gạt trạng thái cho toàn bộ checkbox trên toàn giao diện.
            _chkCheckAll.AutoSize = true;
            _chkCheckAll.Padding = new Padding(10, 2, 0, 0);
            _chkCheckAll.CheckedChanged += (_, _) =>
            {
                var allChecked = _chkCheckAll.Checked;
                SetAllCheckBoxes(allChecked);
            };
            engine.Controls.Add(_chkCheckAll);

            tooltip.SetToolTip(_numMaxParallelism, "Số luồng tối đa xử lý song song các bảng độc lập (theo phụ thuộc FK). -1 = min(CPU, 4).");
            tooltip.SetToolTip(_numMaxBufferMB, "Ngân sách bộ nhớ tối đa cho toàn bộ engine; chia cho các worker theo bề rộng dòng ước lượng.");
            tooltip.SetToolTip(_numChunkRowCount, "Số dòng tối đa mỗi chunk keyset. 0 = tự chọn theo ngân sách RAM.");
            tooltip.SetToolTip(_chkKeysetChunking, "Đọc theo WHERE key > @mốc (seek index) thay vì scan toàn bảng. Tránh khóa dài & spool tempdb.");
            tooltip.SetToolTip(_chkEnableRcsi, "CẢNH BÁO: chạy ALTER DATABASE trên nguồn (thao tác GHI lên nguồn, cần quyền ALTER DATABASE, cần exclusive access). Mặc định TẮT để giữ nguồn chỉ đọc.");
            tooltip.SetToolTip(_chkSwitchRecovery, "Tạm chuyển recovery model đích sang BULK_LOGGED trong lúc đổ dữ liệu lớn để giảm log; khôi phục sau khi xong.");
            tooltip.SetToolTip(_chkTransferCheckpoint, "Ghi mốc chunk vào file để lần chạy sau tiếp tục từ nơi dừng thay vì đổ lại.");
            tooltip.SetToolTip(_chkEstimatedCounts, "Dùng sys.partitions để ước lượng số dòng nhanh (để tính ETA) thay vì COUNT_BIG chậm trên bảng lớn.");
            tooltip.SetToolTip(_chkVerifyAfterMigration, "Sau khi di chuyển xong, tự so sánh số dòng (COUNT_BIG) và checksum nội dung từng bảng giữa đích và nguồn.");

            // Xếp các khối tùy chọn theo dòng bằng TableLayoutPanel để thứ tự luôn đúng
            // (mode → flags → numbers → engine → Check All), không phụ thuộc z-order dock.
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            modePanel.Dock = DockStyle.Fill;
            flags.Dock = DockStyle.Fill;
            numbers.Dock = DockStyle.Fill;
            engine.Dock = DockStyle.Fill;

            stack.Controls.Add(modePanel, 0, 0);
            stack.Controls.Add(flags, 0, 1);
            stack.Controls.Add(numbers, 0, 2);
            stack.Controls.Add(engine, 0, 3);

            // Bọc trong panel cuộn dọc để mọi tùy chọn luôn nhìn thấy dù form nhỏ.
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BorderStyle = BorderStyle.None };
            scroller.Controls.Add(stack);
            group.Controls.Add(scroller);

            return group;
        }

        /// <summary>Gạt trạng thái toàn bộ checkbox tùy chọn trên giao diện (trừ chính checkbox Check All).</summary>
        private void SetAllCheckBoxes(bool state)
        {
            foreach (var field in typeof(MainForm).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            {
                if (field.FieldType != typeof(CheckBox))
                    continue;
                var box = (CheckBox?)field.GetValue(this);
                if (box == null || ReferenceEquals(box, _chkCheckAll))
                    continue;
                box.Checked = state;
            }
        }

        private Control BuildTablesGroup()
        {
            var group = new GroupBox { Text = "4. Bảng & đối tượng cần di chuyển", Dock = DockStyle.Fill, Padding = new Padding(8) };

            _objectsTab.Dock = DockStyle.Fill;

            // Tab 1: bảng
            var tablesPage = new TabPage("Bảng") { Padding = new Padding(4) };
            _dgvTables.Dock = DockStyle.Fill;
            _dgvTables.AllowUserToAddRows = false;
            _dgvTables.AllowUserToDeleteRows = false;
            _dgvTables.ReadOnly = false;
            _dgvTables.MultiSelect = false;
            _dgvTables.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _dgvTables.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dgvTables.BackgroundColor = Color.White;

            var selectCol = new DataGridViewCheckBoxColumn { HeaderText = "Chọn", Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
            var nameCol = new DataGridViewTextBoxColumn { HeaderText = "Bảng (Schema.Tên)", ReadOnly = true };
            _dgvTables.Columns.Add(selectCol);
            _dgvTables.Columns.Add(nameCol);

            var tablesActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, WrapContents = true, AutoSize = true };
            tablesActions.Controls.Add(_btnLoadTables);
            tablesActions.Controls.Add(_btnSelectAll);
            tablesActions.Controls.Add(_btnSelectNone);

            _lblTablesStatus.Dock = DockStyle.Bottom;
            _lblTablesStatus.Padding = new Padding(0, 4, 0, 0);

            var tablesContainer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            tablesContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tablesContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tablesContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tablesContainer.Controls.Add(_dgvTables, 0, 0);
            tablesContainer.Controls.Add(tablesActions, 0, 1);
            tablesContainer.Controls.Add(_lblTablesStatus, 0, 2);
            tablesPage.Controls.Add(tablesContainer);

            // Tab 2: đối tượng (SP/function/view/trigger) — mặc định chọn hết để copy tự động,
            // bỏ tích là loại trừ đối tượng đó khỏi quá trình di chuyển.
            var objectsPage = new TabPage("Stored proc / function / view / trigger") { Padding = new Padding(4) };
            _dgvObjects.Dock = DockStyle.Fill;
            _dgvObjects.AllowUserToAddRows = false;
            _dgvObjects.AllowUserToDeleteRows = false;
            _dgvObjects.ReadOnly = false;
            _dgvObjects.MultiSelect = false;
            _dgvObjects.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _dgvObjects.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dgvObjects.BackgroundColor = Color.White;

            var objSelectCol = new DataGridViewCheckBoxColumn { HeaderText = "Chọn", Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
            var objNameCol = new DataGridViewTextBoxColumn { HeaderText = "Đối tượng (Schema.Tên)", ReadOnly = true };
            var objTypeCol = new DataGridViewTextBoxColumn { HeaderText = "Loại", ReadOnly = true, Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
            _dgvObjects.Columns.Add(objSelectCol);
            _dgvObjects.Columns.Add(objNameCol);
            _dgvObjects.Columns.Add(objTypeCol);

            var objectsActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, WrapContents = true, AutoSize = true };
            objectsActions.Controls.Add(_btnLoadObjects);
            objectsActions.Controls.Add(_btnObjectsSelectAll);
            objectsActions.Controls.Add(_btnObjectsSelectNone);

            _lblObjectsStatus.Dock = DockStyle.Bottom;
            _lblObjectsStatus.Padding = new Padding(0, 4, 0, 0);

            var objectsContainer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            objectsContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            objectsContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            objectsContainer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            objectsContainer.Controls.Add(_dgvObjects, 0, 0);
            objectsContainer.Controls.Add(objectsActions, 0, 1);
            objectsContainer.Controls.Add(_lblObjectsStatus, 0, 2);
            objectsPage.Controls.Add(objectsContainer);

            _objectsTab.TabPages.Add(tablesPage);
            _objectsTab.TabPages.Add(objectsPage);

            group.Controls.Add(_objectsTab);
            return group;
        }

        private Control BuildLogPanel()
        {
            var group = new GroupBox { Text = "5. Nhật ký & Trạng thái", Dock = DockStyle.Fill, Padding = new Padding(6) };

            var header = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, AutoSize = true };
            header.Controls.Add(_btnUpdateNew);
            header.Controls.Add(_btnUpdateFull);
            header.Controls.Add(_btnPreflight);
            header.Controls.Add(_btnVerify);
            header.Controls.Add(_btnReconcile);
            header.Controls.Add(_btnStart);
            header.Controls.Add(_btnCancel);
            header.Controls.Add(_btnExportLog);
            header.Controls.Add(_btnClearLog);
            header.Controls.Add(_btnCheckUpdate);

            // Thanh tiến trình: Dock=Fill trong dòng riêng để trải đều toàn bề ngang khung nhật ký.
            var progressRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Height = 18;
            _progressBar.Margin = new Padding(0, 4, 0, 2);
            progressRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            progressRow.Controls.Add(_progressBar, 0, 0);

            // Trạng thái + tốc độ: cho phép xuống dòng khi chuỗi dài để không bị cắt.
            var statusRow = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, AutoSize = true };
            statusRow.Controls.Add(_lblStatus);
            statusRow.Controls.Add(_lblSpeed);
            statusRow.Controls.Add(_lblVersion);
            _lblVersion.Text = "Phiên bản " + AppVersion;

            var preflightArea = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, AutoSize = true };
            preflightArea.Controls.Add(_lblPreflight);

            _txtLog.Dock = DockStyle.Fill;
            _txtLog.BackColor = Color.FromArgb(241, 244, 249);
            _txtLog.ForeColor = Color.FromArgb(30, 41, 59);
            _txtLog.ReadOnly = true;
            _txtLog.Multiline = true;
            _txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            _txtLog.Font = new Font("Consolas", 9.5F);
            _txtLog.HideSelection = false;
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5 };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Controls.Add(header, 0, 0);
            body.Controls.Add(progressRow, 0, 1);
            body.Controls.Add(statusRow, 0, 2);
            body.Controls.Add(preflightArea, 0, 3);
            body.Controls.Add(_txtLog, 0, 4);

            group.Controls.Add(body);
            return group;
        }

        private void WireEvents()
        {
            if (_sourceEditor.Parent is { } sg) { }
            _btnStart.Click += async (_, _) => await StartMigrationAsync();
            _btnUpdateNew.Click += async (_, _) => await RunSyncAsync(SyncKind.AppendNew);
            _btnUpdateFull.Click += async (_, _) => await RunSyncAsync(SyncKind.Full);
            _btnPreflight.Click += async (_, _) => await RunPreflightAndCheckAsync();
            _btnVerify.Click += async (_, _) => await VerifyDatabaseAsync();
            _btnReconcile.Click += async (_, _) => await RunReconcileAsync();
            _btnCancel.Click += (_, _) => _cts?.Cancel();
            _btnExportLog.Click += (_, _) => ExportLogAsync();
            _btnClearLog.Click += (_, _) => _txtLog.Clear();
            _btnCheckUpdate.Click += async (_, _) => await CheckForUpdatesAsync();
            _btnLoadTables.Click += async (_, _) => await LoadTablesAsync();
            _btnSelectAll.Click += (_, _) => SetAllTablesChecked(true);
            _btnSelectNone.Click += (_, _) => SetAllTablesChecked(false);
            _btnLoadObjects.Click += async (_, _) => await LoadObjectsAsync();
            _btnObjectsSelectAll.Click += (_, _) => SetAllObjectsChecked(true);
            _btnObjectsSelectNone.Click += (_, _) => SetAllObjectsChecked(false);
            _dgvTables.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) UpdateTablesStatus(); };
            _dgvObjects.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) UpdateObjectsStatus(); };
            _dgvTables.CellContentClick += (_, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == 0) _dgvTables.EndEdit();
            };
        }

        private async Task LoadTablesAsync()
        {
            var profile = _sourceEditor.ReadProfile();
            if (profile == null) return;
            if (string.IsNullOrWhiteSpace(profile.Database))
            {
                MessageBox.Show("Vui lòng nạp danh sách database và chọn database nguồn trước.",
                    "Chưa chọn database nguồn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _btnLoadTables.Enabled = false;
            try
            {
                var tables = await DatabaseCatalog.GetTablesAsync(profile, _builder, profile.Database);
                _dgvTables.Rows.Clear();
                foreach (var t in tables)
                {
                    var row = _dgvTables.Rows.Add(true, t.PlainName);
                    _dgvTables.Rows[row].Tag = t;
                }
                UpdateTablesStatus();
                AppendLog(string.Format("[THÔNG TIN] Server nguồn {0}/{1} đã kết nối — nạp được {2} bảng.",
                    profile.Server, profile.Database, tables.Count));
            }
            catch (Exception ex)
            {
                AppendLog("[LỖI] Server nguồn " + profile.Server + " không nạp được danh sách bảng: " + ex.Message);
                MessageBox.Show("Không nạp được danh sách bảng:\n" + ex.Message, "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnLoadTables.Enabled = true;
            }
        }

        private void SetAllTablesChecked(bool state)
        {
            foreach (DataGridViewRow row in _dgvTables.Rows)
                row.Cells[0].Value = state;
            UpdateTablesStatus();
        }

        private void UpdateTablesStatus()
        {
            var selected = 0;
            foreach (DataGridViewRow row in _dgvTables.Rows)
            {
                if (row.Cells[0].Value is bool b && b) selected++;
            }
            _lblTablesStatus.Text = string.Format("Có {0}/{1} bảng được chọn (bỏ chọn = bảng bị loại trừ).",
                selected, _dgvTables.Rows.Count);
            _lblTablesStatus.ForeColor = selected == _dgvTables.Rows.Count && _dgvTables.Rows.Count > 0
                ? Color.ForestGreen
                : Color.FromArgb(150, 90, 0);
        }

        /// <summary>Nạp danh sách stored procedure / function / view / trigger của database nguồn để người dùng loại trừ.</summary>
        private async Task LoadObjectsAsync()
        {
            var source = _sourceEditor.ReadProfile();
            if (source == null) return;
            if (string.IsNullOrWhiteSpace(source.Database))
            {
                MessageBox.Show("Vui lòng nạp danh sách database và chọn database nguồn trước.",
                    "Chưa chọn database nguồn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _btnLoadObjects.Enabled = false;
            try
            {
                var options = new MigrationOptions
                {
                    SourceConnectionString = _builder.Build(source),
                    CopyServerTriggers = _chkServerTriggers.Checked,
                    CopyPermissions = _chkCopyPermissions.Checked,
                    CommandTimeoutSeconds = (int)_numCmdTimeout.Value
                };

                await Task.Run(() =>
                {
                    options.SourceConnectionString = SqlConnectionFactory.NormalizeConnectionStringWithFallback(
                        options.SourceConnectionString, (int)_numCmdTimeout.Value, AppendLog);
                });

                var logger = new UiLogger(AppendLog, "Migrator");
                var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);
                var schema = await context.SchemaExtractor.ExtractAsync();

                _dgvObjects.Rows.Clear();
                foreach (var obj in schema.Objects)
                {
                    // Chỉ liệt kê các đối tượng module; không liệt kê schema/bảng/fk/quyền...
                    switch (obj.Type)
                    {
                        case DatabaseObjectType.StoredProcedure:
                        case DatabaseObjectType.Function:
                        case DatabaseObjectType.View:
                        case DatabaseObjectType.Trigger:
                        case DatabaseObjectType.ServerTrigger:
                            _dgvObjects.Rows.Add(true, obj.DisplayName, TypeLabel(obj.Type));
                            break;
                    }
                }
                UpdateObjectsStatus();
                AppendLog(string.Format("[THÔNG TIN] Server nguồn {0}/{1} — nạp được {2} đối tượng (SP/function/view/trigger).",
                    source.Server, source.Database, _dgvObjects.Rows.Count));
            }
            catch (Exception ex)
            {
                AppendLog("[LỖI] Server nguồn " + source.Server + " không nạp được danh sách đối tượng: " + ex.Message);
                MessageBox.Show("Không nạp được danh sách đối tượng:\n" + ex.Message, "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnLoadObjects.Enabled = true;
            }
        }

        private void SetAllObjectsChecked(bool state)
        {
            foreach (DataGridViewRow row in _dgvObjects.Rows)
                row.Cells[0].Value = state;
            UpdateObjectsStatus();
        }

        private void UpdateObjectsStatus()
        {
            var selected = 0;
            foreach (DataGridViewRow row in _dgvObjects.Rows)
            {
                if (row.Cells[0].Value is bool b && b) selected++;
            }
            _lblObjectsStatus.Text = string.Format("Có {0}/{1} đối tượng được chọn (bỏ chọn = đối tượng bị loại trừ khỏi di chuyển).",
                selected, _dgvObjects.Rows.Count);
            _lblObjectsStatus.ForeColor = selected == _dgvObjects.Rows.Count && _dgvObjects.Rows.Count > 0
                ? Color.ForestGreen
                : Color.FromArgb(150, 90, 0);
        }

        private static string TypeLabel(DatabaseObjectType type) => type switch
        {
            DatabaseObjectType.StoredProcedure => "Stored procedure",
            DatabaseObjectType.Function => "Function",
            DatabaseObjectType.View => "View",
            DatabaseObjectType.Trigger => "Trigger (cấp DB)",
            DatabaseObjectType.ServerTrigger => "Trigger (cấp server)",
            _ => type.ToString()
        };

        private bool TryBuildOptions(out MigrationOptions options, out ConnectionProfile source, out ConnectionProfile dest)
        {
            options = null!;
            source = null!;
            dest = null!;

            var s = _sourceEditor.ReadProfile();
            if (s == null) return false;
            source = s;
            var d = _destEditor.ReadProfile();
            if (d == null) return false;
            dest = d;

            if (string.IsNullOrWhiteSpace(source.Database))
            {
                MessageBox.Show("Vui lòng nạp danh sách database và chọn database nguồn.",
                    "Chưa chọn database nguồn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            // Database đích chưa chọn/nhập: yêu cầu người dùng chủ động chọn DB có sẵn
            // hoặc dùng nút "Tạo DB" trong khối đích để tạo database mới trước khi di chuyển.
            if (string.IsNullOrWhiteSpace(dest.Database))
            {
                MessageBox.Show("Vui lòng chọn database đích có sẵn ở ô Database, hoặc dùng nút 'Tạo DB' " +
                    "trong khối đích để tạo database mới trước khi di chuyển.",
                    "Chưa chọn database đích", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            if (dest.Database.Equals(source.Database, StringComparison.OrdinalIgnoreCase)
                && dest.Server.Equals(source.Server, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Database nguồn và đích giống nhau. Vui lòng chọn database đích khác để tránh sao chép lẫn lộn.",
                    "Trùng database nguồn/đích", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var state = new WizardState
            {
                Source = source,
                Destination = dest,
                CreateDestinationDatabase = true,
                DestinationDataFileDirectory = string.IsNullOrWhiteSpace(_destEditor.DestinationDataPath) ? null : _destEditor.DestinationDataPath,
                DestinationLogFileDirectory = string.IsNullOrWhiteSpace(_destEditor.DestinationLogPath) ? null : _destEditor.DestinationLogPath,
                Mode = _rdoFull.Checked ? MigrationMode.Full : _rdoSchema.Checked ? MigrationMode.SchemaOnly : MigrationMode.DataOnly,
                DisableForeignKeyConstraints = _chkDisableFk.Checked,
                DisableCheckConstraints = _chkDisableCheck.Checked,
                DisableTriggers = _chkDisableTriggers.Checked,
                DisableIndexesDuringLoad = _chkDisableIndexes.Checked,
                PreserveIdentity = _chkPreserveIdentity.Checked,
                CheckDataAfterLoad = _chkCheckData.Checked,
                ContinueOnNonCriticalErrors = _chkContinueOnError.Checked,
                ForceUnsupportedFeatures = _chkForceUnsupported.Checked,
                TruncateDestination = _chkTruncate.Checked,
                UseInternalTransaction = _chkUseInternalTransaction.Checked,
                CopyServerTriggers = _chkServerTriggers.Checked,
                CleanupPartialTableOnError = _chkCleanup.Checked,
                BatchSize = (int)_numBatchSize.Value,
                CommandTimeoutSeconds = (int)_numCmdTimeout.Value,
                BulkCopyTimeoutSeconds = (int)_numBulkTimeout.Value,
                // Engine dữ liệu lớn
                MaxParallelism = (int)_numMaxParallelism.Value,
                MaxBufferMB = (int)_numMaxBufferMB.Value,
                ChunkRowCount = (int)_numChunkRowCount.Value,
                UseKeysetChunking = _chkKeysetChunking.Checked,
                EnableReadCommittedSnapshotOnSource = _chkEnableRcsi.Checked,
                SwitchDestinationRecoveryDuringLoad = _chkSwitchRecovery.Checked,
                EnableTransferCheckpoint = _chkTransferCheckpoint.Checked,
                UseEstimatedRowCounts = _chkEstimatedCounts.Checked,
                VerifyAfterMigration = _chkVerifyAfterMigration.Checked
            };

            foreach (DataGridViewRow row in _dgvTables.Rows)
            {
                if (row.Cells[0].Value is bool b && !b)
                    state.ExcludeTables.Add((string)row.Cells[1].Value);
            }

            foreach (DataGridViewRow row in _dgvObjects.Rows)
            {
                if (row.Cells[0].Value is bool b && !b)
                    state.ExcludeObjects.Add((string)row.Cells[1].Value);
            }

            options = state.BuildMigrationOptions();
            options.SourceConnectionString = _builder.Build(source);
            options.DestinationConnectionString = _builder.Build(dest);
            options.CopyPermissions = _chkCopyPermissions.Checked;
            options.FailFast = _chkFailFast.Checked;
            options.LogFilePath = MakeLogFilePath(source, dest);
            return true;
        }

        /// <summary>
        /// Tạo database đích ngay khi người dùng nhập tên DB mới (hoặc DB chưa tồn tại) —
        /// gọi qua Core <see cref="DatabaseProvisioner"/>, connect master bằng account đích.
        /// Trả về false nếu không tạo được (thiếu quyền...) và không cho tiếp tục.
        /// </summary>
        private async Task<bool> EnsureDestinationDatabaseAsync(MigrationOptions options)
        {
            var provisioner = new DatabaseProvisioner(options, new UiLogger(AppendLog, "Migrator"));
            try
            {
                var ok = await provisioner.CreateIfMissingAsync();
                if (!ok)
                {
                    MessageBox.Show("Không tạo được database đích. Xem chi tiết trong nhật ký.",
                        "Lỗi tạo database đích", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    // Tạo các filegroup ROWS còn thiếu (không ném lỗi, chỉ cảnh báo).
                    await provisioner.EnsureFilegroupsAsync();
                }
                return ok;
            }
            catch (Exception ex)
            {
                var safe = SqlConnectionFactory.ScrubConnectionStringSecrets(ex.Message);
                AppendLog("[LỖI] Không tạo được database đích: " + safe);
                MessageBox.Show("Không tạo được database đích (kiểm tra quyền CREATE DATABASE của tài khoản đích):\n" + safe,
                    "Lỗi tạo database đích", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// Điều chỉnh chuỗi kết nối nguồn/đích sang biến thể tương thích với server
        /// (TrustServerCertificate / Encrypt=False khi cần) để mọi thành phần Core
        /// (preflight, sync, schema, data...) mở kết nối thành công ngay từ lần đầu.
        /// Chạy nền để không chặn luồng UI.
        /// </summary>
        private async Task NormalizeConnectionStringsAsync(MigrationOptions options)
        {
            var timeout = (int)_numCmdTimeout.Value;
            await Task.Run(() =>
            {
                options.SourceConnectionString =
                    SqlConnectionFactory.NormalizeConnectionStringWithFallback(options.SourceConnectionString, timeout, AppendLog);
                options.DestinationConnectionString =
                    SqlConnectionFactory.NormalizeConnectionStringWithFallback(options.DestinationConnectionString, timeout, AppendLog);
            });
        }

        /// <summary>
        /// Chạy kiểm tra sơ bộ (preflight: phiên bản, database) + dò thay đổi dữ liệu nguồn
        /// so với mốc đồng bộ. Cập nhật nhãn trạng thái và trạng thái 2 nút Update.
        /// </summary>
        private async Task RunPreflightAndCheckAsync()
        {
            if (!TryBuildOptions(out var options, out _, out _)) return;
            if (!await EnsureDestinationDatabaseAsync(options)) return;
            await NormalizeConnectionStringsAsync(options);

            var logger = new UiLogger(AppendLog, "Migrator");
            var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);
            SetBusy(true);
            try
            {
                _progressBar.Value = 0;
                _lblStatus.Text = "Đang kiểm tra trước khi chạy...";
                _lastPreflight = await context.PreflightChecker.CheckAsync();
                _lastSyncCheck = await context.SyncChecker.CheckAsync();

                AppendLog("— KIỂM TRA TRƯỚC KHI CHẠY —");
                foreach (var e in _lastPreflight.Errors)
                    AppendLog("[LỖI] " + e);
                foreach (var w in _lastPreflight.Warnings)
                    AppendLog("[CẢNH BÁO] " + w);
                // Hồ sơ kiểm kê nguồn real-time: bao nhiêu bảng/view/SP/function...
                foreach (var line in InventoryComparer.FormatInventory(
                    "— HỒ SƠ DATABASE NGUỒN —", _lastPreflight.SourceInventory!))
                    AppendLog(line);
                if (_lastPreflight.DestinationInventory != null)
                {
                    foreach (var line in InventoryComparer.FormatInventory(
                        "— HỒ SƠ DATABASE ĐÍCH (hiện tại) —", _lastPreflight.DestinationInventory))
                        AppendLog(line);
                }
                DisplaySyncCheck(_lastSyncCheck);
                UpdateSyncButtons();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Kiểm tra thất bại: " + ex.Message;
                AppendLog("[LỖI] " + ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task VerifyDatabaseAsync()
        {
            if (!TryBuildOptions(out var options, out _, out _)) return;
            if (!await EnsureDestinationDatabaseAsync(options)) return;
            await NormalizeConnectionStringsAsync(options);

            var logger = new UiLogger(AppendLog, "Migrator");
            var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);

            SetBusy(true);
            _cts = new CancellationTokenSource();
            _progressBar.Value = 0;

            try
            {
                _lblStatus.Text = "Đang trích xuất cấu trúc nguồn...";
                var schema = await context.SchemaExtractor.ExtractAsync(_cts.Token);

                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                    _lblSpeed.Text = "";
                });

                _lblStatus.Text = "Đang kiểm tra toàn vẹn dữ liệu + cấu trúc đích so với nguồn...";
                var report = await context.DataVerifier.VerifyAsync(schema, _cts.Token, progress);
                _progressBar.Value = 100;
                _lblStatus.Text = report.Success
                    ? "Kiểm tra toàn vẹn ĐẠT — đích đầy đủ và đúng như nguồn (dữ liệu + cấu trúc)."
                    : "Kiểm tra toàn vẹn CHƯA ĐẠT — xem chi tiết trong nhật ký.";
                DisplayVerificationSummary(report);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy kiểm tra toàn vẹn.";
                AppendLog("[THÔNG TIN] Đã hủy bởi người dùng.");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex);
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        private void DisplayVerificationSummary(DataVerificationReport report)
        {
            if (IsDisposed) return;

            AppendLog(string.Format(
                "— KIỂM TRA TOÀN VẸN DỮ LIỆU — {0} bảng đúng, {1} bảng lệch/thiếu, {2} bảng lỗi đọc, trong {3}.",
                report.VerifiedCount, report.IssueCount, report.FailedCount, report.Elapsed.ToString(@"hh\:mm\:ss")));

            string structureText;
            if (report.Structure == null)
            {
                structureText = "Chưa so được cấu trúc (xem nhật ký).";
            }
            else
            {
                var src = string.Join(", ", report.Structure.SourceCounts
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(k => k.Key + ": " + k.Value));
                var dst = string.Join(", ", report.Structure.DestinationCounts
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(k => k.Key + ": " + k.Value));
                structureText = report.Structure.Success
                    ? "Cấu trúc ĐỦ — số lượng mỗi loại đối tượng khớp giữa nguồn và đích."
                    : "Cấu trúc THIẾU " + report.Structure.MissingOnDestination.Count + " đối tượng trên đích.";
                AppendLog("— KIỂM TRA CẤU TRÚC — " + structureText);
                AppendLog("    Nguồn : " + src);
                AppendLog("    Đích  : " + dst);
                if (!report.Structure.Success)
                {
                    foreach (var missing in report.Structure.MissingOnDestination)
                        AppendLog("    THIẾU: " + missing);
                }
            }

            var lines = new List<string>
            {
                report.Success
                    ? "✔ ĐẠT — dữ liệu & cấu trúc đích đầy đủ và đúng hoàn toàn như nguồn."
                    : "✘ CHƯA ĐẠT — đích chưa khớp nguồn.",
                "Số bảng đã kiểm tra: " + report.TotalChecked,
                "Bảng khớp chính xác: " + report.VerifiedCount,
                "Bảng lệch số dòng / lệch nội dung / thiếu bảng: " + report.IssueCount,
                "Bảng lỗi đọc (không kết luận được): " + report.FailedCount,
                "Cấu trúc: " + structureText,
                "Thời gian: " + report.Elapsed.ToString(@"hh\:mm\:ss")
            };

            var issues = report.Tables.Where(t => t.Status is VerificationStatus.RowCountMismatch
                or VerificationStatus.ContentMismatch
                or VerificationStatus.MissingOnDestination
                or VerificationStatus.Failed).ToList();
            if (issues.Count > 0)
            {
                lines.Add("Bảng có vấn đề:");
                foreach (var t in issues.Take(10))
                    lines.Add("  - " + t.PlainName + ": " + t.Message);
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines), "Kết quả kiểm tra toàn vẹn",
                MessageBoxButtons.OK, report.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void DisplaySyncCheck(SyncCheckResult check)
        {
            if (!check.HasBaseline)
            {
                _lblPreflight.Text = "Chưa có mốc đồng bộ — chạy 'Bắt đầu di chuyển' một lần trước khi dùng nút Cập nhật.";
                _lblPreflight.ForeColor = Color.FromArgb(150, 90, 0);
                return;
            }

            if (!check.HasChanges)
            {
                _lblPreflight.Text = "Không phát hiện dữ liệu mới trên nguồn (so với mốc đồng bộ gần nhất).";
                _lblPreflight.ForeColor = Color.ForestGreen;
                return;
            }

            var canExact = check.Changes.Any(c => c.CanDetectExactRows && c.DeltaCount > 0);
            _lblPreflight.Text = string.Format(
                "Nguồn có ~{0:N0} dòng mới trên {1} bảng. Nút Cập nhật {2}.",
                check.TotalNewRows, check.Changes.Count,
                canExact ? "(New)/(Full) đã sẵn sàng" : "(Full) đã sẵn sàng, (New) cần bảng có cột rowversion/identity");
            _lblPreflight.ForeColor = Color.FromArgb(120, 80, 0);
        }

        private void UpdateSyncButtons()
        {
            var busy = _btnStart.Enabled == false;
            if (busy || _lastSyncCheck == null || !_lastSyncCheck.HasBaseline)
            {
                _btnUpdateNew.Enabled = false;
                _btnUpdateFull.Enabled = false;
                return;
            }

            _btnUpdateNew.Enabled = _lastSyncCheck.Changes.Any(c => c.CanDetectExactRows && c.DeltaCount > 0);
            _btnUpdateFull.Enabled = _lastSyncCheck.Changes.Any(c => c.HasChanges);
        }

        private async Task StartMigrationAsync()
        {
            if (!TryBuildOptions(out var options, out var source, out var dest)) return;
            if (!await EnsureDestinationDatabaseAsync(options)) return;
            await NormalizeConnectionStringsAsync(options);

            var confirm = MessageBox.Show(
                string.Format("Di chuyển database '{0}.{1}' sang '{2}.{3}'?",
                    source.Server, source.Database, dest.Server, dest.Database),
                "Xác nhận di chuyển", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var logger = new UiLogger(AppendLog, "Migrator");
            var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);

            // Preflight: tự phát hiện nâng/hạ/cùng phiên bản trước khi chạy.
            AppendLog("— KIỂM TRA TRƯỚC KHI CHẠY —");
            try
            {
                _lastPreflight = await context.PreflightChecker.CheckAsync();
                foreach (var w in _lastPreflight.Warnings) AppendLog("[CẢNH BÁO] " + w);
                foreach (var e in _lastPreflight.Errors) AppendLog("[LỖI] " + e);

                if (_lastPreflight.Kind == MigrationKind.Downgrade)
                {
                    var proceed = MessageBox.Show(
                        "KHÔNG khuyến nghị: số phiên bản đích thấp hơn nguồn (HẠ CẤP).\n" +
                        "Một số cấu trúc không được đích hỗ trợ sẽ bị cảnh báo/bỏ qua.\n\nBạn vẫn muốn tiếp tục?",
                        "Cảnh báo hạ cấp phiên bản", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (proceed != DialogResult.Yes) return;
                }
            }
            catch (Exception ex)
            {
                AppendLog("[CẢNH BÁO] Không kiểm tra được trước khi chạy: " + ex.Message);
            }

            SetBusy(true);
            _txtLog.Clear();
            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                    // Tách phần tốc độ/ETA từ message (định dạng: "Đang... • X MB/s • còn...")
                    if (p.Message.Contains("•"))
                    {
                        var parts = p.Message.Split('•');
                        if (parts.Length >= 2)
                            _lblSpeed.Text = "  " + string.Join(" • ", parts.Skip(1)).Trim();
                    }
                    else
                    {
                        _lblSpeed.Text = "";
                    }
                    AppendLog(p.Message);
                });

                // Điểm mốc: nếu đã có file nhật ký mới, xóa text hiển thị để tránh nhầm lẫn.
                _txtLog.Clear();

                var result = await context.Orchestrator.MigrateAsync(_cts.Token, progress);
                ShowSummary(result);

                // Đối chiếu đích vs nguồn vào nhật ký real-time ngay sau migrate.
                foreach (var line in InventoryComparer.FormatComparison(
                    result.SourceInventory!, result.DestinationInventory!))
                    AppendLog(line);

                // Sau đợt chạy thành công, baseline đã được lưu → cập nhật tình trạng nút Update.
                _lastSyncCheck = await context.SyncChecker.CheckAsync();
                DisplaySyncCheck(_lastSyncCheck);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy quá trình di chuyển.";
                AppendLog("[THÔNG TIN] Đã hủy bởi người dùng.");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex);
            }
            finally
            {
                SetBusy(false);
                UpdateSyncButtons();
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task RunSyncAsync(SyncKind kind)
        {
            if (_lastSyncCheck == null)
            {
                MessageBox.Show("Hãy bấm 'Kiểm tra trước khi chạy' để xem nguồn có dữ liệu mới trước.",
                    "Chưa kiểm tra", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RunPreflightAndCheckAsync();
                return;
            }

            if (!TryBuildOptions(out var options, out _, out _)) return;
            if (!await EnsureDestinationDatabaseAsync(options)) return;
            await NormalizeConnectionStringsAsync(options);

            var label = kind == SyncKind.AppendNew ? "Cập nhật dữ liệu MỚI" : "Cập nhật đầy đủ";
            var confirm = MessageBox.Show(
                string.Format("{0} sang database đích? Bảng đích sẽ được {1}.",
                    label,
                    kind == SyncKind.AppendNew ? "thêm các bản ghi mới từ nguồn" : "xóa sạch rồi chép lại toàn bộ từ nguồn"),
                "Xác nhận " + label, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var logger = new UiLogger(AppendLog, "Migrator");
            var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);

            SetBusy(true);
            _cts = new CancellationTokenSource();
            _progressBar.Value = 0;

            try
            {
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                    if (p.Message.Contains("•"))
                    {
                        var parts = p.Message.Split('•');
                        if (parts.Length >= 2)
                            _lblSpeed.Text = "  " + string.Join(" • ", parts.Skip(1)).Trim();
                    }
                    else
                    {
                        _lblSpeed.Text = "";
                    }
                    AppendLog(p.Message);
                });

                var result = await context.SyncUpdater.UpdateAsync(kind, _cts.Token, progress);
                ShowSyncSummary(result);

                _lastSyncCheck = await context.SyncChecker.CheckAsync();
                DisplaySyncCheck(_lastSyncCheck);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy đồng bộ.";
                AppendLog("[THÔNG TIN] Đã hủy bởi người dùng.");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex);
            }
            finally
            {
                SetBusy(false);
                UpdateSyncButtons();
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task RunReconcileAsync()
        {
            if (!TryBuildOptions(out var options, out _, out _)) return;
            if (!await EnsureDestinationDatabaseAsync(options)) return;
            await NormalizeConnectionStringsAsync(options);

            var confirm = MessageBox.Show(
                "Đồng bộ 100% sẽ QUÉT và PHÂN TÍCH sự khác biệt giữa nguồn và đích:\n\n" +
                "  • Đối tượng thiếu (bảng/view/SP/FN/trigger/FK/kiểu dữ liệu)\n" +
                "  • Cột thiếu trên bảng đã có\n" +
                "  • Dữ liệu lệch (chỉ hiển thị, KHÔNG tự sửa)\n" +
                "  • View/SP/FN lỗi compatibility khi downgrade\n\n" +
                "Lưu ý: quá trình quét CÓ tạo thử các object tương thích lên đích\n" +
                "để kiểm tra (không bao giờ ghi lên nguồn).\n\n" +
                "Sau khi quét xong, bạn sẽ xem báo cáo và CHỌN mục muốn xử lý.\n" +
                "App chỉ xử lý những mục được bạn chọn.\n\n" +
                "Tiếp tục?",
                "Xác nhận Quét & Phân tích", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var logger = new UiLogger(AppendLog, "Migrator");
            var context = MigrationPipelineFactory.CreateContext(options, logger, _protector);

            SetBusy(true);
            _cts = new CancellationTokenSource();
            _progressBar.Value = 0;

            try
            {
                var progress = new Progress<MigrationProgress>(p =>
                {
                    _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
                    _lblStatus.Text = p.Message;
                    _lblSpeed.Text = "";
                    AppendLog(p.Message);
                });

                // Pha 1: Quét (đọc nguồn; có tạo thử object tương thích trên đích để kiểm tra).
                var scanResult = await context.DatabaseReconciler.ScanAsync(_cts.Token, progress);

                if (scanResult.TotalIssues == 0)
                {
                    _lblStatus.Text = "Không tìm thấy vấn đề nào — đích đã khớp nguồn.";
                    AppendLog("[THÔNG TIN] Không tìm thấy vấn đề nào. Database đích đã khớp nguồn.");
                    ShowReconcileReport(scanResult);
                    return;
                }

                // Hiển thị báo cáo và để user chọn.
                var selectedIssues = ShowReconcileReportAndSelect(scanResult);
                if (selectedIssues == null || selectedIssues.Count == 0)
                {
                    _lblStatus.Text = "Không chọn vấn đề nào — bỏ qua.";
                    AppendLog("[THÔNG TIN] Người dùng không chọn vấn đề nào để xử lý.");
                    return;
                }

                // Pha 2: Xử lý các vấn đề được chọn.
                _lblStatus.Text = $"Đang xử lý {selectedIssues.Count} vấn đề đã chọn...";
                AppendLog($"[THÔNG TIN] Bắt đầu xử lý {selectedIssues.Count} vấn đề đã chọn...");
                _progressBar.Value = 0;

                var fixResult = await context.DatabaseReconciler.FixAsync(selectedIssues, _cts.Token, progress);
                ShowReconcileFixSummary(fixResult);

                // Hộp quyết định cho phần tồn đọng không thể tự động (AE/RLS/COMPRESS...):
                // user chỉ đọc và làm theo từng bước, không cần viết SQL tay.
                ShowResidualDecisionsDialog(fixResult);

                // Pha 3: Sync dữ liệu các bảng lệch đã chọn (thêm + sửa, không xóa).
                // Xem trước số dòng sẽ đổi, user OK mới ghi đích.
                var dataIssues = selectedIssues
                    .Where(i => i.Type == ReconcileIssueType.DataDifference)
                    .ToList();
                if (dataIssues.Count > 0)
                    await RunDataSyncAsync(context, dataIssues, progress, _cts.Token);

                _lblStatus.Text = fixResult.Success
                    ? $"Xử lý xong — thành công {fixResult.FixedIssues.Count}, bỏ qua {fixResult.SkippedIssues.Count}."
                    : $"Xử lý xong — thành công {fixResult.FixedIssues.Count}, thất bại {fixResult.FailedIssues.Count}.";
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy.";
                AppendLog("[THÔNG TIN] Đã hủy bởi người dùng.");
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi: " + ex.Message;
                AppendLog("[LỖI] " + ex);
            }
            finally
            {
                SetBusy(false);
                _cts.Dispose();
                _cts = null;
            }
        }

        /// <summary>Hiển thị báo cáo quét và để user chọn vấn đề muốn xử lý. Trả về danh sách được chọn.</summary>
        private List<ReconcileIssue>? ShowReconcileReportAndSelect(ReconcileResult result)
        {
            // Tạo form hiển thị báo cáo chi tiết (using để luôn giải phóng handle
            // dù user bấm Xử lý, Bỏ qua hay tắt form).
            using var reportForm = new Form
            {
                Text = "Báo cáo Quét & Phân tích Đồng bộ 100%",
                Size = new Size(800, 600),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var summaryLabel = new Label
            {
                Text = $"Tìm thấy {result.TotalIssues} vấn đề: "
                    + $"{result.MissingTables} bảng thiếu, {result.MissingViews} view thiếu, "
                    + $"{result.MissingProcedures} SP thiếu, {result.MissingFunctions} FN thiếu, "
                    + $"{result.MissingColumns} cột thiếu, {result.MissingForeignKeys} FK thiếu, "
                    + $"{result.DataDifferences} dữ liệu lệch, {result.BrokenDependencies} dependency broken.",
                Dock = DockStyle.Top,
                Height = 50,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Padding(8)
            };

            // DataGridView hiển thị danh sách issues.
            // Lưu ý: grid để ReadOnly = false nhưng các cột chữ đều khóa riêng,
            // chỉ cột checkbox "Select" cho phép tick (trước đây grid ReadOnly = true
            // nên user không tick được và luôn nhận "không chọn vấn đề nào").
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false
            };

            grid.Columns.Add("Select", "✓");
            grid.Columns.Add("ObjectName", "Đối tượng");
            grid.Columns.Add("ObjectType", "Loại");
            grid.Columns.Add("Severity", "Mức độ");
            grid.Columns.Add("Description", "Mô tả");
            grid.Columns.Add("SuggestedAction", "Gợi ý xử lý");
            grid.Columns.Add("CanAutoFix", "Tự fix");

            // ẩn cột Select checkbox — dùng CheckBoxColumn.
            grid.Columns.Remove(grid.Columns["Select"]!);
            var checkCol = new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "✓", Width = 40, ReadOnly = false };
            grid.Columns.Insert(0, checkCol);

            // Khóa các cột chữ, chỉ cho sửa checkbox.
            foreach (DataGridViewColumn col in grid.Columns)
            {
                if (col.Name != "Select")
                    col.ReadOnly = true;
            }
            grid.Columns["Description"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            // Commit ngay khi user click checkbox (nếu không phải rời dòng mới nhận giá trị).
            grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            foreach (var issue in result.Issues)
            {
                var rowIdx = grid.Rows.Add(
                    issue.CanAutoFix && issue.Type != ReconcileIssueType.DataDifference,
                    issue.ObjectName,
                    GetObjectTypeDisplay(issue.ObjectType),
                    GetSeverityDisplay(issue.Severity),
                    issue.Description,
                    issue.SuggestedAction,
                    issue.CanAutoFix ? "✔" : "✘");
                var row = grid.Rows[rowIdx];
                row.Tag = issue;

                // Màu theo severity.
                row.DefaultCellStyle.ForeColor = issue.Severity switch
                {
                    ReconcileIssueSeverity.Error => Color.Red,
                    ReconcileIssueSeverity.Warning => Color.DarkOrange,
                    _ => Color.Black
                };
            }

            // Nút.
            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
            var btnCancel = new Button { Text = "Bỏ qua", DialogResult = DialogResult.Cancel, Width = 80 };
            var btnFixSelected = new Button { Text = "Xử lý đã chọn", Width = 120, Enabled = true };
            var btnCheckAll = new Button { Text = "Chọn tất cả", Width = 100 };
            btnFixSelected.Click += (_, _) => reportForm.DialogResult = DialogResult.OK;
            btnCheckAll.Click += (_, _) =>
            {
                // Toggle: nếu tất cả đang được chọn thì bỏ chọn hết, ngược lại chọn hết.
                var allChecked = true;
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.Cells["Select"] is DataGridViewCheckBoxCell cell
                        && !(cell.Value is bool isChecked && isChecked))
                    {
                        allChecked = false;
                        break;
                    }
                }

                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.Cells["Select"] is DataGridViewCheckBoxCell cell)
                        cell.Value = !allChecked;
                }
                grid.EndEdit();
                btnCheckAll.Text = allChecked ? "Chọn tất cả" : "Bỏ chọn tất cả";
            };
            btnPanel.Controls.Add(btnFixSelected);
            btnPanel.Controls.Add(btnCheckAll);
            btnPanel.Controls.Add(btnCancel);

            reportForm.Controls.Add(grid);
            reportForm.Controls.Add(summaryLabel);
            reportForm.Controls.Add(btnPanel);
            reportForm.CancelButton = btnCancel;
            reportForm.AcceptButton = btnFixSelected;

            // Ghi log chi tiết.
            foreach (var issue in result.Issues)
            {
                var tag = issue.Severity switch
                {
                    ReconcileIssueSeverity.Error => "LỖI",
                    ReconcileIssueSeverity.Warning => "CẢNH BÁO",
                    _ => "THÔNG TIN"
                };
                AppendLog($"  [{tag}] {issue.ObjectName} ({GetObjectTypeDisplay(issue.ObjectType)}): {issue.Description}");
                AppendLog($"    → Gợi ý: {issue.SuggestedAction}");
                if (issue.UnsupportedRules.Count > 0)
                    AppendLog($"    → Không hỗ trợ: {string.Join(", ", issue.UnsupportedRules)}");
                if (issue.HasTriedCreate && !issue.CreateSucceeded)
                    AppendLog($"    → Lỗi SQL: {issue.ErrorMessage}");
            }

            var dialogResult = reportForm.ShowDialog(this);
            if (dialogResult != DialogResult.OK)
                return null;

            // Thu thập các issue được user tick chọn.
            var selected = new List<ReconcileIssue>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is ReconcileIssue issue && row.Cells["Select"] is DataGridViewCheckBoxCell cell)
                {
                    if (cell.Value is bool isChecked && isChecked)
                        selected.Add(issue);
                }
            }

            return selected;
        }

        private void ShowReconcileReport(ReconcileResult result)
        {
            var lines = new List<string>
            {
                result.Success ? "✔ Quét hoàn tất — không có vấn đề." : "✘ Quét hoàn tất có vấn đề.",
                "Tổng thời gian: " + result.Elapsed.ToString(@"hh\:mm\:ss\.fff"),
                $"Tổng số vấn đề: {result.TotalIssues}",
                $"  • Bảng thiếu: {result.MissingTables}",
                $"  • View thiếu: {result.MissingViews}",
                $"  • SP thiếu: {result.MissingProcedures}",
                $"  • FN thiếu: {result.MissingFunctions}",
                $"  • Trigger thiếu: {result.MissingTriggers}",
                $"  • Sequence thiếu: {result.MissingSequences}",
                $"  • Cột thiếu: {result.MissingColumns}",
                $"  • FK thiếu: {result.MissingForeignKeys}",
                $"  • Dữ liệu lệch: {result.DataDifferences}",
                $"  • Dependency broken: {result.BrokenDependencies}"
            };

            if (result.Errors.Count > 0)
            {
                lines.Add("Lỗi:");
                foreach (var e in result.Errors.Take(10))
                    lines.Add("  - " + e);
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines),
                "Kết quả Quét Đồng bộ 100%",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Pha 3 Đồng bộ 100%: xem trước sync dữ liệu (đếm sẽ thêm/sửa, không ghi),
        /// user OK mới ghi đích. Chỉ thêm + sửa, không bao giờ xóa dòng đích.
        /// </summary>
        private async Task RunDataSyncAsync(
            MigrationContext context,
            List<ReconcileIssue> dataIssues,
            IProgress<MigrationProgress> progress,
            CancellationToken ct)
        {
            var tables = dataIssues
                .Select(i => i.ObjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _lblStatus.Text = $"Đang ước lượng sync {tables.Count} bảng...";
            AppendLog($"[THÔNG TIN] Ước lượng sync dữ liệu {tables.Count} bảng đã chọn (chỉ đếm, chưa ghi)...");
            _progressBar.Value = 0;

            var preview = await context.DatabaseReconciler.PreviewDataSyncAsync(
                tables, ct, progress);
            foreach (var item in preview)
            {
                if (item.Skipped)
                    AppendLog($"[BỎ QUA] Sync {item.Table}: {item.SkipReason}");
                else
                    AppendLog($"[THÔNG TIN] Sync {item.Table}: nguồn {item.SourceRows:N0} dòng, "
                        + $"đích {item.DestRows:N0} dòng → sẽ thêm {item.WillInsert:N0}, sửa {item.WillUpdate:N0}.");
            }

            if (!ShowDataSyncPreview(preview))
            {
                _lblStatus.Text = "Đã hủy sync dữ liệu.";
                AppendLog("[THÔNG TIN] Người dùng hủy sync dữ liệu sau khi xem trước.");
                return;
            }

            var runnable = preview.Where(p => !p.Skipped && (p.WillInsert > 0 || p.WillUpdate > 0)).ToList();
            if (runnable.Count == 0)
            {
                _lblStatus.Text = "Không có gì để sync — dữ liệu đã khớp hoặc các bảng đều bỏ qua.";
                AppendLog("[THÔNG TIN] Không có gì để sync.");
                return;
            }

            _lblStatus.Text = $"Đang sync dữ liệu {runnable.Count} bảng...";
            AppendLog($"[THÔNG TIN] Bắt đầu sync dữ liệu {runnable.Count} bảng (chỉ thêm + sửa, không xóa)...");
            _progressBar.Value = 0;

            var syncResult = await context.DatabaseReconciler.SyncDataAsync(
                runnable.Select(p => p.Table).ToList(), ct, progress);
            ShowDataSyncSummary(syncResult);

            _lblStatus.Text = syncResult.Success
                ? $"Sync xong — thêm {syncResult.TotalInserted:N0}, sửa {syncResult.TotalUpdated:N0}."
                : $"Sync xong có lỗi — thêm {syncResult.TotalInserted:N0}, sửa {syncResult.TotalUpdated:N0}.";
        }

        /// <summary>Hộp xem trước sync: bảng nào thêm/sửa bao nhiêu, bảng nào bỏ qua vì sao.</summary>
        private bool ShowDataSyncPreview(IReadOnlyList<DataSyncPreviewItem> preview)
        {
            using var form = new Form
            {
                Text = "Xem trước sync dữ liệu (chỉ thêm + sửa, không xóa)",
                Size = new Size(720, 420),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false
            };
            grid.Columns.Add("Table", "Bảng");
            grid.Columns.Add("Source", "Nguồn (dòng)");
            grid.Columns.Add("Dest", "Đích (dòng)");
            grid.Columns.Add("Insert", "Sẽ thêm");
            grid.Columns.Add("Update", "Sẽ sửa");
            grid.Columns.Add("Note", "Ghi chú");

            foreach (var item in preview)
            {
                var rowIdx = grid.Rows.Add(
                    item.Table,
                    item.Skipped ? "-" : item.SourceRows.ToString("N0"),
                    item.Skipped ? "-" : item.DestRows.ToString("N0"),
                    item.Skipped ? "-" : item.WillInsert.ToString("N0"),
                    item.Skipped ? "-" : item.WillUpdate.ToString("N0"),
                    item.Skipped ? item.SkipReason : "Sẵn sàng sync");
                if (item.Skipped)
                    grid.Rows[rowIdx].DefaultCellStyle.ForeColor = Color.Gray;
            }

            var note = new Label
            {
                Text = "Bấm OK để ghi lên database ĐÍCH (nguồn không đổi). Bảng bỏ qua giữ nguyên.",
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(8, 6, 8, 0)
            };

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft
            };
            var btnOk = new Button { Text = "OK — sync ngay", DialogResult = DialogResult.OK, Width = 130 };
            var btnCancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 80 };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);

            form.Controls.Add(grid);
            form.Controls.Add(note);
            form.Controls.Add(btnPanel);
            form.AcceptButton = btnOk;
            form.CancelButton = btnCancel;

            return form.ShowDialog(this) == DialogResult.OK;
        }

        /// <summary>Hộp tóm tắt kết quả sync dữ liệu.</summary>
        private void ShowDataSyncSummary(DataSyncResult result)
        {
            var lines = new List<string>
            {
                result.Success ? "✔ Sync dữ liệu hoàn tất." : "✘ Sync dữ liệu hoàn tất có lỗi.",
                "Tổng thời gian: " + result.Elapsed.ToString(@"hh\:mm\:ss\.fff"),
                $"Đã thêm: {result.TotalInserted:N0} dòng",
                $"Đã sửa: {result.TotalUpdated:N0} dòng"
            };

            foreach (var t in result.Tables)
            {
                if (t.Skipped)
                    lines.Add($"  - {t.Table}: bỏ qua ({t.SkipReason})");
                else if (t.Errors.Count > 0)
                    lines.Add($"  ✘ {t.Table}: lỗi — {string.Join("; ", t.Errors.Take(2))}");
                else
                    lines.Add($"  ✔ {t.Table}: thêm {t.Inserted:N0}, sửa {t.Updated:N0}.");
            }

            foreach (var line in lines)
                AppendLog((line.StartsWith("  ✘") ? "[LỖI] Sync " : "[THÔNG TIN] Sync ") + line.Trim());

            MessageBox.Show(string.Join(Environment.NewLine, lines),
                "Kết quả sync dữ liệu",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void ShowReconcileFixSummary(ReconcileFixResult result)
        {
            var lines = new List<string>
            {
                result.Success ? "✔ Xử lý hoàn tất." : "✘ Xử lý hoàn tất có lỗi.",
                "Tổng thời gian: " + result.Elapsed.ToString(@"hh\:mm\:ss\.fff"),
                $"Thành công: {result.FixedIssues.Count}",
                $"Thất bại: {result.FailedIssues.Count}",
                $"Bỏ qua: {result.SkippedIssues.Count}"
            };

            if (result.FixedIssues.Count > 0)
            {
                lines.Add("Đã xử lý:");
                foreach (var issue in result.FixedIssues.Take(20))
                    lines.Add($"  ✔ {issue.ObjectName} ({GetObjectTypeDisplay(issue.ObjectType)})");
            }

            if (result.FailedIssues.Count > 0)
            {
                lines.Add("Xử lý thất bại:");
                foreach (var issue in result.FailedIssues.Take(10))
                    lines.Add($"  ✘ {issue.ObjectName}: {issue.ErrorMessage}");
            }

            if (result.SkippedIssues.Count > 0)
            {
                lines.Add("Bỏ qua (ghi rõ lý do từng mục):");
                foreach (var issue in result.SkippedIssues.Take(10))
                    lines.Add($"  - {issue.ObjectName}: {GetSkipReason(issue)}");
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines),
                "Kết quả Xử lý Đồng bộ 100%",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>
        /// Hộp "việc cần quyết định" cho phần tồn đọng không thể tự động hoàn toàn
        /// (Always Encrypted, RLS, COMPRESS, server trigger...). Mỗi mục kèm runbook
        /// từng bước bằng tiếng Việt — user chỉ đọc và làm theo, không viết SQL tay.
        /// </summary>
        private void ShowResidualDecisionsDialog(ReconcileFixResult fixResult)
        {
            var residuals = fixResult.FailedIssues.Concat(fixResult.SkippedIssues)
                .Where(i => i.UnsupportedRules.Any(u =>
                        u == "always_encrypted" || u == "row_level_security"
                        || u == "compress" || u == "decompress")
                    || i.ObjectType == "SERVER_TRIGGER")
                .GroupBy(i => i.ObjectName)
                .Select(g => g.First())
                .ToList();
            if (residuals.Count == 0)
                return;

            foreach (var issue in residuals)
            {
                AppendLog($"[QUYẾT ĐỊNH] {issue.ObjectName}: {GetResidualRunbookTitle(issue)}");
            }

            using var dialog = new Form
            {
                Text = $"Việc cần quyết định ({residuals.Count} mục) — không cần viết SQL tay",
                Size = new Size(760, 520),
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var list = new ListBox { Dock = DockStyle.Left, Width = 240 };
            foreach (var issue in residuals)
                list.Items.Add(issue.ObjectName + "  [" + GetObjectTypeDisplay(issue.ObjectType) + "]");

            var details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.5f)
            };

            list.SelectedIndexChanged += (_, _) =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < residuals.Count)
                    details.Text = GetResidualRunbook(residuals[list.SelectedIndex]);
            };
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;

            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft
            };
            var btnOk = new Button { Text = "Đã hiểu", DialogResult = DialogResult.OK, Width = 100 };
            btnPanel.Controls.Add(btnOk);

            dialog.Controls.Add(details);
            dialog.Controls.Add(list);
            dialog.Controls.Add(btnPanel);
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnOk;
            dialog.ShowDialog(this);
        }

        private static string GetResidualRunbookTitle(ReconcileIssue issue)
        {
            if (issue.UnsupportedRules.Contains("always_encrypted"))
                return "cột mã hóa Always Encrypted — cần xuất khóa.";
            if (issue.UnsupportedRules.Contains("row_level_security"))
                return "chính sách RLS — cần áp dụng lại ở tầng ứng dụng.";
            if (issue.UnsupportedRules.Contains("compress") || issue.UnsupportedRules.Contains("decompress"))
                return "nén/giải nén GZip — cần CLR hoặc chuyển lên ứng dụng.";
            if (issue.ObjectType == "SERVER_TRIGGER")
                return "server trigger — cần quyền CONTROL SERVER để tạo tay.";
            return "cần xử lý thêm: " + (issue.ErrorMessage ?? issue.SuggestedAction);
        }

        private static string GetResidualRunbook(ReconcileIssue issue)
        {
            var head = issue.ObjectName + " (" + GetObjectTypeDisplay(issue.ObjectType) + ")\r\n"
                + new string('=', 60) + "\r\n"
                + (issue.ErrorMessage ?? issue.Description) + "\r\n\r\n";

            if (issue.UnsupportedRules.Contains("always_encrypted"))
            {
                return head + "CỘT MÃ HÓA (Always Encrypted) — từng bước:\r\n"
                    + "1. Trên server NGUỒN, mở SSMS → Security → Always Encrypted Keys →\r\n"
                    + "   chuột phải Column Master Key → Export (lưu file .pfx + mật khẩu).\r\n"
                    + "2. Trên máy chạy ứng dụng của bạn, Import chứng chỉ .pfx vào\r\n"
                    + "   Current User → Personal (cần cho tài khoản chạy app).\r\n"
                    + "3. Chuỗi kết nối tới ĐÍCH phải bật Column Encryption Setting=Enabled\r\n"
                    + "   (hỏi người quản trị nếu dùng tài khoản least-privilege).\r\n"
                    + "4. Dữ liệu app đã chép sang là bản mã (ciphertext) — sau khi có khóa,\r\n"
                    + "   kiểm tra đọc lại 1-2 dòng để xác nhận giải mã đúng.\r\n"
                    + "LƯU Ý: khóa mã hóa không bao giờ được app tự động sao chép.";
            }

            if (issue.UnsupportedRules.Contains("row_level_security"))
            {
                return head + "CHÍNH SÁCH BẢO MẬT DÒNG (RLS) — từng bước:\r\n"
                    + "1. Hàm điều kiện (predicate function) của policy đã được app\r\n"
                    + "   di chuyển sang đích (kiểm tra trong danh sách Function).\r\n"
                    + "2. SQL 2014 không có CREATE SECURITY POLICY, nên quyền lọc dòng\r\n"
                    + "   phải đặt ở TẦNG ỨNG DỤNG: mọi câu SELECT tới bảng này phải\r\n"
                    + "   thêm điều kiện WHERE tương đương hàm điều kiện.\r\n"
                    + "3. Cách khác không sửa app: tạo VIEW lọc sẵn trên đích, VD:\r\n"
                    + "   CREATE VIEW dbo.T_Secure AS SELECT * FROM dbo.T\r\n"
                    + "     WHERE dbo.fn_Predicate(USER_NAME()) = 1;\r\n"
                    + "   rồi cho user chỉ đọc qua view đó (thu hồi SELECT bảng gốc).\r\n"
                    + "LƯU Ý: nếu bỏ qua bước này, user sẽ thấy TOÀN BỘ dòng.";
            }

            if (issue.UnsupportedRules.Contains("compress") || issue.UnsupportedRules.Contains("decompress"))
            {
                return head + "NÉN/GIẢI NÉN GZip — từng bước:\r\n"
                    + "1. SQL 2014 không có COMPRESS/DECOMPRESS. Cách sạch nhất là chuyển\r\n"
                    + "   việc nén/giải nén lên TẦNG ỨNG DỤNG (thư viện GZip có sẵn).\r\n"
                    + "2. Nếu bắt buộc giữ trong DB: tạo SQLCLR function dùng\r\n"
                    + "   System.IO.Compression.GZipStream (cần quyền CREATE ASSEMBLY +\r\n"
                    + "   bật CLR trên server đích — hỏi DBA trước khi làm).\r\n"
                    + "3. Dữ liệu cũ đã nén bằng 2016 vẫn là GZip chuẩn nên phía nào\r\n"
                    + "   giải cũng ra đúng, không lo mất dữ liệu.";
            }

            if (issue.ObjectType == "SERVER_TRIGGER")
            {
                return head + "SERVER TRIGGER — từng bước:\r\n"
                    + "1. Server trigger sống ở cấp SERVER (không nằm trong database),\r\n"
                    + "   tạo nó cần quyền CONTROL SERVER.\r\n"
                    + "2. Mở script gốc trên nguồn (sys.server_triggers → definition),\r\n"
                    + "   chạy CREATE TRIGGER ... ON ALL SERVER trên server đích\r\n"
                    + "   bằng tài khoản sysadmin.\r\n"
                    + "3. Kiểm tra: SELECT * FROM sys.server_triggers trên đích.";
            }

            return head + (issue.SuggestedAction ?? "");
        }

        /// <summary>Giải thích vì sao một issue bị bỏ qua (hiển thị trong tóm tắt sau xử lý).</summary>
        private static string GetSkipReason(ReconcileIssue issue)
        {
            if (issue.Type == ReconcileIssueType.DataDifference)
                return "Dữ liệu lệch chỉ báo cáo, không tự sync — cần chọn sync thủ công.";
            if (issue.Type == ReconcileIssueType.BrokenDependency)
                return "Dependency broken — cần tạo object phụ thuộc trước: "
                    + (issue.Dependencies.Count > 0 ? string.Join(", ", issue.Dependencies) : issue.SuggestedAction);
            if (issue.CreateSucceeded)
                return "Đã tạo thành công ngay khi quét nên không tạo lại.";
            if (issue.UnsupportedRules.Count > 0)
                return "Chứa feature không hỗ trợ trên đích (" + string.Join(", ", issue.UnsupportedRules) + ") — cần fix thủ công.";
            if (string.IsNullOrWhiteSpace(issue.RewrittenScript ?? issue.SourceScript))
                return "Không có script nguồn để tạo.";
            return issue.SuggestedAction;
        }

        private static string GetObjectTypeDisplay(string type) => type switch
        {            "TABLE" => "Bảng",
            "VIEW" => "View",
            "STORED_PROCEDURE" => "Stored Procedure",
            "FUNCTION" => "Function",
            "TRIGGER" => "Trigger",
            "SEQUENCE" => "Sequence",
            "UDT" => "Kiểu dữ liệu",
            "SERVER_TRIGGER" => "Server Trigger",
            "FK" => "Khóa ngoại",
            "COLUMN" => "Cột",
            _ => type
        };

        private static string GetSeverityDisplay(ReconcileIssueSeverity severity) => severity switch
        {
            ReconcileIssueSeverity.Error => "Lỗi",
            ReconcileIssueSeverity.Warning => "Cảnh báo",
            _ => "Thông tin"
        };

        private void ShowSyncSummary(SyncResult result)
        {
            var lines = new List<string>
            {
                result.Success ? "✔ Đồng bộ hoàn tất." : "✘ Đồng bộ hoàn tất có lỗi.",
                "Tổng thời gian: " + result.Elapsed.ToString(@"hh\:mm\:ss\.fff"),
                "Số bảng xử lý: " + result.Tables.Count,
                "Tổng số dòng đã đồng bộ: " + result.RowsSynced.ToString("N0")
            };

            if (result.Tables.Any(t => !t.Success))
            {
                lines.Add("Bảng lỗi:");
                foreach (var t in result.Tables.Where(t => !t.Success).Take(10))
                    lines.Add("  - " + t.PlainName + ": " + t.Error);
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines),
                "Kết quả đồng bộ dữ liệu",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private string MakeLogFilePath(ConnectionProfile source, ConnectionProfile dest)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SqlMigrator", "Logs");
            Directory.CreateDirectory(folder);
            var file = string.Format("migrate_{0:yyyyMMdd_HHmmss}_{1}_{2}.log",
                DateTime.Now,
                Sanitize(source.Server) + "_" + Sanitize(source.Database),
                Sanitize(dest.Server) + "_" + Sanitize(dest.Database));
            return Path.Combine(folder, file);
        }

        private static string Sanitize(string value)
        {
            var chars = value.Where(ch => char.IsLetterOrDigit(ch) || ch == '.').ToArray();
            var result = new string(chars);
            return string.IsNullOrWhiteSpace(result) ? "migrate" : result.Replace(".", "_");
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
            var color = line.Contains("[LỖI]") || line.Contains("[NGHIÊM TRỌNG]")
                ? Color.Firebrick
                : line.Contains("[CẢNH BÁO]")
                    ? Color.DarkOrange
                    : line.Contains("[GỠ RỐI]")
                        ? Color.Gray
                        : Color.FromArgb(30, 41, 59);

            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
            _txtLog.SelectionColor = color;
            _txtLog.AppendText(line + Environment.NewLine);
            _txtLog.ScrollToCaret();
        }

        private void ShowSummary(MigrationResult result)
        {
            if (IsDisposed) return;

            var lines = new List<string>
            {
                result.Success ? "✔ Hoàn tất." : "✘ Hoàn tất với lỗi.",
                "Tổng thời gian: " + (result.Elapsed.TotalSeconds > 0 ? result.Elapsed.ToString(@"hh\:mm\:ss\.fff") : "0"),
                "Đối tượng đã tạo: " + result.ObjectsBuilt,
                "Đối tượng bỏ qua/lỗi: " + result.ObjectsSkipped
            };
            if (result.DataCopy != null)
            {
                lines.Add("Bảng đã sao chép: " + result.DataCopy.TablesCopied);
                lines.Add("Số dòng đã chép: " + result.DataCopy.RowsCopied.ToString("N0"));
                lines.Add("Dòng bỏ qua: " + result.DataCopy.RowsExcluded.ToString("N0"));
            }
            if (result.Verification != null)
            {
                lines.Add("— Kiểm tra toàn vẹn —");
                lines.Add(result.Verification.Success ? "  Dữ liệu đích ĐẠT (đầy đủ và đúng như nguồn)." : "  Dữ liệu đích CHƯA ĐẠT.");
                lines.Add("  Bảng đúng chính xác: " + result.Verification.VerifiedCount);
                lines.Add("  Bảng lệch/thiếu/không đọc được: " + (result.Verification.IssueCount + result.Verification.FailedCount));
            }
            if (result.SourceInventory != null && result.SourceInventory.Ok
                && result.DestinationInventory != null && result.DestinationInventory.Ok)
            {
                lines.Add("— Đối chiếu đích vs nguồn (chi tiết trong nhật ký) —");
                foreach (var d in InventoryComparer.Compare(result.SourceInventory, result.DestinationInventory))
                    lines.Add($"  {(d.Matches ? "✔" : "✘")} {d.Label}: nguồn {d.SourceCount:N0} → đích {d.DestCount:N0}"
                        + (d.Matches ? "" : $" (thiếu {d.MissingNames.Count:N0})"));
            }
            lines.Add("Cảnh báo: " + result.Warnings.Count);
            if (result.Errors.Count > 0)
            {
                lines.Add("Lỗi:");
                foreach (var err in result.Errors.Take(10))
                    lines.Add("  - " + err);
            }
            if (result.ObjectErrors.Count > 0)
            {
                lines.Add("Lỗi tạo đối tượng (xem nhật ký):");
                foreach (var err in result.ObjectErrors.Take(5))
                    lines.Add("  - " + err);
            }

            var icon = result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
            MessageBox.Show(string.Join(Environment.NewLine, lines), "Kết quả di chuyển",
                MessageBoxButtons.OK, icon);
        }

        private void ExportLogAsync()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Xuất nhật ký di chuyển",
                Filter = "Tệp nhật ký (*.log)|*.log|Tệp văn bản (*.txt)|*.txt",
                DefaultExt = "log",
                FileName = "migrate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                System.IO.File.WriteAllText(dialog.FileName, _txtLog.Text, System.Text.Encoding.UTF8);
                MessageBox.Show("Đã xuất nhật ký ra:\n" + dialog.FileName, "Xuất nhật ký",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không xuất được nhật ký:\n" + ex.Message, "Lỗi",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Phiên bản app đang chạy (nguồn sự thật cho updater).</summary>
        private static Version AppVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

        /// <summary>Bản đang chạy có phải bản cài đặt (Inno Setup) không.</summary>
        private static bool IsInstalledMode()
        {
            try
            {
                return File.Exists(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "unins000.exe"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Nút "Kiểm tra cập nhật": hỏi GitHub Releases, có bản mới thì hỏi
        /// tải + cài đặt. Không đụng database nào.
        /// </summary>
        private async Task CheckForUpdatesAsync()
        {
            SetBusy(true);
            _lblStatus.Text = "Đang kiểm tra cập nhật...";
            try
            {
                var current = AppVersion;
                AppendLog($"[THÔNG TIN] Phiên bản hiện tại: {current}. Đang hỏi GitHub...");
                using var svc = new UpdateService(
                    logger: new UiLogger(AppendLog, "Updater"));
                var info = await svc.CheckForUpdateAsync(current, preferInstaller: IsInstalledMode());
                if (info == null)
                {
                    _lblStatus.Text = "Không kiểm tra được cập nhật.";
                    AppendLog("[CẢNH BÁO] Không kiểm tra được cập nhật (mất mạng hoặc chưa có release).");
                    MessageBox.Show("Không kiểm tra được cập nhật.\nHãy kiểm tra mạng rồi thử lại.",
                        "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!info.Available)
                {
                    _lblStatus.Text = $"Đang dùng bản mới nhất ({current}).";
                    AppendLog($"[THÔNG TIN] Đang dùng bản mới nhất ({current}).");
                    MessageBox.Show($"Bạn đang dùng bản mới nhất ({current}).",
                        "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AppendLog($"[THÔNG TIN] Có bản mới {info.LatestVersion} ({info.FileName}).");
                var notes = (info.ReleaseNotes ?? "").Trim();
                if (notes.Length > 1500)
                    notes = notes.Substring(0, 1500) + "\n...";
                var ask = MessageBox.Show(
                    $"Có bản mới {info.LatestVersion} (bạn đang dùng {current}).\n\n"
                    + $"Ghi chú phát hành:\n{notes}\n\nTải và cài đặt ngay?",
                    "Có bản cập nhật", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask != DialogResult.Yes)
                    return;

                await DownloadAndInstallAsync(svc, info);
            }
            catch (OperationCanceledException)
            {
                _lblStatus.Text = "Đã hủy kiểm tra cập nhật.";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi cập nhật: " + ex.Message;
                AppendLog("[LỖI] Cập nhật thất bại: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Tải file cập nhật (hiện % lên thanh tiến trình) rồi cài đặt.</summary>
        private async Task DownloadAndInstallAsync(UpdateService svc, UpdateInfo info)
        {
            var dir = Path.Combine(Path.GetTempPath(), "SqlMigrator_Update");
            var dest = Path.Combine(dir, info.FileName);
            _lblStatus.Text = $"Đang tải {info.FileName}...";
            var progress = new Progress<double>(p =>
            {
                _progressBar.Value = Math.Clamp((int)p, 0, 100);
                _lblStatus.Text = $"Đang tải {info.FileName}... {p:0}%";
            });
            await svc.DownloadAsync(info.DownloadUrl, dest, progress);
            AppendLog($"[THÔNG TIN] Đã tải xong: {dest}");

            if (info.IsInstaller)
            {
                // Bản cài đặt: chạy setup rồi thoát để installer thay file.
                AppendLog("[THÔNG TIN] Đang mở bộ cài đặt mới, app sẽ tự thoát...");
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
                Application.Exit();
                return;
            }

            ApplyPortableUpdate(dest);
        }

        /// <summary>
        /// Bản portable (zip): bung ra thư mục tạm rồi hẹn script chép đè sau khi
        /// app thoát, xong tự mở lại app mới.
        /// </summary>
        private void ApplyPortableUpdate(string zipPath)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stage = Path.Combine(Path.GetTempPath(), "SqlMigrator_Update", "stage");
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
            Directory.CreateDirectory(stage);

            ZipFile.ExtractToDirectory(zipPath, stage, overwriteFiles: true);

            // Zip có thể bọc thêm 1 thư mục con chứa exe — tìm thư mục có exe.
            var source = stage;
            var nested = Directory.GetDirectories(stage)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "SqlMigrator.exe")));
            if (nested != null)
                source = nested;

            var exePath = Path.Combine(appDir, "SqlMigrator.exe");
            var cmdPath = Path.Combine(Path.GetTempPath(), "SqlMigrator_Update", "apply-update.cmd");
            var pid = Environment.ProcessId;
            File.WriteAllText(cmdPath,
                "@echo off\r\n"
                + ":wait\r\n"
                + $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL && (timeout /t 1 /nobreak >NUL & goto wait)\r\n"
                + $"robocopy \"{source}\" \"{appDir}\" /E /IS /IT /NFL /NDL /NJH /NJS\r\n"
                + $"start \"\" \"{exePath}\"\r\n"
                + "(goto) 2>nul & del \"%~f0\"\r\n");

            AppendLog("[THÔNG TIN] Đang áp dụng bản portable mới, app sẽ tự thoát và mở lại...");
            Process.Start(new ProcessStartInfo(cmdPath)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Application.Exit();
        }

        /// <summary>Nạp icon app từ resource nhúng (rớt êm nếu thiếu).</summary>
        private static Icon? LoadAppIcon()
        {
            try
            {
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (stream == null)
                    return null;
                using (stream)
                    return new Icon(stream);
            }
            catch
            {
                return null;
            }
        }

        private void SetBusy(bool busy)
        {
            _btnStart.Enabled = !busy;
            _btnVerify.Enabled = !busy;
            _btnReconcile.Enabled = !busy;
            _btnCheckUpdate.Enabled = !busy;
            _btnLoadTables.Enabled = !busy;
            _btnLoadObjects.Enabled = !busy;
            _btnCancel.Enabled = busy;
            _sourceEditor.Enabled = !busy;
            _destEditor.Enabled = !busy;
            _progressBar.Visible = true;
            if (!busy)
                _progressBar.Value = 0;
            UpdateSyncButtons();
        }
    }
}