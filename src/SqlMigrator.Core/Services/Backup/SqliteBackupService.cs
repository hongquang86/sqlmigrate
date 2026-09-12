using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Backup
{
    /// <summary>
    /// Sao lưu SQLite: copy file (kèm kiểm tra toàn vẹn bản copy bằng
    /// <c>PRAGMA integrity_check</c>). Không cần server, chạy local hoàn toàn.
    /// </summary>
    public sealed class SqliteBackupService
    {
        private readonly ILogger _logger;

        public SqliteBackupService(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Copy file .db sang đích (ghi đè khi cho phép), báo % theo byte,
        /// rồi kiểm tra toàn vẹn bản copy. Trả kết quả + lỗi tiếng Việt.
        /// </summary>
        public async Task<BackupResult> BackupFileAsync(
            string sourceFile, string destFile, bool overwrite,
            CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
                    return Fail(sw, "Không tìm thấy file nguồn: " + sourceFile);
                if (string.IsNullOrWhiteSpace(destFile))
                    return Fail(sw, "Chưa nhập đường dẫn file đích.");
                if (File.Exists(destFile) && !overwrite)
                    return Fail(sw, "File đích đã tồn tại (bật ghi đè nếu muốn thay).");

                var dir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                const int bufSize = 1024 * 1024;
                var total = new FileInfo(sourceFile).Length;
                long done = 0;
                using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, bufSize, useAsync: true))
                using (var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufSize, useAsync: true))
                {
                    var buffer = new byte[bufSize];
                    int read;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        done += read;
                        if (total > 0)
                            progress?.Report(new MigrationProgress(
                                (int)(done * 100 / total), $"Đang sao lưu SQLite... {done * 100 / total}%"));
                    }
                }

                // Kiểm tra toàn vẹn bản copy (mở read-only, không khóa file gốc).
                using (var conn = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = destFile, Mode = SqliteOpenMode.ReadOnly }.ConnectionString))
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    using var cmd = new SqliteCommand("PRAGMA integrity_check;", conn)
                    {
                        CommandTimeout = 300
                    };
                    var verdict = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
                    if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                        return Fail(sw, "Bản copy không toàn vẹn (integrity_check: " + verdict + ").");
                }

                sw.Stop();
                _logger.LogInformation("Sao lưu SQLite xong trong {Elapsed}.", sw.Elapsed);
                return new BackupResult { Success = true, Elapsed = sw.Elapsed };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Sao lưu SQLite thất bại.");
                return new BackupResult { Success = false, Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }

        private BackupResult Fail(Stopwatch sw, string message)
        {
            sw.Stop();
            _logger.LogWarning("Sao lưu SQLite thất bại: {Message}", message);
            return new BackupResult { Success = false, Elapsed = sw.Elapsed, Error = message };
        }
    }
}
