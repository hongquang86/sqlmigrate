using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;

namespace SqlMigrator.Core.Services.Backup
{
    /// <summary>
    /// Chạy một job sao lưu tự động ở chế độ headless (Task Scheduler gọi
    /// <c>SqlMigrator.exe --run-backup &lt;jobId&gt;</c>, không mở giao diện).
    /// Tên file theo mẫu &lt;DB&gt;_yyyyMMdd_HHmmss[_Diff].bak, ghi lịch sử như chạy tay.
    /// </summary>
    public sealed class BackupJobRunner
    {
        private readonly IDataProtector _protector;
        private readonly SecureConnectionStringBuilder _builder;
        private readonly ILogger _logger;

        public BackupJobRunner(IDataProtector protector, SecureConnectionStringBuilder builder, ILogger logger)
        {
            _protector = protector;
            _builder = builder;
            _logger = logger;
        }

        /// <summary>Chạy job; trả 0 khi mọi DB xong, 1 khi có lỗi hoặc job không hợp lệ.</summary>
        public async Task<int> RunAsync(string jobId, CancellationToken ct = default)
        {
            var store = new BackupJobStore();
            var job = await store.FindAsync(jobId, ct).ConfigureAwait(false);
            if (job == null)
            {
                _logger.LogError("Không tìm thấy job sao lưu '{Id}'.", jobId);
                return 1;
            }
            if (!job.Enabled)
            {
                _logger.LogWarning("Job '{Name}' đang tắt — bỏ qua.", job.Name);
                return 0;
            }
            if (job.Databases.Count == 0)
            {
                _logger.LogError("Job '{Name}' chưa chọn database nào.", job.Name);
                return 1;
            }
            if (string.IsNullOrWhiteSpace(job.Folder))
            {
                _logger.LogError("Job '{Name}' chưa có thư mục lưu trên server.", job.Name);
                return 1;
            }
            string connectionString;
            try
            {
                connectionString = _builder.Build(job.Source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job '{Name}': không dựng được kết nối nguồn.", job.Name);
                return 1;
            }
            var options = new MigrationOptions
            {
                DestinationConnectionString = connectionString,
                CommandTimeoutSeconds = 600
            };
            var service = new BackupService(options, _logger);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var failed = 0;
            foreach (var db in job.Databases)
            {
                ct.ThrowIfCancellationRequested();
                var file = job.Folder.TrimEnd('\\') + "\\" + db
                    + "_" + stamp + (job.FullBackup ? "" : "_Diff") + ".bak";
                var request = new BackupRequest
                {
                    Database = db,
                    BackupFile = file,
                    FullBackup = job.FullBackup,
                    Compression = job.Compression
                };
                _logger.LogInformation("Job '{Name}': sao lưu {Db}.", job.Name, db);
                BackupResult result;
                try
                {
                    result = await service.BackupDatabaseAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new BackupResult { Error = ex.Message };
                }
                if (!result.Success)
                    failed++;
                await BackupHistoryStore.AppendAsync(new BackupHistoryEntry
                {
                    AtUtc = DateTime.UtcNow,
                    Kind = (job.FullBackup ? "Sao lưu Full" : "Sao lưu Diff") + " (lịch: " + job.Name + ")",
                    Server = job.Source.Server,
                    Database = db,
                    File = file,
                    Success = result.Success,
                    Message = result.Success ? $"xong trong {result.Elapsed:mm\\:ss}" : result.Error ?? "lỗi"
                }, ct).ConfigureAwait(false);
            }
            _logger.LogInformation("Job '{Name}' xong: {Ok}/{Total} database.",
                job.Name, job.Databases.Count - failed, job.Databases.Count);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>Đường dẫn file log cho lần chạy headless (theo ngày).</summary>
        public static string LogFilePath()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SqlMigrator", "logs");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "backup-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }
    }
}
