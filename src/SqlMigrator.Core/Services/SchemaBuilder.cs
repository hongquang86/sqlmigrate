using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Executes the captured scripts on the destination database, one batch at a time,
    /// in dependency-safe order. Non-critical failures are logged and skipped when
    /// <see cref="MigrationOptions.ContinueOnNonCriticalErrors"/> is set.
    /// </summary>
    public sealed class SchemaBuilder : ISchemaBuilder
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public SchemaBuilder(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        public Task<SchemaBuildResult> BuildAsync(SchemaModel schema, CancellationToken ct = default) =>
            RunAsync(schema.Objects, "build", ct);

        public Task<SchemaBuildResult> BuildDeferredObjectsAsync(SchemaModel schema, CancellationToken ct = default) =>
            RunAsync(schema.DeferredObjects, "build deferred objects", ct);

        private async Task<SchemaBuildResult> RunAsync(IReadOnlyList<DatabaseObject> objects, string phase, CancellationToken ct)
        {
            var result = new SchemaBuildResult();
            using var conn = new SqlConnection(_options.DestinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();

                if (obj.IsSkipped)
                {
                    _logger.LogInformation("Skipping {Obj} ({Reason}).", obj.DisplayName, obj.SkipReason ?? "marked skipped");
                    continue;
                }

                foreach (var batch in ScriptUtils.SplitBatches(obj.Definition))
                {
                    if (string.IsNullOrWhiteSpace(batch))
                        continue;

                    try
                    {
                        using var cmd = new SqlCommand(batch, conn)
                        {
                            CommandTimeout = _options.CommandTimeoutSeconds
                        };
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        result.Built++;
                        _logger.LogDebug("Executed [{Type}] {Obj}: {Summary}",
                            obj.Type, obj.DisplayName, Summarize(batch, 80));
                    }
                    catch (Exception ex)
                    {
                        var message = $"Không tạo được [{obj.Type}] {obj.DisplayName}: {ex.Message}";
                        result.AddError(message);
                        _logger.LogError(ex, "Failed to create [{Type}] {Obj}. {Message}",
                            obj.Type, obj.DisplayName, ex.Message);
                        if (_options.FailFast || !_options.ContinueOnNonCriticalErrors)
                            throw new InvalidOperationException(
                                $"Schema build failed at '{obj.DisplayName}' during {phase}: {ex.Message}", ex);
                    }
                }
            }

            _logger.LogInformation("{Phase} completed with {Built} batches executed.",
                phase, result.Built);
            return result;
        }

        private static string Summarize(string sql, int max)
        {
            var single = sql.Replace("\r", " ").Replace("\n", " ");
            while (single.Contains("  "))
                single = single.Replace("  ", " ");
            return single.Length <= max ? single : single.Substring(0, max) + "...";
        }
    }

    /// <summary>Splits a script on GO batch separators (client-side, line based).</summary>
    public static class ScriptUtils
    {
        /// <summary>
        /// Splits <paramref name="sql"/> on lines that are exactly "GO" (any case, with
        /// optional TrailingComments). Batch terminators are removed from the output.
        /// </summary>
        public static IEnumerable<string> SplitBatches(string sql)
        {
            var lines = sql.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var current = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (IsGoBatchTerminator(trimmed))
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }

                    continue;
                }

                current.AppendLine(line);
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private static bool IsGoBatchTerminator(string line)
        {
            if (line.Length < 2 || !line.StartsWith("GO", StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = line.Substring(2).Trim();
            // Optional ", <count>" repetition syntax is not supported by our executors;
            // treat as separator only when the remainder is empty or a comment.
            return rest.Length == 0 || rest.StartsWith("--") || rest.StartsWith("/*");
        }
    }
}