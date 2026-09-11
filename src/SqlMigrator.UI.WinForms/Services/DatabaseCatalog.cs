using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services;

namespace SqlMigrator.UI.Services
{
    /// <summary>Mô tả một bảng trong database để hiển thị trên trang chọn bảng.</summary>
    public sealed class DatabaseTableInfo
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string PlainName => Schema + "." + Name;
        public override string ToString() => PlainName;
    }

    /// <summary>
    /// Duyệt cấu trúc server nguồn (danh sách database, danh sách bảng) phục vụ các trang
    /// Wizard. Chỉ dùng chuỗi kết nối do <see cref="SecureConnectionStringBuilder"/> tạo và
    /// mở qua auto-fallback (cert tự ký / SQL 2008 thiếu TLS) để kết nối được SQL 2008 → 2025.
    /// </summary>
    public static class DatabaseCatalog
    {
        /// <summary>Danh sách database (bỏ các database hệ thống).</summary>
        public static async Task<List<string>> GetDatabasesAsync(
            ConnectionProfile profile,
            SecureConnectionStringBuilder builder,
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            const string query = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name;";
            var result = new List<string>();

            using var conn = SqlConnectionFactory.OpenWithFallback(builder.Build(profile), 30, log, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 60 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(reader.GetString(0));

            return result;
        }

        /// <summary>Danh sách bảng (không gồm bảng hệ thống) của database nguồn/đích.</summary>
        public static async Task<List<DatabaseTableInfo>> GetTablesAsync(
            ConnectionProfile profile,
            SecureConnectionStringBuilder builder,
            string databaseName,
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            const string query = @"
SELECT OBJECT_SCHEMA_NAME(t.object_id), t.name
  FROM sys.tables t
 WHERE t.is_ms_shipped = 0
 ORDER BY OBJECT_SCHEMA_NAME(t.object_id), t.name;";

            var databaseProfile = CloneWithDatabase(profile, databaseName);
            var result = new List<DatabaseTableInfo>();

            using var conn = SqlConnectionFactory.OpenWithFallback(builder.Build(databaseProfile), 30, log, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 60 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new DatabaseTableInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1)
                });
            }

            return result;
        }

        /// <summary>Kiểm tra kết nối tới server (mở rồi đóng ngay).</summary>
        public static async Task TestConnectionAsync(
            ConnectionProfile profile,
            SecureConnectionStringBuilder builder,
            CancellationToken ct = default,
            Action<string>? log = null)
        {
            using var conn = SqlConnectionFactory.OpenWithFallback(builder.Build(profile), 30, log, ct);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static ConnectionProfile CloneWithDatabase(ConnectionProfile source, string databaseName)
        {
            return new ConnectionProfile
            {
                Id = source.Id,
                Name = source.Name,
                Server = source.Server,
                Database = databaseName,
                Authentication = source.Authentication,
                UserName = source.UserName,
                ProtectedPassword = source.ProtectedPassword,
                EncryptConnection = source.EncryptConnection,
                TrustServerCertificate = source.TrustServerCertificate
            };
        }
    }
}