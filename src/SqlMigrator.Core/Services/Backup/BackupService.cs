using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Backup
{
    /// <summary>
    /// Dựng câu lệnh BACKUP/RESTORE T-SQL. Thuần chuỗi để kiểm thử không cần server.
    /// Mọi tên object/file đều quote/escape, không nối chuỗi thô từ người dùng.
    /// </summary>
    public static class BackupSqlBuilder
    {
        /// <summary>BACKUP DATABASE với STATS=5 để hứng % tiến trình.</summary>
        public static string BuildBackupSql(BackupRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Database))
                throw new ArgumentException("Thiếu tên database.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.BackupFile))
                throw new ArgumentException("Thiếu đường dẫn file backup (phía server).", nameof(request));
            return "BACKUP DATABASE " + Quoting.QuoteIdentifier(request.Database.Trim())
                + " TO DISK = " + Literal(request.BackupFile.Trim())
                + (request.FullBackup ? "" : " WITH DIFFERENTIAL")
                + (request.Compression ? ", COMPRESSION" : ", NO_COMPRESSION")
                + ", STATS = 5;";
        }

        /// <summary>
        /// RESTORE DATABASE với MOVE (khi đổi thư mục), REPLACE và RECOVERY/NORECOVERY.
        /// files: danh sách từ RESTORE FILELISTONLY (rỗng = không MOVE).
        /// </summary>
        public static string BuildRestoreSql(
            RestoreRequest request, IReadOnlyList<BackupFileEntry> files)
        {
            if (string.IsNullOrWhiteSpace(request.Database))
                throw new ArgumentException("Thiếu tên database đích.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.BackupFile))
                throw new ArgumentException("Thiếu đường dẫn file backup (phía server).", nameof(request));

            var sql = "RESTORE DATABASE " + Quoting.QuoteIdentifier(request.Database.Trim())
                + " FROM DISK = " + Literal(request.BackupFile.Trim()) + " WITH ";
            var clauses = new List<string>();
            foreach (var file in files)
            {
                var dir = file.Type == "L" ? request.LogDirectory : request.DataDirectory;
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(file.PhysicalName);
                var ext = System.IO.Path.GetExtension(file.PhysicalName);
                if (string.IsNullOrEmpty(ext))
                    ext = file.Type == "L" ? ".ldf" : ".mdf";
                var target = System.IO.Path.Combine(dir.Trim(), name + "_" + request.Database.Trim() + ext);
                clauses.Add("MOVE " + Literal(file.LogicalName) + " TO " + Literal(target));
            }
            if (request.ReplaceExisting)
                clauses.Add("REPLACE");
            clauses.Add(request.WithRecovery ? "RECOVERY" : "NORECOVERY");
            clauses.Add("STATS = 5");
            return sql + string.Join(", ", clauses) + ";";
        }

        /// <summary>Trích % từ message "10 percent processed." của SQL Server.</summary>
        internal static int? ParsePercent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return null;
            var m = Regex.Match(message, @"(\d+)\s*percent processed", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var percent))
                return null;
            return Math.Clamp(percent, 0, 100);
        }

        private static string Literal(string path) => "N'" + path.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Chạy sao lưu/khôi phục SQL Server, hứng % tiến trình từ InfoMessage.
    /// Chỉ ghi phía server đích/quản trị theo yêu cầu; nguồn chỉ đọc catalog.
    /// </summary>
    public sealed class BackupService
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public BackupService(MigrationOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Sao lưu database đích ra file phía server, báo % qua progress.</summary>
        public async Task<BackupResult> BackupDatabaseAsync(
            BackupRequest request, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var sql = BackupSqlBuilder.BuildBackupSql(request);
                using var conn = SqlConnectionFactory.Open(
                    _options.DestinationConnectionString, _options.CommandTimeoutSeconds, ct);
                var lastPercent = -1;
                void OnInfo(object? sender, SqlInfoMessageEventArgs e)
                {
                    foreach (SqlError info in e.Errors)
                    {
                        var percent = BackupSqlBuilder.ParsePercent(info.Message);
                        if (percent != null && percent != lastPercent)
                        {
                            lastPercent = percent.Value;
                            progress?.Report(new MigrationProgress(percent.Value,
                                $"Đang sao lưu {request.Database}... {percent.Value}%"));
                        }
                        _logger.LogInformation("BACKUP: {Message}", info.Message);
                    }
                }
                conn.InfoMessage += OnInfo;
                try
                {
                    using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    conn.InfoMessage -= OnInfo;
                }
                sw.Stop();
                _logger.LogInformation("Sao lưu {Db} xong trong {Elapsed}.", request.Database, sw.Elapsed);
                return new BackupResult { Success = true, Elapsed = sw.Elapsed };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Sao lưu {Db} thất bại.", request.Database);
                return new BackupResult { Success = false, Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }

        /// <summary>Đọc danh sách file logic trong backup (để MOVE khi restore).</summary>
        public async Task<IReadOnlyList<BackupFileEntry>> GetBackupFileListAsync(
            string backupFile, CancellationToken ct = default)
        {
            var entries = new List<BackupFileEntry>();
            using var conn = SqlConnectionFactory.Open(
                _options.DestinationConnectionString, _options.CommandTimeoutSeconds, ct);
            using var cmd = new SqlCommand(
                "RESTORE FILELISTONLY FROM DISK = @file;", conn) { CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@file", backupFile);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(new BackupFileEntry
                {
                    LogicalName = reader.GetString(0),
                    PhysicalName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Type = reader.GetString(2)
                });
            }
            return entries;
        }

        /// <summary>
        /// Khôi phục database từ file backup phía server. Khi ReplaceExisting, ngắt
        /// kết nối đang dùng DB đích trước (SINGLE_USER), xong trả về MULTI_USER.
        /// </summary>
        public async Task<BackupResult> RestoreDatabaseAsync(
            RestoreRequest request, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var db = request.Database.Trim();
            try
            {
                var files = await GetBackupFileListAsync(request.BackupFile, ct).ConfigureAwait(false);
                var sql = BackupSqlBuilder.BuildRestoreSql(request, files);
                using var conn = SqlConnectionFactory.Open(
                    _options.DestinationConnectionString, _options.CommandTimeoutSeconds, ct);

                if (request.ReplaceExisting)
                {
                    using var single = new SqlCommand(
                        "ALTER DATABASE " + Quoting.QuoteIdentifier(db)
                        + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", conn)
                        { CommandTimeout = 300 };
                    try { await single.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        // DB chưa tồn tại thì không cần ngắt kết nối — đi tiếp.
                        _logger.LogDebug("Bỏ qua SINGLE_USER ({Message}).", ex.Message);
                    }
                }

                var lastPercent = -1;
                void OnInfo(object? sender, SqlInfoMessageEventArgs e)
                {
                    foreach (SqlError info in e.Errors)
                    {
                        var percent = BackupSqlBuilder.ParsePercent(info.Message);
                        if (percent != null && percent != lastPercent)
                        {
                            lastPercent = percent.Value;
                            progress?.Report(new MigrationProgress(percent.Value,
                                $"Đang khôi phục {db}... {percent.Value}%"));
                        }
                        _logger.LogInformation("RESTORE: {Message}", info.Message);
                    }
                }
                conn.InfoMessage += OnInfo;
                try
                {
                    using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    conn.InfoMessage -= OnInfo;
                    if (request.ReplaceExisting)
                    {
                        try
                        {
                            using var multi = new SqlCommand(
                                "ALTER DATABASE " + Quoting.QuoteIdentifier(db) + " SET MULTI_USER;", conn)
                                { CommandTimeout = 300 };
                            await multi.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Không trả được MULTI_USER cho {Db} ({Message}).", db, ex.Message);
                        }
                    }
                }
                sw.Stop();
                _logger.LogInformation("Khôi phục {Db} xong trong {Elapsed}.", db, sw.Elapsed);
                return new BackupResult { Success = true, Elapsed = sw.Elapsed };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Khôi phục {Db} thất bại.", db);
                return new BackupResult { Success = false, Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }
    }
}
