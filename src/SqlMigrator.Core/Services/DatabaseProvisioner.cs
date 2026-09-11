using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Tạo database đích nếu chưa tồn tại — DÙNG CHUNG cho giao diện (tạo ngay khi nhập tên DB
    /// mới) và cho lộ trình di chuyển (MigrationOrchestrator) để tránh trùng lặp logic.
    /// Chuỗi kết nối điều chỉnh về master để lệnh CREATE DATABASE không đụng database chưa tồn
    /// tại; tự xử lý chứng chỉ tự ký / TLS cũ qua fallback kết nối an toàn.
    /// Không bao giờ log chuỗi kết nối, mật khẩu hay bí mật.
    /// </summary>
    public sealed class DatabaseProvisioner
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public DatabaseProvisioner(MigrationOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Tạo database đích (theo <see cref="MigrationOptions.DestinationConnectionString"/>)
        /// nếu chưa tồn tại. Dùng chính tài khoản trong chuỗi đích (User ID / Windows account),
        /// mở kết nối tới master để thực thi CREATE DATABASE; collation lấy từ database nguồn
        /// (fallback về SQL_Latin1_General_CP1_CI_AS). Trả về true nếu database đã sẵn sàng.
        /// </summary>
        public async Task<bool> CreateIfMissingAsync(CancellationToken ct = default)
        {
            var destBuilder = new SqlConnectionStringBuilder(_options.DestinationConnectionString);
            var dbName = destBuilder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(dbName))
                throw new InvalidOperationException(
                    "Chuỗi kết nối đích phải có tên database (Initial Catalog).");

            var masterBuilder = new SqlConnectionStringBuilder(_options.DestinationConnectionString)
            {
                InitialCatalog = "master"
            };

            // Log an toàn: chỉ tên server + tên DB + tài khoản (không chứa mật khẩu).
            var account = string.IsNullOrWhiteSpace(destBuilder.UserID)
                ? "Windows account hiện tại"
                : "user '" + destBuilder.UserID + "'";
            _logger.LogInformation(
                "Đang tạo database đích '{Db}' trên '{Server}' bằng {Account} (kết nối qua master)...",
                dbName, destBuilder.DataSource, account);

            using var conn = OpenWithFallback(masterBuilder.ConnectionString, ct);

            // Database đã tồn tại thì không tạo lại — trả về true ngay.
            var existsSql = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
            using (var existsCmd = new SqlCommand(existsSql, conn) { CommandTimeout = 30 })
            {
                existsCmd.Parameters.AddWithValue("@name", dbName);
                var count = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                if (count > 0)
                {
                    _logger.LogInformation("Database đích '{Db}' đã tồn tại.", dbName);
                    return true;
                }
            }

            var collation = "SQL_Latin1_General_CP1_CI_AS";
            try
            {
                using var src = OpenWithFallback(_options.SourceConnectionString, ct);
                using var cmd = new SqlCommand(
                    "SELECT DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS collation;", src)
                {
                    CommandTimeout = 30
                };
                var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    collation = s;
            }
            catch
            {
                // Không đọc được collation nguồn thì dùng collation mặc định phổ biến.
            }

            // COLLATE phải đứng SAU các mệnh đề ON / LOG ON (đúng cú pháp CREATE DATABASE);
            // để trước ON sẽ gây "Incorrect syntax near the keyword 'ON'".
            var createSql = BuildCreateDatabaseSql(dbName, collation,
                _options.DestinationDataFileDirectory, _options.DestinationLogFileDirectory);
            using (var cmd = new SqlCommand(createSql, conn) { CommandTimeout = _options.CommandTimeoutSeconds })
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Đã tạo xong database đích '{Db}' bằng {Account} (collation {Collation}).",
                dbName, account, collation);
            return true;
        }

        /// <summary>
        /// Tạo các nhóm file ROWS của nguồn còn thiếu trên đích (kèm 1 file .ndf
        /// mỗi nhóm). Idempotent, không ném lỗi (thiếu quyền → cảnh báo).
        /// Filegroup FILESTREAM/memory-optimized chỉ cảnh báo hướng dẫn tay.
        /// Chỉ ghi đích; nguồn chỉ SELECT catalog.
        /// </summary>
        public async Task EnsureFilegroupsAsync(CancellationToken ct = default)
        {
            var destBuilder = new SqlConnectionStringBuilder(_options.DestinationConnectionString);
            var dbName = destBuilder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(dbName))
                return;

            List<string> wanted;
            try
            {
                using var src = OpenWithFallback(_options.SourceConnectionString, ct);
                wanted = new List<string>();
                using (var cmd = new SqlCommand(
                    "SELECT name FROM sys.filegroups WHERE type = 'FG' AND name <> N'PRIMARY';", src)
                    { CommandTimeout = 60 })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        wanted.Add(reader.GetString(0));
                }

                // Nhóm đặc biệt (filestream/memory-optimized) cần container riêng → hướng dẫn tay.
                using (var cmd = new SqlCommand(
                    "SELECT name, type_desc FROM sys.filegroups WHERE type <> 'FG' AND name <> N'PRIMARY';", src)
                    { CommandTimeout = 60 })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "Filegroup đặc biệt '{Fg}' ({Type}) trên nguồn cần container riêng — "
                            + "hãy tạo tay trên đích trước khi di chuyển bảng nằm trên đó.",
                            reader.GetString(0), reader.GetString(1));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được filegroup nguồn ({Message}); bỏ qua bước filegroup.",
                    ex.Message);
                return;
            }

            if (wanted.Count == 0)
                return;

            // Thư mục file dữ liệu: ưu tiên người dùng chọn, rồi đường dẫn mặc định của server đích.
            var dataDir = _options.DestinationDataFileDirectory;
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                try
                {
                    var masterBuilder = new SqlConnectionStringBuilder(_options.DestinationConnectionString)
                    {
                        InitialCatalog = "master"
                    };
                    using var master = OpenWithFallback(masterBuilder.ConnectionString, ct);
                    using var cmd = new SqlCommand(
                        "SELECT CONVERT(NVARCHAR(4000), SERVERPROPERTY('InstanceDefaultDataPath'));", master)
                        { CommandTimeout = 30 };
                    dataDir = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không xác định được thư mục dữ liệu đích ({Message}); bỏ qua filegroup.",
                        ex.Message);
                    return;
                }
            }
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                _logger.LogWarning("Chưa có thư mục dữ liệu đích nên bỏ qua tạo filegroup.");
                return;
            }

            try
            {
                using var dest = OpenWithFallback(_options.DestinationConnectionString, ct);
                foreach (var fg in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var cmd = new SqlCommand(
                            FilegroupSqlBuilder.BuildEnsureFilegroupSql(dbName, fg, dataDir!),
                            dest)
                            { CommandTimeout = 300 };
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        _logger.LogInformation("Filegroup '{Fg}' đã sẵn sàng trên đích.", fg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không tạo được filegroup '{Fg}' trên đích ({Message}).", fg, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không kết nối được đích để tạo filegroup ({Message}).", ex.Message);
            }
        }

        /// <summary>
        /// Dựng câu CREATE DATABASE theo đúng cú pháp SQL Server: các mệnh đề file (ON / LOG ON)
        /// đứng TRƯỚC mệnh đề COLLATE. Để COLLATE trước ON sẽ gây "Incorrect syntax near the
        /// keyword 'ON'". Không chứa chuỗi kết nối hay mật khẩu — an toàn khi test.
        /// </summary>
        internal static string BuildCreateDatabaseSql(string dbName, string collation,
            string? dataDirectory, string? logDirectory)
        {
            return "CREATE DATABASE " + Quoting.QuoteIdentifier(dbName) +
                   BuildFileClause(dbName, dataDirectory, logDirectory) +
                   " COLLATE " + collation + ";";
        }

        /// <summary>
        /// Sinh mệnh đề ON (file dữ liệu) và LOG ON (file log) cho câu CREATE DATABASE dựa theo
        /// thư mục người dùng chọn. Cả hai đều không rỗng mới sinh (một ô trống sẽ dùng chung
        /// thư mục với ô kia). Đường dẫn được escape dấu nháy đơn để chống injection; không bao
        /// giờ liệt kê đường dẫn thật trong log.
        /// </summary>
        private static string BuildFileClause(string dbName, string? dataDirectory, string? logDirectory)
        {
            var dataDir = dataDirectory;
            var logDir = logDirectory;

            if (string.IsNullOrWhiteSpace(dataDir) && string.IsNullOrWhiteSpace(logDir))
                return string.Empty;

            // Nếu một thư mục trống thì dùng chung thư mục của ô còn lại.
            dataDir = string.IsNullOrWhiteSpace(dataDir) ? logDir : dataDir.Trim();
            logDir = string.IsNullOrWhiteSpace(logDir) ? dataDir : logDir.Trim();

            // Cắt null thông báo cho compiler: đây là hằng duyệt an toàn đã kiểm tra bên trên.
            var dataPath = dataDir!.Trim() + System.IO.Path.DirectorySeparatorChar;
            var logPath = logDir!.Trim() + System.IO.Path.DirectorySeparatorChar;

            var dataFile = System.IO.Path.Combine(dataPath, dbName + ".mdf");
            var logFile = System.IO.Path.Combine(logPath, dbName + "_log.ldf");

            // FILENAME phải là chuỗi literal (không dùng tham số được) → escape dấu nháy đơn.
            string Literal(string path) => "'" + path.Replace("'", "''") + "'";

            return " ON (NAME = " + Quoting.QuoteIdentifier(dbName + "_data") +
                   ", FILENAME = " + Literal(dataFile) + ")" +
                   " LOG ON (NAME = " + Quoting.QuoteIdentifier(dbName + "_log") +
                   ", FILENAME = " + Literal(logFile) + ")";
        }

        /// <summary>Mở kết nối có auto-fallback SSL (chứng chỉ tự ký / TLS cũ); log quyết định.</summary>
        private SqlConnection OpenWithFallback(string connectionString, CancellationToken ct)
        {
            return SqlConnectionFactory.OpenWithFallback(
                connectionString, 30, m => _logger.LogInformation("{Message}", m), ct);
        }
    }
}