using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Metadata kiểm soát một cột trong bảng nguồn (dùng cho đồng bộ tăng dần).
    /// </summary>
    internal sealed class CatalogColumn
    {
        public string Name { get; init; } = string.Empty;
        public bool IsIdentity { get; init; }
        public bool IsComputed { get; init; }
        public bool IsRowVersion { get; init; }
    }

    /// <summary>Mô tả nhẹ một bảng nguồn đọc từ sys.* (không cần SMO).</summary>
    internal sealed class CatalogTable
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<CatalogColumn> Columns { get; } = new();
        public List<string> PrimaryKeyColumns { get; } = new();

        public string PlainName => Schema + "." + Name;
        public string QualifiedName => Quoting.QuoteQualifiedName(Schema, Name);

        public CatalogColumn? IdentityColumn => Columns.FirstOrDefault(c => c.IsIdentity);
        public CatalogColumn? RowVersionColumn => Columns.FirstOrDefault(c => c.IsRowVersion);
    }

    /// <summary>
    /// Đọc/ghi/lưu mốc đồng bộ (baseline) dưới dạng file JSON trong LocalAppData.
    /// Baseline chỉ chứa metadata (count, max identity, max rowversion) chứ KHÔNG chứa
    /// chuỗi kết nối, mật khẩu hay bất kỳ secret nào; các khóa file là hash của server+db.
    /// </summary>
    public sealed class SyncBaselineStore
    {
        private readonly ILogger _logger;

        public SyncBaselineStore(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>Khóa (hash SHA-256) của cặp server nguồn–đích, dùng làm tên file baseline.</summary>
        public static string KeyOf(string sourceConnectionString, string destinationConnectionString)
        {
            var source = DescribeServer(sourceConnectionString);
            var dest = DescribeServer(destinationConnectionString);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(source + "|" + dest));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string DescribeServer(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                return builder.DataSource + "/" + builder.InitialCatalog;
            }
            catch
            {
                return "unknown";
            }
        }

        public static string BaseFolder
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator", "baselines");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        private static string FilePathOf(string sourceConnectionString, string destinationConnectionString) =>
            Path.Combine(BaseFolder, KeyOf(sourceConnectionString, destinationConnectionString) + ".json");

        private static string PathFromKey(string key) =>
            Path.Combine(BaseFolder, (string.IsNullOrWhiteSpace(key) ? "unknown" : key) + ".json");

        public async Task<SyncBaseline?> LoadAsync(string sourceConnectionString, string destinationConnectionString, CancellationToken ct = default)
        {
            var path = FilePathOf(sourceConnectionString, destinationConnectionString);
            if (!File.Exists(path))
                return null;

            try
            {
                await using (var stream = File.OpenRead(path))
                {
                    return await JsonSerializer.DeserializeAsync<SyncBaseline>(stream,
                        JsonOptions, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được baseline '{Path}': {Message}", path, ex.Message);
                return null;
            }
        }

        public Task SaveAsync(SyncBaseline baseline)
            => SaveAsync(baseline, PathFromKey(baseline.SourceKey));

        /// <summary>Lưu baseline vào file tương ứng với hai chuỗi kết nối của một MigrationOptions.</summary>
        public Task SaveAsync(SyncBaseline baseline, string sourceConnectionString, string destinationConnectionString,
            CancellationToken ct = default)
        {
            var path = FilePathOf(sourceConnectionString, destinationConnectionString);
            return SaveAsync(baseline, path, ct);
        }

        private async Task SaveAsync(SyncBaseline baseline, string path, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(baseline, JsonOptions);
            await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);
            _logger.LogInformation("Đã lưu mốc đồng bộ ({TableCount} bảng) vào '{Path}'.", baseline.Tables.Count, path);
        }

        /// <summary>
        /// Ghi lại mốc đồng bộ hiện tại phía NGUỒN cho mọi bảng người dùng (đọc từ sys.*).
        /// Gọi sau khi một đợt di chuyển/đồng bộ hoàn tất thành công.
        /// </summary>
        public async Task<SyncBaseline> CaptureAsync(MigrationOptions options, CancellationToken ct = default)
        {
            var sourceKey = KeyOf(options.SourceConnectionString, options.DestinationConnectionString);
            var destKey = KeyOf(options.DestinationConnectionString, options.SourceConnectionString);
            var builder = new SyncBaseline
            {
                SourceKey = sourceKey,
                DestinationKey = destKey,
                SavedAtUtc = DateTime.UtcNow
            };
            var tables = new Dictionary<string, TableBaseline>(StringComparer.OrdinalIgnoreCase);

            var catalog = await QueryCatalogAsync(options.SourceConnectionString, ct).ConfigureAwait(false);
            foreach (var table in catalog)
            {
                var meter = await ReadMeterAsync(options.SourceConnectionString, table, ct).ConfigureAwait(false);
                tables[table.PlainName] = new TableBaseline
                {
                    Schema = table.Schema,
                    Name = table.Name,
                    RowCount = meter.Count,
                    MaxIdentity = meter.MaxIdentity,
                    MaxRowVersion = meter.MaxRowVersion,
                    IdentityColumn = table.IdentityColumn?.Name,
                    RowVersionColumn = table.RowVersionColumn?.Name,
                    PrimaryKeyColumns = table.PrimaryKeyColumns
                };
            }

            builder.Tables = tables;
            return builder;
        }

        /// <summary>Đọc cấu trúc nhẹ (cột, identity, rowversion, khóa chính) của mọi bảng người dùng.</summary>
        internal static async Task<IReadOnlyList<CatalogTable>> QueryCatalogAsync(string connectionString, CancellationToken ct)
        {
            var tables = new List<CatalogTable>();
            var index = new Dictionary<string, CatalogTable>(StringComparer.OrdinalIgnoreCase);

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            const string columnsQuery = @"
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name,
       c.is_identity, c.is_computed, c.system_type_id, c.column_id
  FROM sys.tables t
  JOIN sys.schemas s ON s.schema_id = t.schema_id
  JOIN sys.columns c ON c.object_id = t.object_id
 WHERE t.is_ms_shipped = 0
 ORDER BY s.name, t.name, c.column_id;";

            using (var cmd = new SqlCommand(columnsQuery, conn) { CommandTimeout = 120 })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    var key = schema + "." + name;
                    if (!index.TryGetValue(key, out var table))
                    {
                        table = new CatalogTable { Schema = schema, Name = name };
                        tables.Add(table);
                        index[key] = table;
                    }

                    // system_type_id 189 = timestamp/rowversion
                    table.Columns.Add(new CatalogColumn
                    {
                        Name = reader.GetString(2),
                        IsIdentity = reader.GetBoolean(3),
                        IsComputed = reader.GetBoolean(4),
                        IsRowVersion = reader.GetByte(5) == 189
                    });
                }
            }

            const string pkQuery = @"
SELECT s.name AS schema_name, t.name AS table_name, c.name AS pk_column
  FROM sys.tables t
  JOIN sys.schemas s ON s.schema_id = t.schema_id
  JOIN sys.key_constraints kc ON kc.parent_object_id = t.object_id AND kc.type = 'PK'
  JOIN sys.index_columns ic
       ON ic.object_id = t.object_id AND ic.index_id = kc.unique_index_id
  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
 WHERE t.is_ms_shipped = 0
 ORDER BY s.name, t.name, ic.key_ordinal;";

            using (var cmd = new SqlCommand(pkQuery, conn) { CommandTimeout = 120 })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var key = reader.GetString(0) + "." + reader.GetString(1);
                    if (index.TryGetValue(key, out var table))
                        table.PrimaryKeyColumns.Add(reader.GetString(2));
                }
            }

            return tables;
        }

        /// <summary>Đọc số dòng + MAX(identity) + MAX(rowversion) của một bảng từ catalog.</summary>
        internal static async Task<(long Count, long? MaxIdentity, long? MaxRowVersion)> ReadMeterAsync(
            string connectionString, CatalogTable table, CancellationToken ct)
        {
            var id = table.IdentityColumn;
            var rv = table.RowVersionColumn;

            var select = "SELECT COUNT_BIG(1)";
            if (id != null)
                select += ", MAX(CONVERT(bigint, " + Quoting.QuoteIdentifier(id.Name) + "))";
            else
                select += ", NULL";
            if (rv != null)
                select += ", MAX(CONVERT(bigint, " + Quoting.QuoteIdentifier(rv.Name) + "))";
            else
                select += ", NULL";

            var sql = select + " FROM " + table.QualifiedName + ";";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return (0, null, null);

            long? maxId = null;
            long? maxRv = null;
            if (!reader.IsDBNull(1)) maxId = reader.GetInt64(1);
            if (!reader.IsDBNull(2)) maxRv = reader.GetInt64(2);
            return (reader.GetInt64(0), maxId, maxRv);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
    }
}