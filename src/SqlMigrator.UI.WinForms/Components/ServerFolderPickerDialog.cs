using SqlMigrator.Core.Services;

namespace SqlMigrator.UI.Components
{
    /// <summary>
    /// Hộp thoại duyệt thư mục NGAY TRÊN server đích qua kết nối SQL (không cần share mạng).
    /// Bước 1: hiển thị thư mục mặc định (.mdf/.ldf) của server + nút "Dùng mặc định".
    /// Bước 2: danh sách ổ đĩa từ <c>xp_fixeddrives</c>; chọn ổ → liệt kê thư mục con cấp 1.
    /// Bước 3: cho nhập tay trực tiếp khi server quá hạn chế quyền (xp_dirtree bị từ chối).
    /// Trả về đường dẫn đã chọn (có thể bọc bằng mục chọn thư mục mới).
    /// Không bao giờ hiển thị chuỗi kết nối hay mật khẩu.
    /// </summary>
    public sealed class ServerFolderPickerDialog : Form
    {
        private readonly IServerFolderLister _lister;
        private readonly string _connectionString;
        private readonly CancellationToken _ct;

        private readonly ListBox _listDrives = new() { Dock = DockStyle.Fill };
        private readonly TextBox _txtPath = new() { Dock = DockStyle.Top };
        private readonly Button _btnUseDefault = new() { Text = "Dùng thư mục mặc định", AutoSize = true, Height = 32 };
        private readonly Button _btnUp = new() { Text = "Thư mục cấp trên", AutoSize = true, Height = 32 };
        private readonly Button _btnRefresh = new() { Text = "Làm mới", AutoSize = true, Height = 32 };
        private readonly Button _btnOK = new() { Text = "Chọn thư mục này", DialogResult = DialogResult.OK, Height = 32 };
        private readonly Button _btnCancel = new() { Text = "Hủy", DialogResult = DialogResult.Cancel, Height = 32 };
        private readonly Label _lblHint = new() { AutoSize = true, ForeColor = Color.Gray };
        private readonly List<string> _history = new();

        private string _currentRoot = string.Empty;
        private bool _isPopulating;

        public string SelectedPath => _txtPath.Text.Trim();

        public ServerFolderPickerDialog(IServerFolderLister lister, string connectionString, CancellationToken ct = default)
        {
            _lister = lister ?? throw new ArgumentNullException(nameof(lister));
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _ct = ct;

            Text = "Chọn thư mục chứa file database trên SERVER ĐÍCH";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 520);
            Font = new Font("Segoe UI", 9.25F);

            _lblHint.Dock = DockStyle.Top;
            _lblHint.Padding = new Padding(12, 10, 12, 0);
            _lblHint.Text = "Đang kết nối server đích để đọc thư mục...";

            _txtPath.Dock = DockStyle.Top;
            _txtPath.Padding = new Padding(12, 6, 12, 4);
            _txtPath.Font = new Font("Consolas", 9.5F);

            // Cột 1: đường dẫn tạm (bundled vào form) — dùng khi người dùng muốn gõ tay.
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(0) };

            var actions = CreateActionsPanel();
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // tiêu đề / hint
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // đường dẫn hiện tại
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // danh sách ổ/thư mục
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // nút chức năng
            layout.Controls.Add(_lblHint, 0, 0);
            layout.Controls.Add(_txtPath, 0, 1);
            listHost.Controls.Add(_listDrives);
            layout.Controls.Add(listHost, 0, 2);
            layout.Controls.Add(actions, 0, 3);
            root.Controls.Add(layout, 0, 0);
            Controls.Add(root);

            _lblHint.Text = "Đang kết nối server đích để đọc thư mục...";

            _btnUseDefault.Click += async (_, _) => await UseDefaultAsync();
            _listDrives.SelectedIndexChanged += (_, _) => UpdatePathFromSelection();
            _listDrives.DoubleClick += async (_, _) => await DrillIntoAsync();
            _btnUp.Click += (_, _) => GoUp();
            _btnRefresh.Click += async (_, _) => await RefreshAsync();
            _btnCancel.Click += (_, _) => Close();

