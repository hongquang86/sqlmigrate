using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Điều phối "không gian an toàn" quanh máy nguồn–đích khi đổ dữ liệu lớn:
    ///  - Nguồn: đảm bảo dữ liệu đọc không chặn ghi (RCSI, hoặc SNAPSHOT isolation);
    ///  - Đích: tạm chuyển recovery += BULK_LOGGED để tránh log phình, rồi khôi phục;
    ///  - Cảnh báo sớm khi log thấp về dung lượng còn trống.
    /// Phương châm: mọi thiếu quyền chỉ dẫn tới cảnh báo chứ KHÔNG dừng tiến trình.
    /// </summary>
    public sealed class SqlEnvironmentManager
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;
        private string _originalRecoveryModel = string.Empty;

        public SqlEnvironmentManager(MigrationOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Kiểm tra RCSI trên nguồn; nếu chưa bật và user cho phép
        /// (<see cref="MigrationOptions.EnableReadCommittedSnapshotOnSource"/>) thì thử bật
        /// bằng ALTER DATABASE (thao tác GHI lên nguồn — mặc định tắt).
        /// Trả true khi RCSI đang bật (đọc READ COMMITTED sẽ dùng row versioning,
        /// không chặn ghi); false trong mọi trường hợp còn lại mà không ném lỗi.
        /// </summary>
        public async Task<bool> EnsureReadCommittedSnapshotOnSourceAsync(CancellationToken ct)
        {
            var builder = new SqlConnectionStringBuilder(_options.SourceConnectionString);
            var database = builder.InitialCatalog;

            var rcsi = await IsRcsiEnabledAsync(_options.SourceConnectionString, database, ct).ConfigureAwait(false);
            if (rcsi)
            {
                _logger.LogInformation("Nguồn '{Database}' đang bật READ_COMMITTED_SNAPSHOT → không chặn ghi khi đọc.", database);
                return true;
            }

            var so = await IsSnapshotIsolationEnabledAsync(_options.SourceConnectionString, database, ct).ConfigureAwait(false);
            if (!_options.EnableReadCommittedSnapshotOnSource)
            {
                if (so)
                    _logger.LogInformation("Nguồn '{Database}' bật SNAPSHOT isolation → dùng đọc snapshot.", database);
                else
                    _logger.LogWarning("Nguồn '{Database}' không bật RCSI/SNAPSHOT; sẽ đọc theo READ COMMITTED, có thể chặn ghi của ứng dụng khác.", database);
                return false;
            }

            try
            {
                await using var conn = new SqlConnection(_options.SourceConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(
                    "ALTER DATABASE " + Quoting.QuoteIdentifier(database) + " SET READ_COMMITTED_SNAPSHOT ON;", conn)
                { CommandTimeout = 180 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Đã bật READ_COMMITTED_SNAPSHOT trên nguồn '{Database}'.", database);
                return true;
            }
            catch (Exception ex)
            {
                var so2 = await IsSnapshotIsolationEnabledAsync(_options.SourceConnectionString, database, ct).ConfigureAwait(false);
                if (so2)
                {
                    _logger.LogWarning(
                        "Chưa bật được READ_COMMITTED_SNAPSHOT trên nguồn (thiếu quyền? {Message}); dùng SNAPSHOT isolation thay thế.",
                        ex.Message);
                    return false;
                }

                _logger.LogWarning(
                    "Chưa bật được READ_COMMITTED_SNAPSHOT trên nguồn ({Message}); hãy bật thủ công hoặc chấp nhận đọc READ COMMITTED.",
                    ex.Message);
                return false;
            }
        }

        private static async Task<bool> IsRcsiEnabledAsync(string connectionString, string database, CancellationToken ct)
        {
            try
            {
                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(
                    "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @db;", c)
                { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@db", database);
                var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return value is 1 || (value is int i && i == 1);
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> IsSnapshotIsolationEnabledAsync(string connectionString, string database, CancellationToken ct)
        {
            try
            {
                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(
                    "SELECT snapshot_isolation_state FROM sys.databases WHERE name = @db;", c)
                { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@db", database);
                var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return value is 1 || value is 2 || (value is int i && (i == 1 || i == 2));
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string?> ReadRecoveryModelAsync(string connectionString, CancellationToken ct)
        {
            try
            {
                await using var c = new SqlConnection(connectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(
                    "SELECT recovery_model_desc FROM sys.databases WHERE database_id = DB_ID();", c)
                { CommandTimeout = 30 };
                return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Trước khi đổ dữ liệu: chuyển ĐÍCH sang BULK_LOGGED nếu đang FULL và được phép
        /// (giữ minimal logging khi dòng dữ liệu lớn). Không làm gì nếu SIMPLE.
        /// </summary>
        public async Task PrepareDestinationRecoveryAsync(CancellationToken ct)
        {
            if (!_options.SwitchDestinationRecoveryDuringLoad)
                return;

            var current = await ReadRecoveryModelAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);
            _originalRecoveryModel = current ?? string.Empty;
            if (string.IsNullOrWhiteSpace(current) || current.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await using var c = new SqlConnection(_options.DestinationConnectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                var database = new SqlConnectionStringBuilder(_options.DestinationConnectionString).InitialCatalog;
                await using var cmd = new SqlCommand(
                    "ALTER DATABASE " + Quoting.QuoteIdentifier(database) + " SET RECOVERY BULK_LOGGED;", c)
                { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Đã tạm chuyển database đích sang BULK_LOGGED để đổ dữ liệu nhanh.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không chuyển được recovery model của đích sang BULK_LOGGED ({Message}); log FULL có thể no-space nếu dữ liệu rất lớn.", ex.Message);
            }
        }

        /// <summary>Khôi phục lại recovery model gốc của ĐÍCH sau khi đổ xong (dù thành công hay lỗi).</summary>
        public async Task RestoreDestinationRecoveryAsync(CancellationToken ct)
        {
            if (!_options.SwitchDestinationRecoveryDuringLoad || string.IsNullOrWhiteSpace(_originalRecoveryModel))
                return;

            try
            {
                await using var c = new SqlConnection(_options.DestinationConnectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                var database = new SqlConnectionStringBuilder(_options.DestinationConnectionString).InitialCatalog;
                await using var cmd = new SqlCommand(
                    "ALTER DATABASE " + Quoting.QuoteIdentifier(database) + " SET RECOVERY " + _originalRecoveryModel + ";", c)
                { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Đã khôi phục recovery model '{Model}' cho database đích.", _originalRecoveryModel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không khôi phục được recovery model '{Model}' của đích ({Message}); vui lòng kiểm tra thủ công.", _originalRecoveryModel, ex.Message);
            }

            _originalRecoveryModel = string.Empty;
        }

        /// <summary>Kiểm tra log của đích còn đủ trống; trả % log đã dùng (0 nếu không đọc được).</summary>
        public async Task<double> CheckDestinationLogUsageAsync(CancellationToken ct)
        {
            try
            {
                await using var c = new SqlConnection(_options.DestinationConnectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = new SqlCommand(
                    // MAX trên các log file (DB nhiều file log thì lấy file đầy nhất).
                    "SELECT MAX(used_log_space_in_percent) FROM sys.dm_db_log_space_usage;", c)
                { CommandTimeout = 30 };
                var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var percent = Convert.ToDouble(value);
                if (percent > 90)
                    _logger.LogWarning("Log của database đích đã dùng {Percent:0.#}%; có nguy cơ no-space log khi đổ dữ liệu lớn.", percent);
                return percent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được dung lượng log đích: {Message}", ex.Message);
                return 0;
            }
        }
    }
}