using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Disables/reattaches CHECK constraints, foreign keys, triggers and non-clustered
    /// indexes on the destination. SQL generation is exposed as pure functions so the
    /// constraint behavior can be unit tested without a live server.
    /// </summary>
    public sealed class ConstraintManager : IConstraintManager
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public ConstraintManager(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        // ---- Pure SQL generation (unit-testable) -----------------------------------

        /// <summary>Disables every textual CHECK/FK constraint on a table.</summary>
        public static string BuildDisableConstraintScript(TableSchema table) =>
            $"ALTER TABLE {table.QualifiedName} NOCHECK CONSTRAINT ALL{Terminator()}";

        /// <summary>Re-enables textual CHECK/FK constraints, optionally validating loaded rows.</summary>
        public static string BuildEnableConstraintScript(TableSchema table, bool checkExistingData)
        {
            var withCheck = checkExistingData ? "WITH CHECK" : "WITH NOCHECK";
            return $"ALTER TABLE {table.QualifiedName} {withCheck} CHECK CONSTRAINT ALL{Terminator()}";
        }

        public static string BuildDisableTriggerScript(TableSchema table) =>
            $"DISABLE TRIGGER ALL ON {table.QualifiedName}{Terminator()}";

        public static string BuildEnableTriggerScript(TableSchema table) =>
            $"ENABLE TRIGGER ALL ON {table.QualifiedName}{Terminator()}";

        public static string BuildDisableIndexScript(string indexName, TableSchema table) =>
            $"ALTER INDEX {Quoting.QuoteIdentifier(indexName)} ON {table.QualifiedName} DISABLE{Terminator()}";

        public static string BuildRebuildIndexScript(string indexName, TableSchema table) =>
            $"ALTER INDEX {Quoting.QuoteIdentifier(indexName)} ON {table.QualifiedName} REBUILD{Terminator()}";

        private static string Terminator() => ";";

        // ---- Instance entry points --------------------------------------------------

        public async Task DisableConstraintsAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default)
        {
            var materialized = tables.Where(t => !t.IsSkipped).ToList();
            if (materialized.Count == 0)
                return;

            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var table in materialized)
            {
                var sql = BuildDisableConstraintScript(table);
                await ExecuteAsync(conn, sql, $"disable constraints on {table.PlainName}", ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Disabled CHECK/FK constraints on {Count} destination tables.", materialized.Count);
        }

        public async Task EnableConstraintsAsync(IEnumerable<TableSchema> tables, bool checkExistingData, CancellationToken ct = default)
        {
            var materialized = tables.Where(t => !t.IsSkipped).ToList();
            if (materialized.Count == 0)
                return;

            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var failures = new List<string>();
            foreach (var table in materialized)
            {
                var sql = BuildEnableConstraintScript(table, checkExistingData);
                using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
                try
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // WITH CHECK thất bại (dữ liệu không thỏa ràng buộc): phải ĐẢM BẢO ràng buộc
                    // vẫn được bật lại (WITH NOCHECK) để không để đích trong trạng thái NOCHECK
                    // tồn đọng. Dữ liệu không hợp lệ sẽ được báo rõ trong cảnh báo.
                    if (checkExistingData)
                    {
                        try
                        {
                            var noCheckSql = BuildEnableConstraintScript(table, checkExistingData: false);
                            using var noCheckCmd = new SqlCommand(noCheckSql, conn)
                            {
                                CommandTimeout = _options.CommandTimeoutSeconds
                            };
                            await noCheckCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            var message = $"{table.PlainName}: {ex.Message} — đã bật lại ràng buộc dạng WITH NOCHECK (không kiểm tra dữ liệu cũ).";
                            failures.Add(message);
                            _logger.LogWarning("Failed WITH CHECK on {T}; re-enabled WITHOUT check. {Message}",
                                table.PlainName, ex.Message);
                            continue;
                        }
                        catch (Exception noCheckEx)
                        {
                            var message = $"{table.PlainName}: {ex.Message} (kể cả no-check: {noCheckEx.Message})";
                            failures.Add(message);
                            _logger.LogError(ex, "Failed to enable constraints on {T}: {Message}", table.PlainName, ex.Message);
                            continue;
                        }
                    }

                    var plain = $"{table.PlainName}: {ex.Message}";
                    failures.Add(plain);
                    _logger.LogError(ex, "Failed to enable constraints on {T}: {Message}", table.PlainName, ex.Message);
                }
            }

            if (failures.Count > 0)
            {
                var detail = string.Join("; ", failures);
                throw new InvalidOperationException(
                    $"Constraint verification failed for {failures.Count} table(s): {detail}");
            }
        }

        public async Task DisableTriggersAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default)
        {
            var materialized = tables.Where(t => !t.IsSkipped).ToList();
            if (materialized.Count == 0)
                return;

            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var table in materialized)
            {
                await ExecuteAsync(conn, BuildDisableTriggerScript(table),
                    $"disable triggers on {table.PlainName}", ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Disabled triggers on {Count} destination tables.", materialized.Count);
        }

        public async Task EnableTriggersAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default)
        {
            var materialized = tables.Where(t => !t.IsSkipped).ToList();
            if (materialized.Count == 0)
                return;

            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var table in materialized)
            {
                await ExecuteAsync(conn, BuildEnableTriggerScript(table),
                    $"enable triggers on {table.PlainName}", ct).ConfigureAwait(false);
            }
        }

        public async Task DisableSecondaryIndexesAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default)
        {
            foreach (var table in tables.Where(t => !t.IsSkipped))
            {
                var indexes = await ReadDisposableIndexesAsync(table, ct).ConfigureAwait(false);
                if (indexes.Count == 0)
                    continue;

                using var conn = new SqlConnection(_options.DestinationConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                foreach (var index in indexes)
                {
                    await ExecuteAsync(conn, BuildDisableIndexScript(index, table),
                        $"disable index {index} on {table.PlainName}", ct).ConfigureAwait(false);
                }
            }
        }

        public async Task RebuildSecondaryIndexesAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default)
        {
            foreach (var table in tables.Where(t => !t.IsSkipped))
            {
                var indexes = await ReadDisposableIndexesAsync(table, ct).ConfigureAwait(false);
                if (indexes.Count == 0)
                    continue;

                using var conn = new SqlConnection(_options.DestinationConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                foreach (var index in indexes)
                {
                    await ExecuteAsync(conn, BuildRebuildIndexScript(index, table),
                        $"rebuild index {index} on {table.PlainName}", ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Returns non-clustered, non-unique indexes that are safe to disable/rebuild
        /// (unique indexes back UNIQUE/PK constraints and cannot simply be disabled).
        /// </summary>
        private async Task<List<string>> ReadDisposableIndexesAsync(TableSchema table, CancellationToken ct)
        {
            const string query = @"
SELECT i.name
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID(@table)
   AND i.type = 2
   AND i.is_unique = 0
   AND i.is_primary_key = 0
   AND i.is_unique_constraint = 0
   AND i.name IS NOT NULL;";

            var result = new List<string>();
            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@table", table.PlainName);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }

        private async Task ExecuteAsync(SqlConnection conn, string sql, string what, CancellationToken ct)
        {
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            try
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_options.FailFast || !_options.ContinueOnNonCriticalErrors)
                    throw;
                _logger.LogWarning(ex, "Could not {What}: {Message}", what, ex.Message);
            }
        }
    }
}