            Shown += async (_, _) => await InitAsync();
        }

        private void BuildActionRow()
        {
            _listDrives.DrawMode = DrawMode.Normal;
            _listDrives.ItemHeight = 20;
        }

        private Control CreateActionsPanel()
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(12, 6, 12, 6) };
            flow.Controls.Add(_btnUseDefault);
            flow.Controls.Add(_btnUp);
            flow.Controls.Add(_btnRefresh);
            flow.Controls.Add(new Label { Text = "   ", AutoSize = true });
            flow.Controls.Add(_btnOK);
            flow.Controls.Add(_btnCancel);
            return flow;
        }

        private async Task InitAsync()
        {
            try
            {
                var defaults = await _lister.GetDefaultPathsAsync(_connectionString, _ct).ConfigureAwait(true);
                if (defaults != null && !defaults.IsEmpty)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(defaults.DataPath))
                        parts.Add(".mdf mặc định: " + defaults.DataPath);
                    if (!string.IsNullOrWhiteSpace(defaults.LogPath))
                        parts.Add(".ldf mặc định: " + defaults.LogPath);
                    _lblHint.Text = "Thư mục mặc định của server đích: " + string.Join(" · ", parts) +
                                    Environment.NewLine + "Chọn thư mục thấp hơn để tránh lỗi quyền ghi file.";
                }

                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _lblHint.Text = "Không đọc được thông tin server: " + ex.Message +
                                Environment.NewLine + "Bạn vẫn có thể nhập đường dẫn trực tiếp vào ô phía trên.";
                _txtPath.Select();
            }
        }

        private async Task RefreshAsync()
        {
            try
            {
                var drives = await _lister.GetDrivesAsync(_connectionString, _ct).ConfigureAwait(true);
                if (drives == null || drives.Count == 0)
                    throw new InvalidOperationException("Server đích không trả về ổ đĩa nào.");

                _isPopulating = true;
                try
                {
                    _listDrives.Items.Clear();
                    foreach (var drive in drives) _listDrives.Items.Add(drive);
                    _listDrives.HorizontalScrollbar = true;
                    if (_listDrives.Items.Count > 0)
                        _listDrives.SelectedIndex = 0;
                }
                finally
                {
                    _isPopulating = false;
                }
            }
            catch (Exception ex)
            {
                _lblHint.Text = "Không liệt kê được ổ đĩa trên server: " + ex.Message;
            }
        }

        /// <summary>
        /// Khi chọn (click 1 lần) một mục trong danh sách, cập nhật ô đường dẫn ngay để
        /// nút "Chọn thư mục này" dùng đúng thư mục đang được chọn — không phải thư mục
        /// cha như trước (gây ra việc tạo file ở gốc ổ đĩa).
        /// </summary>
        private void UpdatePathFromSelection()
        {
            if (_isPopulating) return;
            if (_listDrives.SelectedItem is not string item) return;

            var candidate = item.EndsWith(":", StringComparison.OrdinalIgnoreCase)
                ? item + "\\"
                : CombinePath(_currentRoot, item);
            _txtPath.Text = candidate;
        }

        private async Task DrillIntoAsync()
        {
            if (_listDrives.SelectedItem is not string item) return;

            var candidate = item.EndsWith(":", StringComparison.OrdinalIgnoreCase)
                ? item + "\\"
                : CombinePath(_currentRoot, item);

            try
            {
                var folders = await _lister.GetChildFoldersAsync(_connectionString, candidate, _ct).ConfigureAwait(true);

                // Luôn cập nhật đường dẫn hiện tại để có thể bấm "Chọn thư mục này".
                _history.Add(_txtPath.Text.Trim());
                _currentRoot = candidate;
                _txtPath.Text = candidate;

var childFolders = folders ?? Array.Empty<string>();
                if (childFolders.Count == 0)
                {
                    // Thư mục không có thư mục con (là thư mục đích kiểu leaf): làm rõ là
                    // có thể bấm OK luôn, tránh để người dùng tưởng đi chưa tới.
                    _listDrives.Items.Clear();
                    _lblHint.Text = "Thư mục '" + candidate + "' không có thư mục con cấp dưới. " +
                                    "Bấm 'Chọn thư mục này' để dùng làm nơi lưu file database.";
                    return;
                }

                _listDrives.Items.Clear();
                foreach (var f in childFolders)
                    _listDrives.Items.Add(f.EndsWith("\\", StringComparison.OrdinalIgnoreCase) ? f : f + "\\");
            }
            catch (Exception ex)
            {
                _lblHint.Text = "Không đọc được thư mục '" + candidate + "': " + ex.Message +
                                Environment.NewLine + "Bạn có thể nhập đường dẫn trực tiếp.";
            }
        }

        private void GoUp()
        {
            if (_history.Count == 0)
            {
                // Về danh sách ổ đĩa: reset gốc để lần duyệt sau gộp path đúng.
                _txtPath.Text = string.Empty;
                _currentRoot = string.Empty;
                _ = Task.Run(async () => await RefreshAsync().ConfigureAwait(true));
                return;
            }

            var previous = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);
            _currentRoot = previous ?? string.Empty;
            _txtPath.Text = previous ?? string.Empty;
            _ = Task.Run(async () => await DrillUpAsync(_currentRoot));
        }

        private async Task UseDefaultAsync()
        {
            // Nút này lấy thư mục mặc định .mdf làm kết quả; người dùng có thể chỉnh tay.
            try
            {
                var defaults = await _lister.GetDefaultPathsAsync(_connectionString, _ct).ConfigureAwait(true);
                if (defaults == null) return;
                _txtPath.Text = string.IsNullOrWhiteSpace(defaults.DataPath)
                    ? (defaults.LogPath ?? string.Empty)
                    : defaults.DataPath;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                _lblHint.Text = "Không đọc được thư mục mặc định: " + ex.Message;
            }
        }

        private string CombinePath(string root, string child)
        {
            var safe = root.Trim();
            var childName = child.Trim();
            if (safe.Length == 0) return childName;
            if (safe.EndsWith("\\", StringComparison.OrdinalIgnoreCase)) return safe + childName;
            return safe + "\\" + childName;
        }

        private async Task DrillUpAsync(string path)
        {
            try
            {
                var folders = await _lister.GetChildFoldersAsync(_connectionString, path, _ct).ConfigureAwait(true);
                var childFolders = folders ?? Array.Empty<string>();
                _listDrives.BeginInvoke(new Action(() =>
                {
                    _isPopulating = true;
                    try
                    {
                        _listDrives.Items.Clear();
                        foreach (var f in childFolders)
                            _listDrives.Items.Add(f.EndsWith("\\", StringComparison.OrdinalIgnoreCase) ? f : f + "\\");
                    }
                    finally
                    {
                        _isPopulating = false;
                    }
                }));
            }
            catch
            {
                // không lấp danh sách được thì cứ để ô path trống
            }
        }
    }
}