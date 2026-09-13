using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services.Backup;
using SqlMigrator.UI.Services;

namespace SqlMigrator.UI.Components
{
    /// <summary>
    /// Hộp thoại lập lịch sao lưu tự động: danh sách job + tạo/sửa/xóa/bật-tắt,
    /// chạy thử ngay, đăng ký/gỡ Windows Task Scheduler (schtasks.exe).
    /// Job nhúng bản sao profile nguồn nên Task Scheduler chạy headless được.
    /// </summary>
    public sealed class BackupScheduleDialog : Form
    {
        private readonly ConnectionProfile _source;
        private readonly SecureConnectionStringBuilder _builder;
        private readonly Action<string> _log;

        private readonly DataGridView _dgvJobs = new();
        private readonly TextBox _txtName = new() { Dock = DockStyle.Fill };
        private readonly CheckedListBox _lstDbs = new() { Dock = DockStyle.Fill, Height = 90, CheckOnClick = true };
        private readonly TextBox _txtFolder = new() { Dock = DockStyle.Fill };
        private readonly RadioButton _rdoFull = new() { Text = "Full", Checked = true, AutoSize = true };
        private readonly RadioButton _rdoDiff = new() { Text = "Diff", AutoSize = true };
        private readonly CheckBox _chkCompress = new() { Text = "Nén", Checked = true, AutoSize = true };
        private readonly CheckBox _chkEnabled = new() { Text = "Bật job", Checked = true, AutoSize = true };
        private readonly ComboBox _cmbFreq = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly DateTimePicker _dtpTime = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true };
        private readonly NumericUpDown _numHours = new() { Minimum = 1, Maximum = 24, Value = 6, Width = 60 };
        private readonly ComboBox _cmbDay = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _btnSave = new() { Text = "Lưu job", AutoSize = true };
        private readonly Button _btnDelete = new() { Text = "Xóa", AutoSize = true };
        private readonly Button _btnToggle = new() { Text = "Bật/Tắt", AutoSize = true };
        private readonly Button _btnRunNow = new() { Text = "Chạy ngay", AutoSize = true };
        private readonly Button _btnRegister = new() { Text = "Đăng ký lịch", AutoSize = true };
        private readonly Button _btnUnregister = new() { Text = "Gỡ lịch", AutoSize = true };
        private readonly Label _lblStatus = new() { Text = "Sẵn sàng.", AutoSize = true };

        private List<BackupJob> _jobs = new();

        public BackupScheduleDialog(
            ConnectionProfile source, IReadOnlyList<string> databases,
            string folder, bool fullBackup,
            SecureConnectionStringBuilder builder, Action<string> log)
        {
            _source = source;
            _builder = builder;
            _log = log;
            Text = "Lập lịch sao lưu tự động";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 560);
            Font = new Font("Segoe UI", 9.25F);

