using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Điều phối di chuyển schema + dữ liệu chéo engine (SQL Server/PostgreSQL/SQLite):
    /// đọc schema chuẩn → sắp xếp theo FK → tạo bảng đích (kèm schema) → chép
    /// từng batch có chuyển kiểu → đối chiếu số dòng. Nguồn CHỈ ĐỌC; ghi chỉ đích.
    /// Index phụ không di chuyển (ghi cảnh báo chung một lần).
    /// </summary>
    public sealed class CrossEngineMigrationService
    {
        private readonly ILogger _logger;

        public CrossEngineMigrationService(ILogger logger)
        {
            _logger = logger;
        }

        public static IDbEndpoint GetEndpoint(DatabaseEngine engine)
        {
            return engine switch
            {
                DatabaseEngine.PostgreSql => new PostgresEndpoint(),
                DatabaseEngine.Sqlite => new SqliteEndpoint(),
                DatabaseEngine.SqlServer => new SqlServerEndpoint(),
                DatabaseEngine.MySql => new MySqlEndpoint(),
                DatabaseEngine.MongoDb => new MongoEndpoint(),
                _ => throw new InvalidOperationException("Engine chưa hỗ trợ di chuyển: " + engine)
            };
        }

        /// <summary>Sắp xếp tên bảng theo FK (cha trước con). Thuần logic để kiểm thử.</summary>
        internal static IReadOnlyList<CanonicalTable> OrderTables(IEnumerable<CanonicalTable> tables)
        {
            var list = tables.ToList();
            var names = list.Select(t => t.DisplayName).ToList();
            var deps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list)
            {
                var refs = new List<string>();
                foreach (var fk in t.ForeignKeys)
                {
                    var target = string.IsNullOrWhiteSpace(fk.RefSchema)
                        ? fk.RefTable
                        : fk.RefSchema + "." + fk.RefTable;
                    refs.Add(target);
                }
                deps[t.DisplayName] = refs;
            }
            var orderedNames = ModuleDependencySorter.SortKeys(names, deps);
            var orderedSet = new HashSet<string>(orderedNames, StringComparer.OrdinalIgnoreCase);
            var byName = list.ToDictionary(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);
            var result = new List<CanonicalTable>();
            foreach (var name in orderedNames)
            {
                if (byName.TryGetValue(name, out var table))
                    result.Add(table);
            }
            foreach (var t in list)
            {
                if (!orderedSet.Contains(t.DisplayName))
                    result.Add(t);
            }
            return result;
        }

        public async Task<CrossEngineResult> MigrateAsync(
            DbProbe source, DatabaseEngine srcEngine,
            DbProbe dest, DatabaseEngine dstEngine,
            CrossEngineOptions options,
            CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var tableResults = new List<CrossEngineTableResult>();
            var globalWarnings = new List<string>
            {
                "Index phụ không di chuyển chéo engine — hãy tạo lại tay trên đích nếu cần."
            };

            var srcEp = GetEndpoint(srcEngine);
            var dstEp = GetEndpoint(dstEngine);

            _logger.LogInformation("Đọc schema {Engine} nguồn...", srcEngine);
            var schema = await srcEp.ReadSchemaAsync(source, ct).ConfigureAwait(false);
            var tables = OrderTables(FilterTables(schema.Tables, options)).ToList();
            _logger.LogInformation("Nguồn có {Count} bảng sẽ di chuyển.", tables.Count);

            // Ghi đè: xóa trước theo thứ tự ngược (con trước cha).
            if (options.OverwriteExistingTables)
            {
                foreach (var table in ((IEnumerable<CanonicalTable>)tables).Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (await dstEp.TableExistsAsync(dest, table.Schema, table.Name, ct)
                            .ConfigureAwait(false))
                        {
                            await dstEp.DropTableIfExistsAsync(dest, table.Schema, table.Name,
                                dstEngine == DatabaseEngine.PostgreSql, ct).ConfigureAwait(false);
                            _logger.LogInformation("Đã xóa bảng đích {T} để ghi đè.", table.DisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không xóa được bảng đích {T} ({Message}).", table.DisplayName, ex.Message);
                    }
                }
            }

            // Đảm bảo schema tồn tại trên đích (SQL Server + PostgreSQL).
            // MySQL/SQLite không có schema: kết nối đã trỏ đúng database/file.
            var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                if (!string.IsNullOrWhiteSpace(t.Schema))
                    schemas.Add(t.Schema!);
            }
            foreach (var schemaName in schemas)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (dstEngine == DatabaseEngine.SqlServer && dstEp is SqlServerEndpoint sqlEp)
                        await sqlEp.EnsureSchemaAsync(dest, schemaName, ct).ConfigureAwait(false);
                    else if (dstEngine == DatabaseEngine.PostgreSql)
                        await dstEp.ExecuteDdlAsync(dest,
                            "CREATE SCHEMA IF NOT EXISTS \"" + schemaName.Replace("\"", "\"\"") + "\";",
                            ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không tạo được schema {S} trên đích ({Message}).", schemaName, ex.Message);
                }
            }

            var done = 0;
            foreach (var table in tables)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new MigrationProgress(
                    (int)(done * 100.0 / Math.Max(1, tables.Count)),
                    $"Di chuyển {table.DisplayName} ({done}/{tables.Count})..."));
                tableResults.Add(await MoveTableAsync(
                    srcEp, source, dstEp, dest, dstEngine, table, options, ct).ConfigureAwait(false));
            }

            sw.Stop();
            var result = new CrossEngineResult
            {
                Tables = tableResults,
                Elapsed = sw.Elapsed,
                Warnings = globalWarnings,
                Success = tableResults.All(t => t.Errors.Count == 0)
            };
            _logger.LogInformation("Di chuyển chéo xong sau {Elapsed}: {Rows:n0} dòng.",
                sw.Elapsed, result.TotalRows);
            return result;
        }

        internal static IReadOnlyList<CanonicalTable> FilterTables(
            IReadOnlyList<CanonicalTable> tables, CrossEngineOptions options)
        {
            if (options.OnlyTables == null || options.OnlyTables.Count == 0)
                return tables;
            var wanted = new HashSet<string>(options.OnlyTables, StringComparer.OrdinalIgnoreCase);
            return tables.Where(t => wanted.Contains(t.DisplayName) || wanted.Contains(t.Name)).ToList();
        }

        private async Task<CrossEngineTableResult> MoveTableAsync(
            IDbEndpoint srcEp, DbProbe source,
            IDbEndpoint dstEp, DbProbe dest, DatabaseEngine dstEngine,
            CanonicalTable table, CrossEngineOptions options, CancellationToken ct)
        {
            var result = new CrossEngineTableResult { Table = table.DisplayName };
            var warnings = new List<string>();
            try
            {
                if (await dstEp.TableExistsAsync(dest, table.Schema, table.Name, ct).ConfigureAwait(false))
                {
                    result.Skipped = true;
                    result.SkipReason = "Bảng đã tồn tại trên đích (bật ghi đè để tạo lại).";
                    _logger.LogInformation("Bỏ qua {T}: đã tồn tại trên đích.", table.DisplayName);
                    return result;
                }

                string ddl;
                List<string> emitWarnings;
                if (dstEngine == DatabaseEngine.PostgreSql)
                    ddl = CanonicalDdl.EmitPostgres(table, out emitWarnings);
                else if (dstEngine == DatabaseEngine.Sqlite)
                    ddl = CanonicalDdl.EmitSqlite(table, out emitWarnings);
                else if (dstEngine == DatabaseEngine.MySql)
                    ddl = CanonicalDdl.EmitMySql(table, out emitWarnings);
                else if (dstEngine == DatabaseEngine.MongoDb)
                    ddl = CanonicalDdl.EmitMongo(table, out emitWarnings);
                else
                    ddl = CanonicalDdl.EmitSqlServer(table, out emitWarnings);
                warnings.AddRange(emitWarnings);
                await dstEp.ExecuteDdlAsync(dest, ddl, ct).ConfigureAwait(false);

                var keyIndex = table.PrimaryKeyColumns.Count == 1
                    ? FindColumnIndex(table, table.PrimaryKeyColumns[0])
                    : -1;
                string? afterKey = null;
                long copied = 0;
                long read = 0;
                var batchSize = Math.Clamp(options.BatchRows, 100, 50000);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = await srcEp.ReadBatchAsync(source, table, afterKey, batchSize, ct)
                        .ConfigureAwait(false);
                    if (rows.Count == 0)
                        break;
                    read += rows.Count;
                    var converted = ConvertRows(rows, table, dstEngine);
                    await dstEp.WriteBatchAsync(dest, table, converted, ct).ConfigureAwait(false);
                    copied += converted.Count;
                    if (keyIndex >= 0 && rows[rows.Count - 1].Length > keyIndex)
                        afterKey = CrossEngineKeys.Serialize(rows[rows.Count - 1][keyIndex]);
                    else
                        afterKey = (long.Parse(afterKey ?? "0") + rows.Count).ToString();
                    if (rows.Count < batchSize)
                        break;
                }

                result.RowsCopied = copied;
                result.SourceRows = read;
                result.Warnings = warnings;

                var destCount = await dstEp.CountRowsAsync(dest, table.Schema, table.Name, ct)
                    .ConfigureAwait(false);
                if (destCount != copied)
                {
                    result.Errors = new List<string>
                    {
                        $"Đích có {destCount:N0} dòng nhưng đã chép {copied:N0} — lệch, cần kiểm tra."
                    };
                }
                _logger.LogInformation("Đã chuyển {T}: {Rows:n0} dòng.", table.DisplayName, copied);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors = new List<string> { ex.Message };
                result.Warnings = warnings;
                _logger.LogError(ex, "Di chuyển bảng {T} thất bại.", table.DisplayName);
                return result;
            }
        }

        private static int FindColumnIndex(CanonicalTable table, string name)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (table.Columns[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static IReadOnlyList<object?[]> ConvertRows(
            IReadOnlyList<object?[]> rows, CanonicalTable table, DatabaseEngine dstEngine)
        {
            var converted = new List<object?[]>(rows.Count);
            foreach (var row in rows)
            {
                var next = new object?[row.Length];
                for (var i = 0; i < row.Length && i < table.Columns.Count; i++)
                    next[i] = CanonicalValues.ConvertFor(dstEngine, row[i], table.Columns[i].Type);
                converted.Add(next);
            }
            return converted;
        }
    }
}
