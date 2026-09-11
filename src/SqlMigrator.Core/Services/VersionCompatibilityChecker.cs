using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Compares source and destination server versions and reports destination features
    /// that would prevent certain objects from being created or used correctly.
    /// The checker never throws for a mismatch; it produces a <see cref="CompatibilityReport"/>
    /// that the orchestrator and CLI surface to the user, flagging incompatible tables for skip.
    /// </summary>
    public sealed class VersionCompatibilityChecker : ICompatibilityChecker
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public VersionCompatibilityChecker(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        public async Task<CompatibilityReport> CheckAsync(SchemaModel schema, CancellationToken ct = default)
        {
            var issues = new List<CompatibilityIssue>();
            var tablesToSkip = new List<string>();

            var sourceMajor = await ReadProductMajorAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            var destMajor = await ReadProductMajorAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);

            if (destMajor < 0 || sourceMajor < 0)
            {
                issues.Add(new CompatibilityIssue
                {
                    ObjectName = "server",
                    Message = "Could not determine product versions; compatibility analysis was skipped."
                });
                return new CompatibilityReport { Issues = issues, TablesToSkip = tablesToSkip };
            }

            if (destMajor < sourceMajor)
            {
                issues.Add(new CompatibilityIssue
                {
                    ObjectName = "server",
                    Feature = "downgrade",
                    Severity = "Error",
                    Message =
                        $"Migrating DOWNGRADE: source is SQL {DescribeVersion(sourceMajor)}, " +
                        $"destination is SQL {DescribeVersion(destMajor)}. Features unavailable on the " +
                        $"destination will be skipped with warnings."
                });
            }

            foreach (var table in schema.Tables)
            {
                if (table.IsMemoryOptimized && destMajor < 12)
                {
                    AddFeature(table.Schema + "." + table.Name, "memory-optimized tables",
                        "Memory-optimized tables require SQL Server 2014 (12.0) or later.",
                        tablesToSkip, issues);
                }

                if (table.IsTemporal && destMajor < 13)
                {
                    AddFeature(table.Schema + "." + table.Name, "system-versioned (temporal) tables",
                        "Temporal tables require SQL Server 2016 (13.0) or later.",
                        tablesToSkip, issues);
                }

                if (table.IsFileTable && destMajor < 11)
                {
                    AddFeature(table.Schema + "." + table.Name, "FileTables",
                        "FileTables require SQL Server 2012 (11.0) or later.",
                        tablesToSkip, issues);
                }
            }

            if (destMajor < 12)
            {
                var columnstoreTables = await DetectColumnstoreTablesAsync(ct).ConfigureAwait(false);
                foreach (var table in columnstoreTables)
                {
                    if (!tablesToSkip.Contains(table, StringComparer.OrdinalIgnoreCase))
                        AddFeature(table, "columnstore indexes", "Columnstore indexes are not supported on the destination.",
                            tablesToSkip, issues);
                }
            }

            return new CompatibilityReport { Issues = issues, TablesToSkip = tablesToSkip };
        }

        /// <summary>
        /// Ghi một cảnh báo tương thích. Khi <see cref="MigrationOptions.ForceUnsupportedFeatures"/>
        /// bật thì KHÔNG đánh dấu bảng cần bỏ qua — vẫn cố tạo và chỉ bỏ qua nếu đích từ chối.
        /// </summary>
        private void AddFeature(string objectName, string feature, string baseMessage,
            List<string> tablesToSkip, List<CompatibilityIssue> issues)
        {
            if (_options.ForceUnsupportedFeatures)
            {
                issues.Add(new CompatibilityIssue
                {
                    ObjectName = objectName,
                    Feature = feature,
                    Message = baseMessage +
                        " Đang cố tạo trên đích; nếu đích từ chối, bảng sẽ được bỏ qua kèm cảnh báo."
                });
                return;
            }

            issues.Add(new CompatibilityIssue
            {
                ObjectName = objectName,
                Feature = feature,
                Message = baseMessage + " Table skipped."
            });
            tablesToSkip.Add(objectName);
        }

        private async Task<List<string>> DetectColumnstoreTablesAsync(CancellationToken ct)
        {
            var result = new List<string>();
            const string query = @"
SELECT DISTINCT OBJECT_SCHEMA_NAME(i.object_id) + '.' + OBJECT_NAME(i.object_id)
  FROM sys.indexes i
 WHERE i.type IN (5, 6) -- nonclustered / clustered columnstore
   AND i.object_id > 0;";

            try
            {
                using var conn = new SqlConnection(_options.SourceConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    result.Add(reader.GetString(0));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Columnstore detection failed; continuing without it. {Message}", ex.Message);
            }

            return result;
        }

        private static async Task<int> ReadProductMajorAsync(string connectionString, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", conn)
                {
                    CommandTimeout = 30
                };
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            catch
            {
                return -1;
            }
        }

        private static string DescribeVersion(int major) => major switch
        {
            10 => "2008",
            11 => "2012",
            12 => "2014",
            13 => "2016",
            14 => "2017",
            15 => "2019",
            16 => "2022",
            17 => "2025",
            _ => $"Server {major}.0"
        };
    }
}