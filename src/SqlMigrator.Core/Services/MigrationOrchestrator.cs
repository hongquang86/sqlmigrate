using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Coordinates the full migration pipeline: extract -> check compatibility ->
    /// build schema -> disable constraints -> copy data -> re-enable constraints.
    /// Each phase is a separate service so the pipeline is easy to test and extend.
    /// </summary>
    public sealed class MigrationOrchestrator
    {
        private readonly MigrationOptions _options;
        private readonly ISchemaExtractor _extractor;
        private readonly ICompatibilityChecker _compatibilityChecker;
        private readonly ISchemaBuilder _schemaBuilder;
        private readonly IConstraintManager _constraintManager;
        private readonly IDataCopier _dataCopier;
        private readonly IDataVerifier _dataVerifier;
        private readonly ILogger _logger;
        private readonly SyncBaselineStore? _baselineStore;

        public MigrationOrchestrator(
            MigrationOptions options,
            ISchemaExtractor extractor,
            ICompatibilityChecker compatibilityChecker,
            ISchemaBuilder schemaBuilder,
            IConstraintManager constraintManager,
            IDataCopier dataCopier,
            ILogger logger,
            SyncBaselineStore? baselineStore = null,
            IDataVerifier? dataVerifier = null)
        {
            _options = options;
            _extractor = extractor;
            _compatibilityChecker = compatibilityChecker;
            _schemaBuilder = schemaBuilder;
            _constraintManager = constraintManager;
            _dataCopier = dataCopier;
            _dataVerifier = dataVerifier ?? new DataVerifier(options, logger);
            _logger = logger;
            _baselineStore = baselineStore;
        }

        public async Task<MigrationResult> MigrateAsync(CancellationToken ct = default, IProgress<MigrationProgress>? progress = null)
        {
            _options.Validate();
            var sw = Stopwatch.StartNew();
            var result = new MigrationResult { Success = true };

            void Report(int percent, string message) => progress?.Report(new MigrationProgress(percent, message));

            // Chốt chặn an toàn: nguồn và đích không được là cùng một database —
            // app chỉ di chuyển một chiều Nguồn → Đích, không bao giờ ghi lên nguồn.
            if (SqlConnectionFactory.IsSameDatabase(
                _options.SourceConnectionString, _options.DestinationConnectionString))
            {
                const string msg = "Từ chối chạy: nguồn và đích trỏ cùng một database trên cùng một server. "
                    + "App chỉ di chuyển một chiều Nguồn → Đích — hãy kiểm tra lại cấu hình kết nối.";
                result.AddError(msg);
                _logger.LogError(msg);
                Report(0, "Lỗi: " + msg);
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                return result;
            }

            // 0a. Chuẩn hóa chuỗi kết nối nguồn/đích (SQL 2008 → 2025): server dùng chứng chỉ
            //     tự ký hoặc không hỗ trợ TLS hiện tại thì mọi thành phần sau (extract, copy,
            //     preflight, verify...) sẽ mở kết nối được ngay từ lần đầu.
            Report(1, "Đang kiểm tra khả năng kết nối nguồn/đích...");
            _options.SourceConnectionString = SqlConnectionFactory.NormalizeConnectionStringWithFallback(
                _options.SourceConnectionString, 30,
                m => _logger.LogInformation("{Message}", m), ct);
            _options.DestinationConnectionString = SqlConnectionFactory.NormalizeConnectionStringWithFallback(
                _options.DestinationConnectionString, 30,
                m => _logger.LogInformation("{Message}", m), ct);

            try
            {
                // 1. Create the destination database when requested (avoids failing the
                //    first connection attempt because the routing database does not exist).
                if (_options.CreateDestinationDatabase)
                {
                    Report(3, "Chuẩn bị database đích...");
                    await new DatabaseProvisioner(_options, _logger).CreateIfMissingAsync(ct).ConfigureAwait(false);
                }

                // Filegroup ROWS còn thiếu (idempotent, chỉ cảnh báo khi lỗi).
                if (_options.Mode != MigrationMode.DataOnly)
                {
                    try
                    {
                        await new DatabaseProvisioner(_options, _logger)
                            .EnsureFilegroupsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Bỏ qua bước filegroup ({Message}).", ex.Message);
                    }
                }

                // 2. Capture the source schema.
                Report(10, "Đang trích xuất cấu trúc nguồn...");
                var schema = await _extractor.ExtractAsync(ct).ConfigureAwait(false);

                // Hồ sơ kiểm kê nguồn (catalog SELECT, rẻ) để đối chiếu sau migrate.
                try
                {
                    Report(12, "Đang kiểm kê database nguồn...");
                    result.SourceInventory = await new DatabaseInventoryReader(_logger)
                        .ReadAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không kiểm kê được nguồn ({Message}).", ex.Message);
                }

                // 3. Compatibility analysis: surface warnings and mark unusable tables skipped.
                Report(18, "Đang phân tích tương thích phiên bản...");
                var compatibility = await _compatibilityChecker.CheckAsync(schema, ct).ConfigureAwait(false);
                foreach (var issue in compatibility.Issues)
                {
                    _logger.LogWarning("{Sev} {Object}: {Message}", issue.Severity, issue.ObjectName, issue.Message);
                    if (issue.Severity == "Warning")
                        result.Warnings = result.Warnings.Concat(new[] { issue }).ToArray();
                }

                foreach (var skip in compatibility.TablesToSkip)
                {
                    var table = schema.Tables.FirstOrDefault(t =>
                        t.PlainName.Equals(skip, StringComparison.OrdinalIgnoreCase));
                    table?.MarkSkipped($"Unsupported feature on destination ({skip})");
                }

                // 4. Schema build (skipped entirely for data_only migrations).
                if (_options.Mode != MigrationMode.DataOnly)
                {
                    Report(30, "Đang tạo cấu trúc database đích...");
                    var built = await _schemaBuilder.BuildAsync(schema, ct).ConfigureAwait(false);
                    result.ObjectsBuilt = built.Built;
                    MergeObjectErrors(result, built.Errors);
                    if (_options.Mode == MigrationMode.Full)
                    {
                        Report(55, "Đang tạo khóa ngoại trên đích...");
                        var deferred = await _schemaBuilder.BuildDeferredObjectsAsync(schema, ct).ConfigureAwait(false);
                        result.ObjectsBuilt += deferred.Built;
                        MergeObjectErrors(result, deferred.Errors);
                    }

                    // Khi „ép thử tạo“ đối tượng feature không hỗ trợ (hạ cấp), bảng nào đích
                    // không tạo được sẽ bị đánh dấu bỏ qua — kèm cảnh báo — thay vì để DataCopier
                    // đổ dữ liệu vào bảng không tồn tại.
                    await MarkMissingTablesAsync(schema, result, ct).ConfigureAwait(false);
                }

                // 5. Data copy (skipped entirely for schema_only migrations).
                if (_options.Mode != MigrationMode.SchemaOnly)
                {
                    var copyable = schema.Tables.Where(t => !t.IsSkipped).ToList();
                    var shouldDefer = _options.DisableForeignKeyConstraints || _options.DisableCheckConstraints;

                    if (shouldDefer)
                    {
                        Report(62, "Đang vô hiệu hóa ràng buộc trên đích...");
                        await _constraintManager.DisableConstraintsAsync(copyable, ct).ConfigureAwait(false);
                    }

                    if (_options.DisableTriggers)
                    {
                        Report(65, "Đang vô hiệu hóa trigger trên đích...");
                        await _constraintManager.DisableTriggersAsync(copyable, ct).ConfigureAwait(false);
                    }

                    if (_options.DisableIndexesDuringLoad)
                    {
                        Report(67, "Đang vô hiệu hóa index phụ trên đích...");
                        await _constraintManager.DisableSecondaryIndexesAsync(copyable, ct).ConfigureAwait(false);
                    }

                    try
                    {
                        Report(70, "Đang sao chép dữ liệu...");
                        result.DataCopy = await _dataCopier.CopyAsync(schema, ct, progress).ConfigureAwait(false);
                        Report(90, "Đang hoàn tất sao chép dữ liệu...");
                    }
                    finally
                    {
                        if (_options.DisableIndexesDuringLoad)
                        {
                            Report(92, "Đang xây dựng lại index phụ...");
                            await _constraintManager.RebuildSecondaryIndexesAsync(copyable, ct).ConfigureAwait(false);
                        }

                        if (_options.DisableTriggers)
                        {
                            Report(94, "Đang bật lại trigger...");
                            await _constraintManager.EnableTriggersAsync(copyable, ct).ConfigureAwait(false);
                        }

                        if (shouldDefer)
                        {
                            Report(96, "Đang bật lại ràng buộc...");
                            await _constraintManager.EnableConstraintsAsync(copyable, _options.CheckDataAfterLoad, ct)
                                .ConfigureAwait(false);
                        }
                    }
                }

                // 6. Kiểm tra toàn vẹn dữ liệu đích so với nguồn (khi được bật) để người dùng
                //    biết ngay sau khi di chuyển: đích thiếu bảng, thiếu dòng hay lệch nội dung.
                if (_options.Mode != MigrationMode.SchemaOnly && _options.VerifyAfterMigration)
                {
                    Report(97, "Đang kiểm tra toàn vẹn dữ liệu đích so với nguồn...");
                    result.Verification = await _dataVerifier.VerifyAsync(schema, ct, progress).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Kiểm tra toàn vẹn xong: {Verified} bảng đúng, {Issues} bảng lệch, {Failed} bảng lỗi đọc, trong {Elapsed}.",
                        result.Verification.VerifiedCount, result.Verification.IssueCount,
                        result.Verification.FailedCount, result.Verification.Elapsed);

                    if (result.Verification.IssueCount > 0 || result.Verification.FailedCount > 0)
                        _logger.LogWarning(
                            "Dữ liệu đích CHƯA đạt toàn vẹn: {Issues} bảng lệch số dòng/checksum/thiếu bảng, {Failed} bảng không đọc được.",
                            result.Verification.IssueCount, result.Verification.FailedCount);
                }

                if (result.DataCopy != null && _baselineStore != null && !_options.Mode.Equals(MigrationMode.SchemaOnly))
                {
                    Report(98, "Đang ghi nhận mốc đồng bộ dữ liệu...");
                    try
                    {
                        var snapshot = await _baselineStore.CaptureAsync(_options, ct).ConfigureAwait(false);
                        await _baselineStore.SaveAsync(snapshot, _options.SourceConnectionString,
                            _options.DestinationConnectionString, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không ghi nhận được mốc đồng bộ sau khi di chuyển: {Message}",
                            ex.Message);
                    }
                }

                // Hồ sơ kiểm kê đích lúc kết thúc (bọc kỹ để không bao giờ làm hỏng kết quả).
                try
                {
                    Report(99, "Đang kiểm kê database đích để đối chiếu...");
                    result.DestinationInventory = await new DatabaseInventoryReader(_logger)
                        .ReadAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không kiểm kê được đích ({Message}).", ex.Message);
                }

                sw.Stop();
                result.Elapsed = sw.Elapsed;
                Report(100, "Hoàn tất.");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Elapsed = sw.Elapsed;
                result.AddError(ex.Message);
                _logger.LogError(ex, "Migration failed: {Message}", ex.Message);
                Report(0, "Lỗi: " + ex.Message);
                return result;
            }
        }

        private static void MergeObjectErrors(MigrationResult result, IReadOnlyList<string> errors)
        {
            if (errors == null || errors.Count == 0) return;
            foreach (var error in errors)
            {
                var list = new List<string>(result.ObjectErrors) { error };
                result.ObjectErrors = list;
            }

            result.ObjectsSkipped += errors.Count;
            result.Success = false;
        }

        /// <summary>
        /// Kiểm tra bảng nào trong schema không tồn tại trên đích sau khi build để đánh dấu
        /// bỏ qua (kèm cảnh báo). Được dùng khi „ép thử tạo“ đối tượng feature không hỗ trợ:
        /// nếu đích từ chối (hạ cấp) thì bảng vắng mặt → skip thay vì để DataCopier lỗi.
        /// </summary>
        private async Task MarkMissingTablesAsync(SchemaModel schema, MigrationResult result, CancellationToken ct)
        {
            // Chỉ cần xử lý khi người dùng chọn ép tạo; ngược lại bảng đã bị skip cứng trước.
            if (!_options.ForceUnsupportedFeatures)
                return;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var conn = new SqlConnection(_options.DestinationConnectionString))
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    using (var cmd = new SqlCommand(
                        "SELECT OBJECT_SCHEMA_NAME(object_id), name FROM sys.tables;", conn)
                    { CommandTimeout = _options.CommandTimeoutSeconds })
                    using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                            present.Add(reader.GetString(0) + "." + reader.GetString(1));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không kiểm tra được bảng tồn tại trên đích: {Message}", ex.Message);
                return;
            }

            foreach (var table in schema.Tables.Where(t => !t.IsSkipped && !t.IsExternal))
            {
                if (present.Contains(table.PlainName))
                    continue;

                table.MarkSkipped($"Đối tượng không tồn tại trên đích sau khi cố tạo ({table.PlainName})");
                result.ObjectsSkipped++;
                result.Warnings = result.Warnings.Concat(new[]
                {
                    new CompatibilityIssue
                    {
                        ObjectName = table.PlainName,
                        Feature = "unsupported-on-destination",
                        Message = $"Bảng '{table.PlainName}' không được tạo trên đích; đã bỏ qua kèm cảnh báo. " +
                                  "Nhấn 'Xuất báo cáo' để xem chi tiết lỗi tạo cấu trúc (ObjectErrors)."
                    }
                }).ToArray();
                _logger.LogWarning("Table {Table} missing on destination after build; marked skipped.", table.PlainName);
            }
        }
    }
}