            _cmbFreq.Items.Add("Hàng ngày");
            _cmbFreq.Items.Add("Hàng giờ");
            _cmbFreq.Items.Add("Hàng tuần");
            _cmbFreq.SelectedIndex = 0;
            _cmbDay.Items.AddRange(new object[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" });
            _cmbDay.SelectedIndex = 0;
            _txtFolder.Text = folder;
            _rdoFull.Checked = fullBackup;
            _rdoDiff.Checked = !fullBackup;
            foreach (var db in databases)
                _lstDbs.Items.Add(db, true);

            _dgvJobs.Dock = DockStyle.Fill;
            _dgvJobs.AllowUserToAddRows = false;
            _dgvJobs.ReadOnly = true;
            _dgvJobs.MultiSelect = false;
            _dgvJobs.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dgvJobs.RowHeadersVisible = false;
            _dgvJobs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            _dgvJobs.Columns.Add("Name", "Tên job");
            _dgvJobs.Columns.Add("Dbs", "Database");
            _dgvJobs.Columns.Add("Sched", "Lịch");
            _dgvJobs.Columns.Add("On", "Bật");
            _dgvJobs.SelectionChanged += (_, _) => LoadSelectedToEditor();

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            editor.Controls.Add(new Label { Text = "Tên job:", AutoSize = true }, 0, 0);
            editor.Controls.Add(_txtName, 1, 0);
            editor.Controls.Add(new Label { Text = "Database:", AutoSize = true }, 0, 1);
            editor.Controls.Add(_lstDbs, 1, 1);
            editor.Controls.Add(new Label { Text = "Thư mục (server):", AutoSize = true }, 0, 2);
            editor.Controls.Add(_txtFolder, 1, 2);
            var typeRow = new FlowLayoutPanel { AutoSize = true };
            typeRow.Controls.Add(_rdoFull);
            typeRow.Controls.Add(_rdoDiff);
            typeRow.Controls.Add(_chkCompress);
            typeRow.Controls.Add(_chkEnabled);
            editor.Controls.Add(new Label { Text = "Loại:", AutoSize = true }, 0, 3);
            editor.Controls.Add(typeRow, 1, 3);
            var schedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            schedRow.Controls.Add(_cmbFreq);
            schedRow.Controls.Add(new Label { Text = "Giờ:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            schedRow.Controls.Add(_dtpTime);
            schedRow.Controls.Add(new Label { Text = "Mỗi (giờ):", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            schedRow.Controls.Add(_numHours);
            schedRow.Controls.Add(new Label { Text = "Thứ:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            schedRow.Controls.Add(_cmbDay);
            editor.Controls.Add(new Label { Text = "Lịch:", AutoSize = true }, 0, 4);
            editor.Controls.Add(schedRow, 1, 4);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            buttons.Controls.Add(_btnSave);
            buttons.Controls.Add(_btnDelete);
            buttons.Controls.Add(_btnToggle);
            buttons.Controls.Add(_btnRunNow);
            buttons.Controls.Add(_btnRegister);
            buttons.Controls.Add(_btnUnregister);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(_dgvJobs, 0, 0);
            root.Controls.Add(editor, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            root.Controls.Add(_lblStatus, 0, 3);
            Controls.Add(root);

            _btnSave.Click += async (_, _) => await SaveJobAsync();
            _btnDelete.Click += async (_, _) => await DeleteJobAsync();
            _btnToggle.Click += async (_, _) => await ToggleJobAsync();
            _btnRunNow.Click += async (_, _) => await RunNowAsync();
            _btnRegister.Click += async (_, _) => await RegisterAsync();
            _btnUnregister.Click += async (_, _) => await UnregisterAsync();

            _ = RefreshJobsAsync();
        }

        private BackupJob? SelectedJob()
        {
            if (_dgvJobs.SelectedRows.Count == 0) return null;
            var id = _dgvJobs.SelectedRows[0].Cells[0].Value?.ToString() ?? "";
            // Id lưu trong Tag của dòng.
            var tag = _dgvJobs.SelectedRows[0].Tag as string ?? "";
            foreach (var job in _jobs)
            {
                if (job.Id == tag || job.Name == id)
                    return job;
            }
            return null;
        }

        private static string Describe(BackupJob job)
        {
            return job.Frequency switch
            {
                BackupJobFrequency.Hourly => $"mỗi {job.IntervalHours} giờ",
                BackupJobFrequency.Weekly => $"{job.DayOfWeek} {job.TimeOfDay}",
                _ => $"hàng ngày {job.TimeOfDay}"
            };
        }

        private async Task RefreshJobsAsync()
        {
            _jobs = (await BackupJobStore.LoadAsync()).ToList();
            _dgvJobs.Rows.Clear();
            foreach (var job in _jobs)
            {
                var row = _dgvJobs.Rows.Add(job.Name, string.Join(", ", job.Databases),
                    Describe(job), job.Enabled ? "Có" : "Không");
                _dgvJobs.Rows[row].Tag = job.Id;
            }
        }

        private void LoadSelectedToEditor()
        {
            var job = SelectedJob();
            if (job == null) return;
            _txtName.Text = job.Name;
            for (var i = 0; i < _lstDbs.Items.Count; i++)
            {
                var name = _lstDbs.Items[i]?.ToString() ?? "";
                _lstDbs.SetItemChecked(i, job.Databases.Contains(name));
            }
            _txtFolder.Text = job.Folder;
            _rdoFull.Checked = job.FullBackup;
            _rdoDiff.Checked = !job.FullBackup;
            _chkEnabled.Checked = job.Enabled;
            _cmbFreq.SelectedIndex = job.Frequency == BackupJobFrequency.Hourly ? 1
                : job.Frequency == BackupJobFrequency.Weekly ? 2 : 0;
            if (TimeSpan.TryParse(job.TimeOfDay, out var t))
                _dtpTime.Value = DateTime.Today.Add(t);
            _numHours.Value = Math.Clamp(job.IntervalHours, 1, 24);
            _cmbDay.SelectedItem = job.DayOfWeek;
        }

        private BackupJob ReadEditor(string? keepId = null)
        {
            var dbs = new List<string>();
            foreach (var item in _lstDbs.CheckedItems)
            {
                if (item?.ToString() is string db && !string.IsNullOrWhiteSpace(db))
                    dbs.Add(db);
            }
            return new BackupJob
            {
                Id = keepId ?? Guid.NewGuid().ToString("N"),
                Name = _txtName.Text.Trim(),
                Enabled = _chkEnabled.Checked,
                Source = _source,
                Databases = dbs,
                Folder = _txtFolder.Text.Trim().TrimEnd('\\'),
                FullBackup = _rdoFull.Checked,
                Compression = _chkCompress.Checked,
                Frequency = _cmbFreq.SelectedIndex == 1 ? BackupJobFrequency.Hourly
                    : _cmbFreq.SelectedIndex == 2 ? BackupJobFrequency.Weekly
                    : BackupJobFrequency.Daily,
                TimeOfDay = _dtpTime.Value.ToString("HH:mm"),
                IntervalHours = (int)_numHours.Value,
                DayOfWeek = _cmbDay.SelectedItem?.ToString() ?? "SUN"
            };
        }

        private async Task SaveJobAsync()
        {
            var selected = SelectedJob();
            var job = ReadEditor(selected?.Id);
            if (string.IsNullOrWhiteSpace(job.Name))
            {
                MessageBox.Show("Nhập tên job.", "Lưu job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (job.Databases.Count == 0)
            {
                MessageBox.Show("Check chọn ít nhất 1 database.", "Lưu job", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var jobs = (await BackupJobStore.LoadAsync()).ToList();
            var idx = jobs.FindIndex(j => j.Id == job.Id);
            if (idx >= 0) jobs[idx] = job;
            else jobs.Add(job);
            await BackupJobStore.SaveAsync(jobs);
            _lblStatus.Text = $"Đã lưu job '{job.Name}'.";
            await RefreshJobsAsync();
        }

        private async Task DeleteJobAsync()
        {
            var selected = SelectedJob();
            if (selected == null) return;
            if (MessageBox.Show($"Xóa job '{selected.Name}' (đồng thời gỡ lịch Task Scheduler)?",
                "Xóa job", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            await new TaskSchedulerService().UnregisterAsync(selected);
            var jobs = (await BackupJobStore.LoadAsync()).ToList();
            jobs.RemoveAll(j => j.Id == selected.Id);
            await BackupJobStore.SaveAsync(jobs);
            _lblStatus.Text = $"Đã xóa job '{selected.Name}'.";
            await RefreshJobsAsync();
        }

        private async Task ToggleJobAsync()
        {
            var selected = SelectedJob();
            if (selected == null) return;
            selected.Enabled = !selected.Enabled;
            var jobs = (await BackupJobStore.LoadAsync()).ToList();
            var idx = jobs.FindIndex(j => j.Id == selected.Id);
            if (idx >= 0) jobs[idx] = selected;
            await BackupJobStore.SaveAsync(jobs);
            _lblStatus.Text = $"Job '{selected.Name}' đã {(selected.Enabled ? "bật" : "tắt")}.";
            await RefreshJobsAsync();
        }

        private async Task RunNowAsync()
        {
            var selected = SelectedJob();
            if (selected == null)
            {
                MessageBox.Show("Chọn một job để chạy thử.", "Chạy ngay",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                _lblStatus.Text = $"Đang chạy thử job '{selected.Name}'...";
                var logger = new UiLogger(msg =>
                {
                    _lblStatus.Text = msg;
                    _log(msg);
                }, "BackupJob");
                var code = await new BackupJobRunner(
                    new DpapiDataProtector(), _builder, logger).RunAsync(selected.Id);
                _lblStatus.Text = code == 0 ? "Chạy thử xong." : "Chạy thử có lỗi (xem nhật ký).";
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Lỗi chạy thử: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RegisterAsync()
        {
            var selected = SelectedJob();
            if (selected == null) return;
            var exe = Application.ExecutablePath;
            var result = await new TaskSchedulerService().RegisterAsync(exe, selected);
            _lblStatus.Text = result.Success
                ? $"Đã đăng ký lịch '{selected.Name}' vào Task Scheduler."
                : "Đăng ký thất bại: " + result.Output;
            if (!result.Success)
                MessageBox.Show("Không đăng ký được lịch:\n" + result.Output,
                    "Task Scheduler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private async Task UnregisterAsync()
        {
            var selected = SelectedJob();
            if (selected == null) return;
            var result = await new TaskSchedulerService().UnregisterAsync(selected);
            _lblStatus.Text = result.Success ? "Đã gỡ lịch." : "Gỡ thất bại: " + result.Output;
        }

        private void SetBusy(bool busy)
        {
            _btnSave.Enabled = !busy;
            _btnRunNow.Enabled = !busy;
            _btnRegister.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
