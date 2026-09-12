using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Security;
using SqlMigrator.UI.Services;

namespace SqlMigrator.UI.Components
{
    /// <summary>
    /// Khối nhập thông tin kết nối SQL Server dùng chung cho trang Nguồn và trang Đích.
    /// Toàn bộ yêu cầu bảo mật được áp dụng: mật khẩu che dấu (mask), lưu profile qua
    /// DPAPI, chuỗi kết nối luôn bật mã hóa, không hiển thị chuỗi kết nối hay mật khẩu.
    /// </summary>
    public sealed class ConnectionEditor : UserControl
    {
        /// <summary>Mục trắng đứng đầu danh sách database để buộc người dùng chủ động chọn, tránh nhầm DB nguồn/đích.</summary>
        public const string DbPlaceholder = "-- Chọn cơ sở dữ liệu --";

        private readonly IDataProtector _protector;
        private readonly IConnectionProfileStore _profileStore;
        private readonly SecureConnectionStringBuilder _builder;
        private readonly string _profileKind;

        private readonly TextBox _txtServer = new();
        private readonly TextBox _txtUser = new();
        private readonly TextBox _txtPassword = new();
        private readonly ComboBox _cmbDatabase = new();
        private readonly ComboBox _cmbProfile = new();
        private readonly ComboBox _compatLevelHint = new();
        private readonly RadioButton _rdoWindows = new() { Text = "Xác thực Windows" };
        private readonly RadioButton _rdoSql = new() { Text = "Xác thực SQL Server" };
        private readonly CheckBox _chkEncrypt = new() { Text = "Mã hóa kết nối" };
        private readonly CheckBox _chkTrustCertificate = new();
        private readonly Label _lblSafeSummary = new() { ForeColor = Color.Gray, AutoSize = true };
        private readonly Label _lblDbs = new() { Text = "Database:", AutoSize = true };
        private readonly Label _lblServer = new() { Text = "Server / Instance:", AutoSize = true };
        private readonly Label _lblUser = new() { Text = "Tên đăng nhập:", AutoSize = true };
        private readonly Label _lblPassword = new() { Text = "Mật khẩu:", AutoSize = true };
        private readonly Label _lblDbData = new() { Text = "Thư mục DB (.mdf):", AutoSize = true };
        private readonly Label _lblDbLog = new() { Text = "Thư mục DB (.ldf):", AutoSize = true };
        private readonly Button _btnTest = new() { Text = "Kiểm tra kết nối", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Button _btnLoadDatabases = new() { Text = "Connect DB", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Button _btnSaveProfile = new() { Text = "Lưu", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Button _btnDeleteProfile = new() { Text = "Xóa", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Label _lblConnStatus = new() { AutoSize = true, ForeColor = Color.Gray };
        private readonly ErrorProvider _errorProvider = new();
        private readonly TextBox _txtDbDataPath = new() { Width = 200 };
        private readonly TextBox _txtDbLogPath = new() { Width = 200 };
        private readonly Button _btnBrowseDataPath = new() { Text = "Chọn…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Button _btnBrowseLogPath = new() { Text = "Chọn…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Button _btnCreateDb = new() { Text = "Tạo DB", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private bool _showDbFileFields;

        /// <summary>Loại thẻ profile (vd "source" / "dest") để không đè profile của trang khác.</summary>
        public ConnectionEditor(IDataProtector protector, IConnectionProfileStore profileStore, SecureConnectionStringBuilder builder, string profileKind)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _profileKind = profileKind ?? throw new ArgumentNullException(nameof(profileKind));

            BuildLayout();
            WireEvents();
        }

        /// <summary>Đối tượng dùng để lấy danh sách database (gán bởi trang cha).</summary>
        public Func<ConnectionProfile, Task<List<string>>>? DatabaseFetcher { get; set; }

        /// <summary>Đối tượng dùng để kiểm tra kết nối (gán bởi trang cha).</summary>
        public Func<ConnectionProfile, Task>? ConnTester { get; set; }

        /// <summary>Delegate ghi dòng nhật ký vào khung realtime log (gán bởi form cha). Không bao giờ ghi secret.</summary>
        public Action<string>? LogSink { get; set; }

        /// <summary>Hiển thị thêm 2 ô chọn thư mục chứa file database (.mdf/.ldf) và nút Tạo DB trên server đích.</summary>
        public bool ShowDbFileFields
        {
            get => _showDbFileFields;
            set
            {
                _showDbFileFields = value;
                _txtDbDataPath.Visible = _txtDbLogPath.Visible = value;
                _btnBrowseDataPath.Visible = _btnBrowseLogPath.Visible = value;
                _lblDbData.Visible = _lblDbLog.Visible = value;
                _btnCreateDb.Visible = value;
            }
        }

        /// <summary>Thư mục chứa file .mdf đã chọn (bỏ trống = dùng mặc định của server đích).</summary>
        public string DestinationDataPath => _txtDbDataPath.Text.Trim();

        /// <summary>Thư mục chứa file .ldf đã chọn (bỏ trống = dùng mặc định của server đích).</summary>
        public string DestinationLogPath => _txtDbLogPath.Text.Trim();

        /// <summary>Delegate mở hộp thoại duyệt thư mục trên CHÍNH server đích (gán bởi form cha). Trả về đường dẫn đã chọn hoặc rỗng khi hủy.</summary>
        public Func<ConnectionEditor, CancellationToken, Task<string>>? FolderPicker { get; set; }

        /// <summary>
        /// Delegate tạo database đích mới (gán bởi form cha). Trả về chuỗi rỗng khi thành
        /// công, hoặc chuỗi lỗi an toàn (không chứa secret) để hiện cho người dùng.
        /// </summary>
        public Func<ConnectionEditor, CancellationToken, Task<string>>? CreateDatabaseHandler { get; set; }

        public SecureConnectionStringBuilder Builder => _builder;
        public string ProfileKind => _profileKind;

        private void BuildLayout()
        {
            AutoSize = false;

            _txtPassword.UseSystemPasswordChar = true;
            _cmbDatabase.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbDatabase.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _cmbDatabase.AutoCompleteSource = AutoCompleteSource.ListItems;
            _cmbProfile.DropDownStyle = ComboBoxStyle.DropDown;
            _rdoSql.Checked = true;
            _rdoWindows.AutoSize = true;
            _rdoSql.AutoSize = true;
            _chkEncrypt.Checked = true;
            _chkEncrypt.AutoSize = true;
            _chkTrustCertificate.AutoSize = true;
            _chkTrustCertificate.Text = "Chấp nhận chứng chỉ tự ký";

            // Root: lưới 2 cột — mỗi dòng có 2 cặp (nhãn | control) để tận dụng không gian ngang.
            // Cột 0,2 là nhãn (AutoSize); cột 1,3 là control (Percent, stretch theo chiều ngang).
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 4,
                Padding = new Padding(4),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));

            // Hàng 0: Server/Instance | Profile + Lưu/Xóa
            _txtServer.Dock = DockStyle.Fill;
            _cmbProfile.Dock = DockStyle.Fill;
            var profileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            profileRow.Controls.Add(_cmbProfile, 0, 0);
            profileRow.Controls.Add(_btnSaveProfile, 1, 0);
            profileRow.Controls.Add(_btnDeleteProfile, 2, 0);
            root.Controls.Add(_lblServer, 0, 0);
            root.Controls.Add(_txtServer, 1, 0);
            root.Controls.Add(new Label { Text = "Profile:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) }, 2, 0);
            root.Controls.Add(profileRow, 3, 0);

            // Hàng 1: Xác thực | Database + Connect DB + Kiểm tra kết nối + Tạo DB
            var authPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            authPanel.Controls.Add(_rdoWindows);
            authPanel.Controls.Add(_rdoSql);
            _cmbDatabase.Dock = DockStyle.Fill;
            var dbRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dbRow.Controls.Add(_cmbDatabase, 0, 0);
            dbRow.Controls.Add(_btnLoadDatabases, 1, 0);
            dbRow.Controls.Add(_btnTest, 2, 0);
            dbRow.Controls.Add(_btnCreateDb, 3, 0);
            root.Controls.Add(new Label { Text = "Xác thực:", AutoSize = true }, 0, 1);
            root.Controls.Add(authPanel, 1, 1);
            root.Controls.Add(_lblDbs, 2, 1);
            root.Controls.Add(dbRow, 3, 1);

            // Hàng 2: Tên đăng nhập | Trạng thái
            _txtUser.Dock = DockStyle.Fill;
            var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            statusRow.Controls.Add(new Label { Text = "Trạng thái:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, 0);
            statusRow.Controls.Add(_lblConnStatus, 1, 0);
            root.Controls.Add(_lblUser, 0, 2);
            root.Controls.Add(_txtUser, 1, 2);
            root.Controls.Add(new Label { Text = "Bảo mật:", AutoSize = true }, 2, 2);
            root.Controls.Add(statusRow, 3, 2);

            // Hàng 3: Mật khẩu | Mã hóa + Trust (checkbox)
            _txtPassword.Dock = DockStyle.Fill;
            var chkPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            chkPanel.Controls.Add(_chkEncrypt);
            chkPanel.Controls.Add(_chkTrustCertificate);
            root.Controls.Add(_lblPassword, 0, 3);
            root.Controls.Add(_txtPassword, 1, 3);
            root.Controls.Add(chkPanel, 3, 3);

            // Hàng 4: Thư mục DB (.mdf) + Chọn… | Thư mục DB (.ldf) + Chọn… (chỉ hiện với server đích)
            _txtDbDataPath.Dock = DockStyle.Fill;
            _txtDbLogPath.Dock = DockStyle.Fill;
            var dataPathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            dataPathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            dataPathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dataPathRow.Controls.Add(_txtDbDataPath, 0, 0);
            dataPathRow.Controls.Add(_btnBrowseDataPath, 1, 0);
            var logPathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            logPathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            logPathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            logPathRow.Controls.Add(_txtDbLogPath, 0, 0);
            logPathRow.Controls.Add(_btnBrowseLogPath, 1, 0);
            root.Controls.Add(_lblDbData, 0, 4);
            root.Controls.Add(dataPathRow, 1, 4);
            root.Controls.Add(_lblDbLog, 2, 4);
            root.Controls.Add(logPathRow, 3, 4);

            // Hàng 5: Tóm tắt an toàn (trải đều cả 2 cột control)
            root.Controls.Add(_lblSafeSummary, 1, 5);
            root.SetColumnSpan(_lblSafeSummary, 3);
            root.RowCount = 6;

            root.RowStyles.Clear();
            for (var i = 0; i < 5; i++)
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Bọc trong panel cuộn: khi form quá thấp → scrollbar thay vì cắt nút.
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BorderStyle = BorderStyle.None };
            scroller.Controls.Add(root);
            Controls.Add(scroller);

            // Hàng thư mục DB chỉ hiện khi bật ShowDbFileFields (mặc định không).
            ShowDbFileFields = _showDbFileFields;
        }

        private void WireEvents()
        {
            _rdoWindows.CheckedChanged += (_, _) => UpdateAuthState();
            _rdoSql.CheckedChanged += (_, _) => UpdateAuthState();
            _chkTrustCertificate.Tag = "chưa dùng";
            _btnTest.Click += async (_, _) => await TestConnectionAsync();
            _btnLoadDatabases.Click += async (_, _) => await LoadDatabasesAsync();
            _btnSaveProfile.Click += (_, _) => SaveProfile();
            _btnDeleteProfile.Click += (_, _) => DeleteProfile();
            _btnBrowseDataPath.Click += async (_, _) => _txtDbDataPath.Text = await PickFolderFromServerAsync();
            _btnBrowseLogPath.Click += async (_, _) => _txtDbLogPath.Text = await PickFolderFromServerAsync();
            _btnCreateDb.Click += async (_, _) => await CreateDatabaseAsync();
            _cmbProfile.SelectedIndexChanged += (_, _) => LoadProfileIntoControls();
            _cmbDatabase.TextChanged += (_, _) => MaybeAutoSaveDefault();
            UpdateAuthState();
            RefreshStoredProfiles();
            AutoLoadDefaultProfile();
        }

        private void UpdateAuthState()
        {
            var sql = _rdoSql.Checked;
            _txtUser.Enabled = sql;
            _txtPassword.Enabled = sql;
        }

        /// <summary>Mở hộp thoại duyệt thư mục trên CHÍNH server đích (dựa kết nối SQL hiện có), điền vào ô TextBox.</summary>
        private async Task<string> PickFolderFromServerAsync()
        {
            if (FolderPicker == null)
            {
                _errorProvider.SetError(_btnBrowseDataPath, "Chưa có dịch vụ duyệt thư mục server đích.");
                return string.Empty;
            }

            try
            {
                // Chỉ gọi picker; kết quả do form cha (MainForm) trả về.
                return await FolderPicker(this, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_btnBrowseDataPath, "Không duyệt được thư mục: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Tạo database đích mới khi bấm nút "Tạo DB" (chỉ hiện ở khối đích): đọc tên DB từ
        /// ô database, ủy thác tạo thật cho form cha qua <see cref="CreateDatabaseHandler"/>,
        /// rồi nạp lại danh sách database để có thể chọn DB vừa tạo.
        /// </summary>
        private async Task CreateDatabaseAsync()
        {
            if (CreateDatabaseHandler == null)
            {
                _errorProvider.SetError(_btnCreateDb, "Chưa có dịch vụ tạo database đích.");
                return;
            }

            // Phải nhập (không chọn placeholder) một tên DB đích trước khi tạo.
            var dbName = _cmbDatabase.Text.Trim();
            if (string.IsNullOrWhiteSpace(dbName) || dbName == DbPlaceholder)
            {
                MessageBox.Show("Vui lòng nhập tên database đích mới cần tạo trước khi bấm 'Tạo DB'.",
                    "Tạo database đích", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var error = await CreateDatabaseHandler(this, CancellationToken.None).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _errorProvider.SetError(_btnCreateDb, error);
                    MessageBox.Show(error, "Không tạo được database đích", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                SetConnStatus("Đã tạo database đích '" + dbName + "'.", true);
                Log("[THÔNG TIN] " + RoleLabel + ": đã tạo database đích '" + dbName + "'.");
                await LoadDatabasesAsync(preserveSelection: true);
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_btnCreateDb, "Không tạo được database đích: " + ex.Message);
            }
        }

        /// <summary>Hiển thị lại toàn bộ tóm tắt an toàn (không chứa mật khẩu hay chuỗi kết nối).</summary>
        private void UpdateSafeSummary()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_txtServer.Text)) parts.Add("máy chủ: " + _txtServer.Text.Trim());
            if (!string.IsNullOrWhiteSpace(_cmbDatabase.Text)) parts.Add("db: " + _cmbDatabase.Text.Trim());
            if (_rdoSql.Checked && !string.IsNullOrWhiteSpace(_txtUser.Text)) parts.Add("đăng nhập: " + _txtUser.Text.Trim());
            _lblSafeSummary.Text = parts.Count == 0
                ? "Chưa nhập thông tin kết nối."
                : "Tóm tắt an toàn: " + string.Join(" · ", parts) + " (mật khẩu được mã hóa, không lưu văn bản thuần)";
        }

        /// <summary>Nhãn vai trò của khối kết nối (nguồn/đích) để đưa vào log cho dễ phân biệt.</summary>
        private string RoleLabel => _profileKind == "dest" ? "Server đích" : "Server nguồn";

        /// <summary>Chỉ lấy server/database làm chuỗi an toàn để log (tuyệt đối không xuất mật khẩu).</summary>
        private static string SafeServer(ConnectionProfile profile) =>
            string.IsNullOrWhiteSpace(profile.Database)
                ? profile.Server
                : profile.Server + "/" + profile.Database;

        /// <summary>Cập nhật nhãn trạng thái ngay dưới khối server (hỗ trợ đọc nhanh tại chỗ).</summary>
        private void SetConnStatus(string text, bool success)
        {
            _lblConnStatus.Text = text;
            _lblConnStatus.ForeColor = success ? Color.ForestGreen : Color.Firebrick;
        }

        /// <summary>Ghi một dòng vào khung realtime log của form chính nếu có delegate (không cần thread-safe riêng).</summary>
        private void Log(string line) => LogSink?.Invoke(line);

        private void RefreshStoredProfiles()
        {
            try
            {
                // Hiển thị mọi profile thuộc khối này (source/dest). Profile cũ chưa có Kind
                // thì dùng tiền tố tên "source:"/"dest:" để vẫn hiện được.
                var profiles = _profileStore.LoadAll()
                    .Where(p => p.Kind.Equals(_profileKind, StringComparison.OrdinalIgnoreCase)
                                || p.Name.StartsWith(_profileKind + ":", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var selected = _cmbProfile.SelectedItem as string;
                _cmbProfile.Items.Clear();
                foreach (var p in profiles) _cmbProfile.Items.Add(p.Name);

                if (selected != null && _cmbProfile.Items.Contains(selected)) _cmbProfile.SelectedItem = selected;
                if (_cmbProfile.SelectedIndex < 0 && _cmbProfile.Items.Count > 0) _cmbProfile.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_cmbProfile, "Không đọc được profile: " + ex.Message);
            }
        }

        /// <summary>
        /// Tự động lưu các giá trị vừa nhập vào profile mặc định (<c>source:default</c> / <c>dest:default</c>)
        /// khi kết nối thành công, để lần sau mở lại có sẵn thông tin mà không cần bấm nút Lưu.
        /// </summary>
        private void AutoSaveDefaultProfile()
        {
            try
            {
                var profile = ReadProfile();
                if (profile == null) return;

                // Dùng tên cố định + id cố định để luôn cập nhật đúng một bản ghi mặc định, không tạo bản sao rác.
                profile.Name = _profileKind + ":default";
                profile.Id = "default:" + _profileKind;
                _profileStore.Upsert(profile);
            }
            catch (Exception ex)
            {
                // Chỉ là tiện ích, không được chặn luồng kết nối chính.
                _errorProvider.SetError(_btnSaveProfile, "Không tự lưu profile: " + ex.Message);
            }
        }

        /// <summary>
        /// Tự động lưu profile mặc định khi người dùng vừa chọn database trên dropdown
        /// (kết nối thành công nhưng chưa chọn db cũng đã được lưu ở <see cref="AutoSaveDefaultProfile"/>).
        /// </summary>
        private void MaybeAutoSaveDefault()
        {
            // Chỉ lưu khi đã có server và db được chọn (kết thúc bằng ký tự hợp lệ, không phải chuỗi rỗng).
            var db = _cmbDatabase.Text;
            if (string.IsNullOrWhiteSpace(db) || db == DbPlaceholder) return;
            if (string.IsNullOrWhiteSpace(_txtServer.Text)) return;

            try
            {
                var profile = ReadProfile();
                if (profile == null) return;
                profile.Name = _profileKind + ":default";
                profile.Id = "default:" + _profileKind;
                _profileStore.Upsert(profile);
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_btnSaveProfile, "Không tự lưu profile: " + ex.Message);
            }
        }

        /// <summary>Tự động nạp profile mặc định khi khởi động nếu đã từng lưu (không cần bấm gì cả).</summary>
        private void AutoLoadDefaultProfile()
        {
            try
            {
                var profiles = _profileStore.LoadAll();
                var def = profiles.FirstOrDefault(p => p.Id == "default:" + _profileKind);
                if (def == null) return;

                // Nạp profile mặc định lên đầu danh sách và chọn nó để tự điền các ô.
                var name = def.Name;
                if (_cmbProfile.Items.Contains(name)) _cmbProfile.SelectedItem = name;
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_cmbProfile, "Không nạp được profile mặc định: " + ex.Message);
            }
        }

        /// <summary>Đọc toàn bộ giá trị người dùng đã nhập thành một <see cref="ConnectionProfile"/> (mật khẩu chưa mã hóa).</summary>
        public ConnectionProfile? ReadProfile(int? userEnteredTestPort = null)
        {
            var server = _txtServer.Text.Trim();
            if (string.IsNullOrWhiteSpace(server))
            {
                _errorProvider.SetError(_txtServer, "Vui lòng nhập server nguồn/đích.");
                return null;
            }

            // Mục trắng "-- Chọn cơ sở dữ liệu --" được coi là chưa chọn database.
            var database = _cmbDatabase.Text.Trim() == DbPlaceholder
                ? string.Empty
                : _cmbDatabase.Text.Trim();
            var auth = _rdoSql.Checked ? AuthenticationMode.SqlLogin : AuthenticationMode.Windows;
            var userName = auth == AuthenticationMode.SqlLogin ? _txtUser.Text.Trim() : string.Empty;
            var password = auth == AuthenticationMode.SqlLogin ? _txtPassword.Text : string.Empty;

            var profile = new ConnectionProfile
            {
                Name = _profileKind + ":" + (database.Length > 0 ? database : "_master"),
                Kind = _profileKind,
                Server = server,
                Database = database,
                Authentication = auth,
                UserName = userName,
                EncryptConnection = _chkEncrypt.Checked,
                TrustServerCertificate = _chkTrustCertificate.Checked
            };

            if (auth == AuthenticationMode.SqlLogin)
                profile.ProtectedPassword = _protector.Protect(System.Text.Encoding.UTF8.GetBytes(password));

            UpdateSafeSummary();
            return profile;
        }

        /// <summary>Nạp giá trị từ profile đã lưu (giải mã mật khẩu để người dùng thấy khi lưu tiếp).</summary>
        private void LoadProfileIntoControls()
        {
            if (_cmbProfile.SelectedItem is not string name) return;
            var profile = _profileStore.LoadAll().FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (p.Kind.Equals(_profileKind, StringComparison.OrdinalIgnoreCase)
                    || p.Name.StartsWith(_profileKind + ":", StringComparison.OrdinalIgnoreCase)));
            if (profile == null) return;

            _txtServer.Text = profile.Server;
            _cmbDatabase.Text = profile.Database;
            if (profile.Authentication == AuthenticationMode.Windows)
            {
                _rdoWindows.Checked = true;
            }
            else
            {
                _rdoSql.Checked = true;
                _txtUser.Text = profile.UserName;
                _txtPassword.Text = profile.ProtectedPassword?.Length > 0
                    ? System.Text.Encoding.UTF8.GetString(_protector.Unprotect(profile.ProtectedPassword))
                    : string.Empty;
            }
            _chkEncrypt.Checked = profile.EncryptConnection;
            _chkTrustCertificate.Checked = profile.TrustServerCertificate;
            UpdateAuthState();
            UpdateSafeSummary();
        }

        private async Task TestConnectionAsync()
        {
            _btnTest.Enabled = false;
            try
            {
                var profile = ReadProfile();
                if (profile == null) return;
                if (ConnTester != null) await ConnTester(profile);
                else await DatabaseCatalog.TestConnectionAsync(profile, _builder);
                SetConnStatus("Kết nối thành công đến " + profile.Server + ".", true);
                Log("[THÔNG TIN] " + RoleLabel + " " + SafeServer(profile) + " kết nối thành công.");
                AutoSaveDefaultProfile();
                MessageBox.Show("Kết nối thành công đến máy chủ " + profile.Server + ".",
                    "Kiểm tra kết nối", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (SqlException ex)
            {
                HandleConnectionError(ex.Message);
            }
            catch (Exception ex)
            {
                HandleConnectionError(ex.Message);
            }
            finally
            {
                _btnTest.Enabled = true;
            }
        }

        /// <summary>Ghi lỗi kết nối vào realtime log + label trạng thái (không dùng icon ngay sau nút).</summary>
        private void HandleConnectionError(string message)
        {
            var hint = IsCertificateTrustError(new Exception(message))
                ? " (gợi ý: tích chọn 'Chấp nhận chứng chỉ tự ký (TrustServerCertificate)' rồi thử lại)"
                : string.Empty;
            SetConnStatus("Kết nối thất bại — " + _cmbDatabase.Text.Trim() + ".", false);
            Log("[LỖI] " + RoleLabel + " kết nối thất bại: " + message + hint);
        }

        private async Task LoadDatabasesAsync(bool preserveSelection = false)
        {
            _btnLoadDatabases.Enabled = false;
            try
            {
                var profile = ReadProfile();
                if (profile == null) return;
                var databases = DatabaseFetcher != null
                    ? await DatabaseFetcher(profile)
                    : await DatabaseCatalog.GetDatabasesAsync(profile, _builder);

                var previous = _cmbDatabase.Text;
                _cmbDatabase.Items.Clear();
                _cmbDatabase.Items.Add(DbPlaceholder);
                foreach (var d in databases) _cmbDatabase.Items.Add(d);

                // Mặc định KHÔNG tự chọn DB nào sau khi nạp: luôn để mục trắng để
                // người dùng chủ động chọn — tránh nhầm DB nguồn/đích.
                // Riêng luồng Tạo DB vừa gõ tên xong thì giữ lại tên đó (preserveSelection).
                if (preserveSelection && databases.Contains(previous)) _cmbDatabase.Text = previous;
                else _cmbDatabase.Text = DbPlaceholder;

                SetConnStatus("Đã kết nối " + SafeServer(profile) + " — thấy " + databases.Count + " database. Chọn database để tiếp tục.", true);
                Log("[THÔNG TIN] " + RoleLabel + " " + SafeServer(profile) + " đã kết nối, nạp được " + databases.Count + " database.");
                AutoSaveDefaultProfile();
            }
            catch (Exception ex)
            {
                var hint = IsCertificateTrustError(ex)
                    ? " (gợi ý: tích chọn 'Chấp nhận chứng chỉ tự ký (TrustServerCertificate)' rồi thử lại)"
                    : string.Empty;
                SetConnStatus("Không kết nối được — " + _cmbDatabase.Text.Trim() + ".", false);
                Log("[LỖI] " + RoleLabel + " không kết nối được: " + ex.Message + hint);
            }
            finally
            {
                _btnLoadDatabases.Enabled = true;
            }
        }

        /// <summary>Nhận diện lỗi SSL do chứng chỉ server không được hệ thống tin tưởng.</summary>
        private static bool IsCertificateTrustError(Exception ex)
        {
            var message = ex.Message;
            return message.Contains("authority that is not trusted", StringComparison.OrdinalIgnoreCase)
                || message.Contains("certificate chain", StringComparison.OrdinalIgnoreCase)
                || message.Contains("SSL Provider", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase);
        }

        private void SaveProfile()
        {
            try
            {
                var profile = ReadProfile();
                if (profile == null) return;

                // Ưu tiên tên đã gõ trên ô profile (combo có thể chỉnh sửa); chỉ mở hộp
                // thoại nhập tên khi ô trống — tránh hỏi lại tên thừa thãi.
                var typed = _cmbProfile.Text.Trim();
                var name = typed;
                if (string.IsNullOrWhiteSpace(name))
                {
                    var askName = new InputDialog("Tên profile", "Nhập tên profile để sử dụng lại lần sau:",
                        _profileKind + ":default");
                    if (askName.ShowDialog() != DialogResult.OK) return;
                    name = askName.Value.Trim();
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show("Tên profile không được để trống.", "Lưu profile",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                profile.Name = name;

                // Lưu trùng tên trong cùng khối (source/dest) là CẬP NHẬT: giữ lại Id cũ,
                // tránh tạo bản sao rác mỗi lần lưu lại.
                var existing = _profileStore.LoadAll()
                    .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                         && p.Kind.Equals(_profileKind, StringComparison.OrdinalIgnoreCase));
                if (existing != null) profile.Id = existing.Id;

                _profileStore.Upsert(profile);
                RefreshStoredProfiles();
                if (_cmbProfile.Items.Contains(name)) _cmbProfile.SelectedItem = name;
                MessageBox.Show("Đã lưu profile '" + name + "' được mã hóa DPAPI.",
                    "Lưu profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_btnSaveProfile, "Không lưu được profile: " + ex.Message);
            }
        }

        private void DeleteProfile()
        {
            if (_cmbProfile.SelectedItem is not string name) return;

            var confirm = MessageBox.Show("Xóa profile '" + name + "'?", "Xóa profile",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                var profile = _profileStore.LoadAll().FirstOrDefault(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && (p.Kind.Equals(_profileKind, StringComparison.OrdinalIgnoreCase)
                        || p.Name.StartsWith(_profileKind + ":", StringComparison.OrdinalIgnoreCase)));
                if (profile == null) return;

                _profileStore.Delete(profile.Id);
                RefreshStoredProfiles();
            }
            catch (Exception ex)
            {
                _errorProvider.SetError(_btnDeleteProfile, "Không xóa được profile: " + ex.Message);
            }
        }
    }

    /// <summary>Hộp thoại nhập một chuỗi đơn giản (dùng để đặt tên profile).</summary>
    public sealed class InputDialog : Form
    {
        private readonly TextBox _txt = new();
        public string Value => _txt.Text;

        public InputDialog(string title, string prompt, string defaultValue)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 420;
            Height = 140;

            var lbl = new Label { Text = prompt, AutoSize = true, Left = 12, Top = 12 };
            _txt.Left = 12;
            _txt.Top = 38;
            _txt.Width = 380;
            _txt.Text = defaultValue;

            var ok = new Button { Text = "Lưu", DialogResult = DialogResult.OK, Left = 260, Top = 70, Width = 70 };
            var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Left = 338, Top = 70, Width = 70 };
            Controls.Add(lbl);
            Controls.Add(_txt);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}