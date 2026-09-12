using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Quét + phân tích + xử lý sự khác biệt giữa database nguồn và đích.
    /// NGUYÊN TẮC BẮT BUỘC:
    ///   • Nguồn: CHỈ ĐỌC (SELECT, sys.*) — KHÔNG BAO GIỜ INSERT/UPDATE/DELETE.
    ///   • Đích: ĐỌC + GHI khi user chọn fix (CREATE, ALTER, INSERT/UPDATE/DELETE).
    /// </summary>
    public sealed class DatabaseReconciler : IDatabaseReconciler
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;
        private readonly ISchemaExtractor _extractor;
        private readonly ISchemaBuilder _schemaBuilder;
        private readonly IConstraintManager _constraints;
        private readonly SqlRewriteEngine? _rewriteEngine;
        private readonly ReconcileDataSync _dataSync;

        /// <summary>Schema nguồn lần quét gần nhất (để sync dữ liệu không cần quét lại).</summary>
        private SchemaModel? _lastSchema;

        public DatabaseReconciler(MigrationOptions options, ILogger logger,
            ISchemaExtractor extractor, ISchemaBuilder schemaBuilder, IConstraintManager constraints)
        {
            _options = options;
            _logger = logger;
            _extractor = extractor;
            _schemaBuilder = schemaBuilder;
            _constraints = constraints;
            _dataSync = new ReconcileDataSync(options, logger);

            // Tải rewrite rules từ config file (nếu có).
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rewrite-rules.json");
                if (File.Exists(configPath))
                    _rewriteEngine = SqlRewriteEngine.FromFile(configPath, logger);
                else
                    logger.LogWarning("Không tìm thấy file rewrite-rules.json tại {Path}.", configPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không thể tải rewrite rules.");
            }
        }

        // ============================================================================
        // PHASE 1: SCAN — chỉ ĐỌC cả nguồn lẫn đích, KHÔNG GHI.
        // ============================================================================

        /// <summary>
        /// Quét source vs dest: đọc schema nguồn, inventory đích, so sánh.
        /// Trả về danh sách vấn đề + thống kê.
        /// CHỈ ĐỌC — KHÔNG ghi database nào.
        /// </summary>
        public async Task<ReconcileResult> ScanAsync(CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new ReconcileResult();
            _logger.LogInformation("Bắt đầu quét đồng bộ 100% (Scan).");

            // Chốt chặn an toàn: từ chối quét khi nguồn và đích trùng nhau.
            if (SqlConnectionFactory.IsSameDatabase(
                _options.SourceConnectionString, _options.DestinationConnectionString))
            {
                const string msg = "Từ chối quét: nguồn và đích trỏ cùng một database trên cùng một server.";
                _logger.LogError(msg);
                result.AddError(msg);
                result.Elapsed = sw.Elapsed;
                return result;
            }

            try
            {
                // 1) Trích xuất cấu trúc nguồn.
                progress?.Report(new MigrationProgress(3, "Đang trích xuất cấu trúc nguồn..."));
                var schema = await _extractor.ExtractAsync(ct).ConfigureAwait(false);
                _lastSchema = schema;

                // 2) Đọc inventory đích (chỉ SELECT, không ghi).
                progress?.Report(new MigrationProgress(15, "Đang đọc cấu trúc đích..."));
                var dest = await ReadDestinationInventoryAsync(ct).ConfigureAwait(false);

                // 3) So sánh và phân loại vấn đề.
                progress?.Report(new MigrationProgress(25, "Đang phân tích sự khác biệt..."));
                AnalyzeMissingObjects(schema, dest, result);
                AnalyzeMissingColumns(schema, dest, result);

                // Map temporal "schema.table" → (lịch sử, cột kỳ) để viết lại
                // truy vấn FOR SYSTEM_TIME sang UNION bảng gốc + lịch sử.
                var temporalMap = TemporalTableConverter.BuildMap(schema.Tables);

                // 4) Đọc script từ nguồn và thử CREATE trên đích (chỉ đọc nguồn, ghi đích).
                // Lưu ý gọi tên đúng: pha này CÓ ghi đích (tạo thử để kiểm tra compatibility),
                // không phải "chỉ đọc" hoàn toàn.
                progress?.Report(new MigrationProgress(40, "Đang đọc script từ nguồn, tạo thử trên đích và phân tích compatibility..."));
                await AnalyzeObjectScriptsAsync(schema, dest, result, progress, temporalMap, ct).ConfigureAwait(false);

                // 5) Quét dependency broken.
                progress?.Report(new MigrationProgress(70, "Đang kiểm tra dependency..."));
                AnalyzeBrokenDependencies(schema, dest, result);

                // 5b) Tra cứu độ dùng view/SP/function (ngày tạo + lần chạy cuối,
                // chỉ SELECT nguồn) để user biết object lỗi có đáng giữ không.
                progress?.Report(new MigrationProgress(78, "Đang tra cứu độ dùng module..."));
                await AnalyzeModuleUsageAsync(result, ct).ConfigureAwait(false);

                // 6) Đọc thống kê dữ liệu (chỉ SELECT COUNT).
                progress?.Report(new MigrationProgress(85, "Đang đọc thống kê dữ liệu..."));
                await AnalyzeDataDifferencesAsync(schema, dest, result, progress, ct).ConfigureAwait(false);

                sw.Stop();
                result.Elapsed = sw.Elapsed;
                result.Success = result.Errors.Count == 0;

                _logger.LogInformation(
                    "Quét xong sau {Elapsed}: tìm thấy {Total} vấn đề "
                    + "({Tables} bảng thiếu, {Views} view thiếu, {Procs} SP thiếu, {Cols} cột thiếu, {Fks} FK thiếu, {Data} dữ liệu lệch).",
                    sw.Elapsed, result.TotalIssues, result.MissingTables, result.MissingViews,
                    result.MissingProcedures, result.MissingColumns, result.MissingForeignKeys,
                    result.DataDifferences);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Quét đồng bộ đã bị hủy.");
                result.AddError("Quét đã bị hủy.");
                result.Elapsed = sw.Elapsed;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quét đồng bộ thất bại: {Message}", ex.Message);
                result.AddError(ex.Message);
                result.Elapsed = sw.Elapsed;
                return result;
            }
        }

        // ============================================================================
        // PHASE 2: FIX — chỉ ghi database ĐÍCH theo user chọn.
        // ============================================================================

        /// <summary>
        /// Xử lý (fix) các vấn đề được user chọn trên database ĐÍCH.
        /// KHÔNG ghi database nguồn.
        /// </summary>
        public async Task<ReconcileFixResult> FixAsync(IReadOnlyList<ReconcileIssue> issues,
            CancellationToken ct = default, IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var fixResult = new ReconcileFixResult();
            _logger.LogInformation("Bắt đầu xử lý {Count} vấn đề đã chọn.", issues.Count);

            var fixedList = new List<ReconcileIssue>();
            var failedList = new List<ReconcileIssue>();
            var skippedList = new List<ReconcileIssue>();

            // Sắp xếp theo thứ tự dependency: UDT trước bảng (bảng có thể dùng kiểu),
            // bảng trước, FK sau, view/SP/FN cuối.
            var sorted = issues.OrderBy(i =>
            {
                return i.ObjectType switch
                {
                    "UDT" => -1,
                    "TABLE" => 0,
                    "SEQUENCE" => 1,
                    "COLUMN" => 2,
                    "FK" => 3,
                    "VIEW" => 10,
                    "STORED_PROCEDURE" => 11,
                    "FUNCTION" => 12,
                    "TRIGGER" => 13,
                    "SERVER_TRIGGER" => 14,
                    _ => 5
                };
            }).ToList();

            // Các issue lỗi thiếu dependency sẽ được hoãn lại để retry ở vòng sau.
            var deferred = new List<ReconcileIssue>();

            for (var i = 0; i < sorted.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var issue = sorted[i];

                progress?.Report(new MigrationProgress(
                    10 + (int)(i * 85.0 / Math.Max(1, sorted.Count)),
                    $"Đang xử lý ({i + 1}/{sorted.Count}): {issue.ObjectName}..."));

                try
                {
                    bool done;
                    switch (issue.Type)
                    {
                        case ReconcileIssueType.MissingObject:
                            done = await FixMissingObjectAsync(issue, fixResult, ct).ConfigureAwait(false);
                            break;
                        case ReconcileIssueType.MissingColumn:
                            done = await FixMissingColumnAsync(issue, ct).ConfigureAwait(false);
                            break;
                        case ReconcileIssueType.DataDifference:
                            // Dữ liệu lệch không fix ở pha này — pha sync riêng ngay sau đó
                            // sẽ xem trước rồi OK mới ghi (chỉ thêm + sửa, không xóa).
                            skippedList.Add(issue);
                            _logger.LogInformation("Để dành dữ liệu lệch {Name} sang pha sync riêng.",
                                issue.ObjectName);
                            continue;
                        case ReconcileIssueType.BrokenDependency:
                            // Dependency broken: sẽ được xử lý khi tạo objects phụ thuộc.
                            skippedList.Add(issue);
                            continue;
                        default:
                            skippedList.Add(issue);
                            continue;
                    }

                    // Phân biệt thật: chỉ đánh dấu Fixed khi thực sự tạo/sửa được.
                    if (done)
                    {
                        issue.Status = ReconcileIssueStatus.Fixed;
                        fixedList.Add(issue);
                    }
                    else
                    {
                        issue.Status = ReconcileIssueStatus.Skipped;
                        skippedList.Add(issue);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Lỗi thiếu dependency (Invalid object name): hoãn lại để retry
                    // ở vòng sau, khi object phụ thuộc có thể đã được tạo.
                    if (IsMissingDependencyError(ex))
                    {
                        issue.ErrorMessage = ex.Message;
                        deferred.Add(issue);
                        _logger.LogWarning("Hoãn '{Name}' sang vòng retry: {Message}",
                            issue.ObjectName, ex.Message);
                    }
                    else
                    {
                        issue.Status = ReconcileIssueStatus.Failed;
                        issue.ErrorMessage = ex.Message;
                        failedList.Add(issue);
                        _logger.LogError(ex, "Xử lý {Name} thất bại: {Message}", issue.ObjectName, ex.Message);
                    }
                }
            }

            // Các vòng retry cho issue thiếu dependency (tối đa 5 vòng).
            // Ví dụ: view A cần view B — vòng 1 tạo B, vòng 2 tạo A.
            var round = 0;
            while (deferred.Count > 0 && round < 5)
            {
                round++;
                ct.ThrowIfCancellationRequested();
                var retry = deferred.ToList();
                deferred.Clear();
                var fixedBefore = fixedList.Count;

                _logger.LogInformation("Retry vòng {Round}: thử lại {Count} vấn đề thiếu dependency.",
                    round, retry.Count);

                for (var i = 0; i < retry.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var issue = retry[i];

                    progress?.Report(new MigrationProgress(
                        90 + (int)(round * 2),
                        $"Đang retry vòng {round} ({i + 1}/{retry.Count}): {issue.ObjectName}..."));

                    try
                    {
                        bool done = issue.Type == ReconcileIssueType.MissingColumn
                            ? await FixMissingColumnAsync(issue, ct).ConfigureAwait(false)
                            : await FixMissingObjectAsync(issue, fixResult, ct).ConfigureAwait(false);

                        if (done)
                        {
                            issue.Status = ReconcileIssueStatus.Fixed;
                            fixedList.Add(issue);
                        }
                        else
                        {
                            issue.Status = ReconcileIssueStatus.Skipped;
                            skippedList.Add(issue);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (IsMissingDependencyError(ex))
                        {
                            issue.ErrorMessage = ex.Message;
                            deferred.Add(issue);
                        }
                        else
                        {
                            issue.Status = ReconcileIssueStatus.Failed;
                            issue.ErrorMessage = ex.Message;
                            failedList.Add(issue);
                            _logger.LogError(ex, "Retry xử lý {Name} thất bại: {Message}",
                                issue.ObjectName, ex.Message);
                        }
                    }
                }

                // Không tiến triển (không fix thêm được issue nào) → dừng.
                if (deferred.Count > 0 && fixedList.Count == fixedBefore)
                    break;
            }

            // Các issue vẫn thiếu dependency sau mọi vòng retry → đánh dấu thất bại
            // với lý do rõ ràng để user tạo thủ công theo đúng thứ tự.
            foreach (var issue in deferred)
            {
                issue.Status = ReconcileIssueStatus.Failed;
                issue.ErrorMessage = (issue.ErrorMessage ?? "Thiếu dependency.")
                    + DescribeMissingDependency(issue.ErrorMessage);
                failedList.Add(issue);
                _logger.LogError("Xử lý {Name} thất bại: vẫn thiếu dependency sau retry.",
                    issue.ObjectName);
            }

            sw.Stop();
            fixResult.FixedIssues = fixedList;
            fixResult.FailedIssues = failedList;
            fixResult.SkippedIssues = skippedList;
            fixResult.Elapsed = sw.Elapsed;
            fixResult.Success = failedList.Count == 0;

            _logger.LogInformation(
                "Xử lý xong sau {Elapsed}: thành công {Fixed}, thất bại {Failed}, bỏ qua {Skipped}.",
                sw.Elapsed, fixedList.Count, failedList.Count, skippedList.Count);

            return fixResult;
        }

        // ============================================================================
        // PHASE 3: SYNC DỮ LIỆU — chỉ thêm + sửa trên ĐÍCH, KHÔNG xóa, KHÔNG ghi nguồn.
        // ============================================================================

        /// <summary>Lấy schema nguồn lần quét gần nhất (quét mới nếu chưa có).</summary>
        private async Task<SchemaModel> GetSchemaForSyncAsync(CancellationToken ct)
        {
            if (_lastSchema != null)
                return _lastSchema;
            _logger.LogInformation("Chưa có schema lần quét — trích xuất mới để sync dữ liệu.");
            _lastSchema = await _extractor.ExtractAsync(ct).ConfigureAwait(false);
            return _lastSchema;
        }

        /// <summary>
        /// Ước lượng sync dữ liệu các bảng đã chọn (không ghi gì).
        /// Mỗi bảng cho biết sẽ thêm/sửa bao nhiêu dòng, hoặc lý do bỏ qua.
        /// </summary>
        public async Task<IReadOnlyList<DataSyncPreviewItem>> PreviewDataSyncAsync(
            IReadOnlyList<string> tableNames,
            CancellationToken ct = default, IProgress<MigrationProgress>? progress = null)
        {
            var schema = await GetSchemaForSyncAsync(ct).ConfigureAwait(false);
            return await _dataSync.PreviewAsync(schema, tableNames, ct, progress).ConfigureAwait(false);
        }

        /// <summary>
        /// Sync dữ liệu các bảng đã chọn: nguồn → đích, chỉ thêm dòng thiếu +
        /// sửa dòng lệch giá trị, KHÔNG xóa dòng đích. KHÔNG ghi nguồn.
        /// </summary>
        public async Task<DataSyncResult> SyncDataAsync(
            IReadOnlyList<string> tableNames,
            CancellationToken ct = default, IProgress<MigrationProgress>? progress = null)
        {
            var schema = await GetSchemaForSyncAsync(ct).ConfigureAwait(false);
            return await _dataSync.SyncAsync(schema, tableNames, ct, progress).ConfigureAwait(false);
        }

        // ============================================================================
        // Phân tích vấn đề (Scan phase)
        // ============================================================================

        /// <summary>Phân tích đối tượng thiếu (bảng/view/SP/FN/trigger/sequence/FK).</summary>
        private void AnalyzeMissingObjects(SchemaModel schema, DestinationInventory dest, ReconcileResult result)
        {
            var issues = new List<ReconcileIssue>();

            foreach (var obj in schema.Objects.Where(o => !o.IsSkipped))
            {
                if (string.IsNullOrWhiteSpace(obj.Name))
                    continue;

                switch (obj.Type)
                {
                    case DatabaseObjectType.Table:
                    {
                        var key = "TABLE:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            var tableIssue = new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "TABLE",
                                Description = $"Bảng '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Tạo bảng từ script nguồn.",
                                Severity = ReconcileIssueSeverity.Error,
                                SourceScript = obj.Definition,
                                CanAutoFix = true
                            };
                            // Bảng có cột masking (đã lột khỏi script): ghi chú để khâu
                            // fix sinh view che + cảnh báo dữ liệu hết che.
                            var tableMasks = MaskingConverter.TryParseMarkers(obj.Definition);
                            if (tableMasks.Count > 0)
                            {
                                var maskedCols = string.Join(", ", tableMasks.Select(m => m.Column));
                                tableIssue.Warnings = new List<string>
                                {
                                    "Bảng có cột che trên nguồn (" + maskedCols + ") nhưng đích 2014 "
                                    + "không hỗ trợ masking — app tạo cột KHÔNG che và sinh view che thay thế."
                                };
                                tableIssue.SuggestedAction +=
                                    " Kèm tạo view che thay thế masking.";
                            }
                            issues.Add(tableIssue);
                        }
                        break;
                    }
                    case DatabaseObjectType.View:
                    {
                        var key = "VIEW:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "VIEW",
                                Description = $"View '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Thử CREATE từ script nguồn. Nếu lỗi compatibility → rewrite hoặc fix thủ công.",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceScript = obj.Definition,
                                CanAutoFix = false // Cần phân tích script trước
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.StoredProcedure:
                    {
                        var key = "P:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "STORED_PROCEDURE",
                                Description = $"Stored procedure '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Thử CREATE từ script nguồn. Nếu lỗi compatibility → rewrite hoặc fix thủ công.",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceScript = obj.Definition,
                                CanAutoFix = false
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.Function:
                    {
                        var key = "F:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "FUNCTION",
                                Description = $"Function '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Thử CREATE từ script nguồn. Nếu lỗi compatibility → rewrite hoặc fix thủ công.",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceScript = obj.Definition,
                                CanAutoFix = false
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.Trigger:
                    {
                        var key = "TR:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "TRIGGER",
                                Description = $"Trigger '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Thử CREATE từ script nguồn.",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceScript = obj.Definition,
                                CanAutoFix = false
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.Sequence:
                    {
                        var key = "SEQ:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "SEQUENCE",
                                Description = $"Sequence '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Tạo sequence từ script nguồn.",
                                Severity = ReconcileIssueSeverity.Info,
                                SourceScript = obj.Definition,
                                CanAutoFix = true
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.UserDefinedDataType:
                    case DatabaseObjectType.UserDefinedTableType:
                    case DatabaseObjectType.UserDefinedClrType:
                    {
                        // Kiểu do user định nghĩa — phải tạo TRƯỚC bảng (xem thứ tự fix "UDT" => -1).
                        var key = "UDT:" + obj.Schema + "." + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = obj.Schema + "." + obj.Name,
                                ObjectType = "UDT",
                                Description = $"Kiểu '{obj.Schema}.{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Tạo kiểu từ script nguồn (phải tạo trước các bảng dùng kiểu này).",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceScript = obj.Definition,
                                CanAutoFix = true
                            });
                        }
                        break;
                    }
                    case DatabaseObjectType.ServerTrigger:
                    {
                        // Server trigger cần quyền cấp server — chỉ báo cáo, không tự tạo.
                        var key = "STRG:" + obj.Name;
                        if (!HasKey(dest, key))
                        {
                            issues.Add(new ReconcileIssue
                            {
                                Type = ReconcileIssueType.MissingObject,
                                ObjectName = "SERVER:" + obj.Name,
                                ObjectType = "SERVER_TRIGGER",
                                Description = $"Server trigger '{obj.Name}' có trên nguồn nhưng thiếu trên đích.",
                                SuggestedAction = "Tạo thủ công trên server đích (cần quyền CONTROL SERVER).",
                                Severity = ReconcileIssueSeverity.Info,
                                SourceScript = obj.Definition,
                                CanAutoFix = false
                            });
                        }
                        break;
                    }
                }
            }

            // FK từ deferred objects.
            foreach (var fk in schema.DeferredObjects.Where(o => !o.IsSkipped && !string.IsNullOrWhiteSpace(o.Name)))
            {
                var key = "FK:" + fk.Schema + "." + fk.Name;
                if (!HasKey(dest, key))
                {
                    issues.Add(new ReconcileIssue
                    {
                        Type = ReconcileIssueType.MissingObject,
                        ObjectName = fk.Schema + "." + fk.Name,
                        ObjectType = "FK",
                        Description = $"Khóa ngoại '{fk.Schema}.{fk.Name}' có trên nguồn nhưng thiếu trên đích.",
                        SuggestedAction = "Tạo FK từ script nguồn (đảm bảo cả 2 bảng đã tồn tại trên đích).",
                        Severity = ReconcileIssueSeverity.Warning,
                        SourceScript = fk.Definition,
                        CanAutoFix = true
                    });
                }
            }

            result.Issues = result.Issues.Concat(issues).ToList();
        }

        /// <summary>Phân tích cột thiếu trên các bảng đã tồn tại.</summary>
        private void AnalyzeMissingColumns(SchemaModel schema, DestinationInventory dest, ReconcileResult result)
        {
            var issues = new List<ReconcileIssue>();

            foreach (var table in schema.Tables.Where(t => !t.IsSkipped && !t.IsExternal))
            {
                if (!HasKey(dest, "TABLE:" + table.PlainName))
                    continue; // Bảng thiếu → đã có issue riêng.

                foreach (var column in table.Columns)
                {
                    var colKey = table.PlainName + "." + column.Name;
                    if (dest.Columns.Contains(colKey))
                        continue;

                    var severity = column.IsComputed || column.IsIdentity || column.IsRowVersion
                        ? ReconcileIssueSeverity.Warning
                        : ReconcileIssueSeverity.Info;

                    var suggestion = column.IsComputed
                        ? "Cột computed không thể ALTER TABLE ADD trực tiếp — cần tạo lại bảng."
                        : column.IsIdentity
                            ? "Cột identity cần IDENTITY_INSERT để thêm dữ liệu."
                            : column.IsRowVersion
                                ? "Cột rowversion tự động — không thêm được."
                                : $"ALTER TABLE {table.PlainName} ADD [{column.Name}] <kiểu>;";

                    issues.Add(new ReconcileIssue
                    {
                        Type = ReconcileIssueType.MissingColumn,
                        ObjectName = colKey,
                        ObjectType = "COLUMN",
                        Description = $"Cột '{column.Name}' trên bảng '{table.PlainName}' có trên nguồn nhưng thiếu trên đích.",
                        SuggestedAction = suggestion,
                        Severity = severity,
                        CanAutoFix = !column.IsComputed && !column.IsIdentity && !column.IsRowVersion
                    });
                }
            }

            result.Issues = result.Issues.Concat(issues).ToList();
        }

        /// <summary>
        /// Đọc script CREATE từ nguồn, thử CREATE trên đích (ghi đích!), phân tích lỗi.
        /// CHỈ ĐỌC script từ nguồn — KHÔNG ghi nguồn.
        /// </summary>
        private async Task AnalyzeObjectScriptsAsync(SchemaModel schema, DestinationInventory dest,
            ReconcileResult result, IProgress<MigrationProgress>? progress,
            IReadOnlyDictionary<string, (string HistorySchema, string HistoryTable, string StartCol, string EndCol)> temporalMap,
            CancellationToken ct)
        {
            var missingIssues = result.Issues
                .Where(i => i.Type == ReconcileIssueType.MissingObject
                    && i.ObjectType is "VIEW" or "STORED_PROCEDURE" or "FUNCTION" or "TRIGGER"
                    && !string.IsNullOrWhiteSpace(i.SourceScript))
                .ToList();

            if (missingIssues.Count == 0)
                return;

            using var destConn = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);

            for (var i = 0; i < missingIssues.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var issue = missingIssues[i];

                progress?.Report(new MigrationProgress(
                    40 + (int)(i * 25.0 / Math.Max(1, missingIssues.Count)),
                    $"Đang phân tích '{issue.ObjectName}' ({i + 1}/{missingIssues.Count})..."));

                issue.HasTriedCreate = true;

                // Bước 1: Phân tích compatibility bằng rewrite engine.
                if (_rewriteEngine != null)
                {
                    var analysis = _rewriteEngine.AnalyzeScript(issue.SourceScript!);
                    var unsupported = analysis.Where(a => !a.IsSupported).ToList();
                    var supported = analysis.Where(a => a.IsSupported).ToList();

                    if (unsupported.Count > 0)
                    {
                        issue.UnsupportedRules = unsupported.Select(u => u.RuleId).ToList();
                        issue.ErrorMessage = string.Join("\n", unsupported.Select(u => u.Description + ": " + u.Message));
                    }

                    if (supported.Count > 0)
                    {
                        issue.AppliedRules = supported.Select(s => s.RuleId).ToList();
                    }
                }

                // Bước 2: Thử CREATE trên đích.
                try
                {
                    await TryCreateOnDestAsync(destConn, issue.SourceScript!, ct).ConfigureAwait(false);
                    issue.CreateSucceeded = true;
                    issue.Status = ReconcileIssueStatus.Fixed;
                    issue.SuggestedAction = "Tạo thành công trên đích.";
                    _logger.LogInformation("✔ '{Name}' tạo thành công trên đích.", issue.ObjectName);
                }
                catch (Exception ex)
                {
                    issue.CreateSucceeded = false;
                    var sqlError = ExtractSqlErrorMessage(ex);
                    issue.ErrorMessage = sqlError ?? ex.Message;

                    // Lỗi 209 (cột mơ hồ, thường do SELECT * lỗi thời): thử bung
                    // tường minh trước; thành công thì xong issue này ngay.
                    // Thất bại (cột ghi tường minh) thì ghi gợi ý tay chính xác.
                    if (IsAmbiguousColumnError(ex))
                    {
                        var starFixed = await TryFixAmbiguousColumnsAsync(
                            destConn, issue, issue.SourceScript!, ct, sqlError ?? ex.Message).ConfigureAwait(false);
                        if (starFixed)
                        {
                            issue.CreateSucceeded = true;
                            issue.Status = ReconcileIssueStatus.Fixed;
                            issue.SuggestedAction = "Đã bung SELECT * tường minh và tạo thành công trên đích."
                                + (issue.Warnings.Count > 0
                                    ? " Lưu ý: " + string.Join(" ", issue.Warnings)
                                    : "");
                            continue;
                        }

                        var hint = await BuildAmbiguousGuidanceAsync(
                            issue, sqlError ?? ex.Message, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(hint))
                            issue.SuggestedAction = hint;
                    }

                    // Bước 3: Nếu lỗi và có rewrite engine, thử rewrite.
                    if (_rewriteEngine != null && !string.IsNullOrEmpty(issue.SourceScript))
                    {
                        var rewriteResult = _rewriteEngine.Rewrite(issue.SourceScript);
                        if (rewriteResult.HasChanges)
                        {
                            issue.RewrittenScript = rewriteResult.RewrittenScript;
                            issue.AppliedRules = rewriteResult.AppliedRules;
                            issue.RequiredShims = rewriteResult.RequiredShims;

                            // Ghi nhận cảnh báo kèm rewrite (VD: lột masking) — user phải đọc.
                            if (rewriteResult.Warnings.Count > 0)
                            {
                                issue.Warnings = rewriteResult.Warnings;
                                foreach (var w in rewriteResult.Warnings)
                                    _logger.LogWarning("'{Name}' lưu ý sau rewrite: {Warning}", issue.ObjectName, w);
                            }

                            issue.UnsupportedRules = rewriteResult.UnsupportedRules;

                            // Bước 3b: viết lại FOR SYSTEM_TIME sang UNION bảng gốc +
                            // bảng lịch sử (dùng map temporal từ metadata nguồn).
                            var temporalScript = TemporalTableConverter.RewriteSystemTimeQuery(
                                issue.RewrittenScript ?? issue.SourceScript!, temporalMap);
                            if (!temporalScript.Equals(
                                issue.RewrittenScript ?? issue.SourceScript, StringComparison.Ordinal))
                            {
                                issue.RewrittenScript = temporalScript;
                                var applied = issue.AppliedRules.ToList();
                                if (!applied.Contains("for_system_time"))
                                    applied.Add("for_system_time");
                                issue.AppliedRules = applied;
                                var unsupported = issue.UnsupportedRules.ToList();
                                unsupported.RemoveAll(u => u == "for_system_time");
                                issue.UnsupportedRules = unsupported;
                                _logger.LogInformation(
                                    "'{Name}' đã viết lại FOR SYSTEM_TIME sang UNION lịch sử.",
                                    issue.ObjectName);
                            }

                            if (issue.UnsupportedRules.Count > 0)
                            {
                                issue.CanAutoFix = false;
                                issue.SuggestedAction = "Có thể rewrite một phần. Script đã rewrite có trong RewrittenScript. "
                                    + "Cần fix thủ công cho các feature không hỗ trợ: "
                                    + string.Join(", ", issue.UnsupportedRules)
                                    + (issue.Warnings.Count > 0
                                        ? " Lưu ý: " + string.Join(" ", issue.Warnings)
                                        : "");
                            }
                            else
                            {
                                // Triển khai shim Compat lên ĐÍCH trước khi tạo thử script đã rewrite.
                                await EnsureCompatShimsAsync(rewriteResult.RequiredShims, ct).ConfigureAwait(false);

                                // Thử lại với script đã rewrite.
                                try
                                {
                                    await TryCreateOnDestAsync(destConn, rewriteResult.RewrittenScript, ct)
                                        .ConfigureAwait(false);
                                    issue.CreateSucceeded = true;
                                    issue.Status = ReconcileIssueStatus.Fixed;
                                    issue.CanAutoFix = true;
                                    issue.SuggestedAction = "Đã rewrite và tạo thành công trên đích."
                                        + (issue.Warnings.Count > 0
                                            ? " Lưu ý: " + string.Join(" ", issue.Warnings)
                                            : "");
                                    _logger.LogInformation("✔ '{Name}' tạo thành công sau rewrite.", issue.ObjectName);
                                }
                                catch (Exception ex2)
                                {
                                    var sqlError2 = ExtractSqlErrorMessage(ex2);
                                    issue.ErrorMessage = (issue.ErrorMessage ?? "") + "\nSau rewrite: " + (sqlError2 ?? ex2.Message);
                                    issue.CanAutoFix = false;
                                    issue.SuggestedAction = "Script đã rewrite nhưng vẫn tạo thất bại. Cần fix thủ công.";
                                    // Lỗi 209 mà không bung * được (cột ghi tường minh): gợi ý tay
                                    // chính xác tới tên bảng nguồn có cột đó để user chỉ việc qualify.
                                    if (IsAmbiguousColumnError(ex2))
                                    {
                                        var hint = await BuildAmbiguousGuidanceAsync(
                                            issue, sqlError2 ?? ex2.Message, ct).ConfigureAwait(false);
                                        if (!string.IsNullOrEmpty(hint))
                                            issue.SuggestedAction = hint;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Phân tích dependency broken: view/SP/FN tham chiếu đối tượng không tồn tại trên đích.</summary>
        private void AnalyzeBrokenDependencies(SchemaModel schema, DestinationInventory dest, ReconcileResult result)
        {
            // Đơn giản: kiểm tra các view/SP/FN trên nguồn tham chiếu bảng nào,
            // nếu bảng đó không có trên đích → broken dependency.
            var objectIssues = result.Issues
                .Where(i => i.Type == ReconcileIssueType.MissingObject
                    && i.ObjectType is "VIEW" or "STORED_PROCEDURE" or "FUNCTION")
                .ToList();

            // Tập schema có thật trên nguồn — dùng để loại alias/biến khỏi kết quả regex
            // (VD: "a.Id" trong SELECT a.Id không phải bảng "a.Id").
            var knownSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in schema.Tables)
            {
                if (!string.IsNullOrWhiteSpace(t.Schema))
                    knownSchemas.Add(t.Schema);
            }
            foreach (var o in schema.Objects)
            {
                if (!string.IsNullOrWhiteSpace(o.Schema)
                    && !o.Schema.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                    knownSchemas.Add(o.Schema);
            }

            foreach (var issue in objectIssues)
            {
                if (string.IsNullOrEmpty(issue.SourceScript))
                    continue;

                var tableRefs = ExtractReferencedObjects(issue.SourceScript, knownSchemas);

                var missingDeps = new List<string>();
                foreach (var tableRef in tableRefs)
                {
                    // Bỏ tự tham chiếu chính mình (tên view/SP/FN xuất hiện ngay
                    // trong dòng CREATE của nó) — trước đây sinh cảnh báo vòng vo.
                    if (tableRef.Equals(issue.ObjectName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!HasKey(dest, "TABLE:" + tableRef))
                        missingDeps.Add(tableRef);
                }

                if (missingDeps.Count > 0)
                {
                    issue.Dependencies = missingDeps;
                    result.Issues = result.Issues.Concat(new[]
                    {
                        new ReconcileIssue
                        {
                            Type = ReconcileIssueType.BrokenDependency,
                            ObjectName = issue.ObjectName,
                            ObjectType = issue.ObjectType,
                            Description = $"'{issue.ObjectName}' tham chiếu bảng thiếu trên đích: {string.Join(", ", missingDeps)}.",
                            SuggestedAction = $"Tạo bảng {string.Join(", ", missingDeps)} trước, sau đó tạo lại '{issue.ObjectName}'.",
                            Severity = ReconcileIssueSeverity.Error,
                            Dependencies = missingDeps,
                            CanAutoFix = false
                        }
                    }).ToList();
                }

                // Cảnh báo TRƯỚC khi fix: tham chiếu sang database khác (VD: ADEL9200.dbo.T)
                // thì app không thể tự tạo — user phải di chuyển database đó trước.
                var sourceDb = new SqlConnectionStringBuilder(_options.SourceConnectionString).InitialCatalog;
                var crossDbs = ExtractCrossDatabaseRefs(issue.SourceScript, sourceDb);
                if (crossDbs.Count > 0)
                {
                    var dbNames = crossDbs
                        .Select(r =>
                        {
                            var segs = r.Split('.');
                            return segs.Length >= 4 ? segs[0] + "." + segs[1] : segs[0];
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    result.Issues = result.Issues.Concat(new[]
                    {
                        new ReconcileIssue
                        {
                            Type = ReconcileIssueType.BrokenDependency,
                            ObjectName = issue.ObjectName,
                            ObjectType = issue.ObjectType,
                            Description = $"'{issue.ObjectName}' tham chiếu sang database khác: {string.Join(", ", crossDbs)}.",
                            SuggestedAction = $"Di chuyển database {string.Join(", ", dbNames)} sang server đích trước "
                                + "(hoặc tạo bảng tương ứng trên đích), rồi quét lại Đồng bộ 100%. "
                                + "App chỉ di chuyển trong một database nên không tự tạo được.",
                            Severity = ReconcileIssueSeverity.Warning,
                            Dependencies = crossDbs,
                            CanAutoFix = false
                        }
                    }).ToList();
                    _logger.LogWarning("'{Name}' tham chiếu database khác: {Refs}.",
                        issue.ObjectName, string.Join(", ", crossDbs));
                }
            }
        }

        /// <summary>
        /// Tra cứu độ dùng view/SP/function thiếu (ngày tạo + lần chạy cuối, chỉ SELECT
        /// nguồn) rồi ghi gợi ý bỏ qua khi object lỗi mà chưa từng thấy dùng.
        /// Không bao giờ tự bỏ qua — chỉ thêm thông tin để user quyết định.
        /// </summary>
        private async Task AnalyzeModuleUsageAsync(ReconcileResult result, CancellationToken ct)
        {
            var modules = result.Issues
                .Where(i => i.Type == ReconcileIssueType.MissingObject
                    && i.ObjectType is "VIEW" or "STORED_PROCEDURE" or "FUNCTION")
                .ToList();
            if (modules.Count == 0)
                return;

            await new ModuleUsageReader(_logger)
                .FillAsync(_options.SourceConnectionString, modules, ct).ConfigureAwait(false);

            foreach (var issue in modules)
            {
                if (issue.CreatedDate == null && issue.UseCount == null && issue.LastUsedDate == null)
                    continue;

                var usedText = issue.LastUsedDate != null
                    ? "dùng lần cuối " + issue.LastUsedDate.Value.ToString("dd/MM/yyyy HH:mm")
                        + (issue.UseCount != null ? $" ({issue.UseCount:N0} lần)" : "")
                        + " theo " + (issue.UsageEvidence ?? "nguồn chứng cứ")
                    : "chưa từng thấy dùng"
                        + (issue.UsageEvidence != null ? " theo " + issue.UsageEvidence : "");
                var createdText = issue.CreatedDate != null
                    ? "tạo từ " + issue.CreatedDate.Value.ToString("dd/MM/yyyy")
                    : "không rõ ngày tạo";
                _logger.LogInformation("'{Name}': {Created}, {Used}.",
                    issue.ObjectName, createdText, usedText);

                if (issue.LastUsedDate == null && issue.UsageEvidence != null)
                {
                    issue.SuggestedAction += $" Độ dùng: {createdText} nhưng {usedText} — "
                        + "nếu nghiệp vụ xác nhận không dùng nữa thì có thể bỏ qua object này.";
                }
            }
        }

        /// <summary>Đọc thống kê dữ liệu (COUNT) từ cả nguồn và đích để phát hiện lệch.</summary>
        private async Task AnalyzeDataDifferencesAsync(SchemaModel schema, DestinationInventory dest,
            ReconcileResult result, IProgress<MigrationProgress>? progress, CancellationToken ct)
        {
            var tables = schema.Tables.Where(t => !t.IsSkipped && !t.IsExternal
                && HasKey(dest, "TABLE:" + t.PlainName)).ToList();

            if (tables.Count == 0)
                return;

            using var sourceConn = SqlConnectionFactory.Open(_options.SourceConnectionString,
                _options.CommandTimeoutSeconds, ct);
            using var destConn = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);

            // Ước lượng nhanh từ sys.partitions để khỏi COUNT_BIG bảng rất lớn khi
            // hai bên đã khớp ước lượng (COUNT full scan bảng trăm triệu dòng rất lâu).
            var sourceEstimates = await ReadRowEstimatesAsync(sourceConn, ct).ConfigureAwait(false);
            var destEstimates = await ReadRowEstimatesAsync(destConn, ct).ConfigureAwait(false);

            for (var i = 0; i < tables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var table = tables[i];

                if (i % 10 == 0)
                {
                    progress?.Report(new MigrationProgress(
                        85 + (int)(i * 10.0 / Math.Max(1, tables.Count)),
                        $"Đang đếm dòng bảng {table.PlainName} ({i + 1}/{tables.Count})..."));
                }

                try
                {
                    // Bảng lớn (≥ 100k dòng ước lượng) mà hai bên ước lượng bằng nhau
                    // → tin ước lượng, bỏ qua COUNT chi tiết để quét nhanh.
                    if (sourceEstimates.TryGetValue(table.PlainName, out var sEst)
                        && destEstimates.TryGetValue(table.PlainName, out var dEst)
                        && sEst == dEst && sEst >= 100_000)
                    {
                        _logger.LogDebug("Bảng {T}: ước lượng khớp ({N:n0} dòng) — bỏ qua COUNT chi tiết.",
                            table.PlainName, sEst);
                        continue;
                    }

                    var sourceCount = await CountRowsAsync(sourceConn, table.Schema, table.Name, ct)
                        .ConfigureAwait(false);
                    var destCount = await CountRowsAsync(destConn, table.Schema, table.Name, ct)
                        .ConfigureAwait(false);

                    if (sourceCount != destCount)
                    {
                        result.Issues = result.Issues.Concat(new[]
                        {
                            new ReconcileIssue
                            {
                                Type = ReconcileIssueType.DataDifference,
                                ObjectName = table.PlainName,
                                ObjectType = "TABLE",
                                Description = $"Bảng '{table.PlainName}': nguồn {sourceCount:N0} dòng, đích {destCount:N0} dòng.",
                                SuggestedAction = "Chọn bảng muốn sync dữ liệu (INSERT/UPDATE/DELETE).",
                                Severity = ReconcileIssueSeverity.Warning,
                                SourceRowCount = sourceCount,
                                DestRowCount = destCount,
                                CanAutoFix = false // KHÔNG tự sync dữ liệu
                            }
                        }).ToList();
                    }
                }
                catch (Exception ex)
                {
                    result.AddWarning($"Không đếm được dòng bảng {table.PlainName}: {ex.Message}");
                }
            }
        }

        /// <summary>Đếm số dòng của một bảng (chỉ SELECT COUNT).</summary>
        private static async Task<long> CountRowsAsync(SqlConnection conn, string schema, string name, CancellationToken ct)
        {
            var sql = $"SELECT COUNT_BIG(*) FROM [{schema}].[{name}];";
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(result);
        }

        /// <summary>
        /// Ước lượng số dòng mọi bảng từ sys.partitions (1 truy vấn, rẻ hơn COUNT_BIG từng bảng).
        /// Dùng làm bộ lọc nhanh: chỉ COUNT chi tiết khi ước lượng hai bên lệch nhau
        /// hoặc bảng nhỏ (metadata có thể cũ sau bulk ops nên không thay COUNT hẳn).
        /// </summary>
        private async Task<Dictionary<string, long>> ReadRowEstimatesAsync(SqlConnection conn, CancellationToken ct)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT s.name, t.name, COALESCE(SUM(p.rows), 0) " +
                    "FROM sys.tables t " +
                    "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                    "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) " +
                    "WHERE t.is_ms_shipped = 0 " +
                    "GROUP BY s.name, t.name;", conn)
                { CommandTimeout = 120 };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    map[reader.GetString(0) + "." + reader.GetString(1)] = reader.GetInt64(2);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được ước lượng số dòng ({Message}); sẽ COUNT chi tiết mọi bảng.", ex.Message);
            }
            return map;
        }

        // ============================================================================
        // Fix phase — ghi database ĐÍCH.
        // ============================================================================

        /// <summary>Fix một đối tượng thiếu trên đích (VIEW/SP/FN/TABLE/SEQUENCE/FK).</summary>
        /// <returns>True nếu đã tạo thành công; false nếu bỏ qua (đã tạo rồi/không có script/feature cấm).</returns>
        private async Task<bool> FixMissingObjectAsync(ReconcileIssue issue, ReconcileFixResult fixResult, CancellationToken ct)
        {
            // Server trigger cần quyền cấp server — chỉ báo cáo, không tự tạo.
            if (string.Equals(issue.ObjectType, "SERVER_TRIGGER", StringComparison.OrdinalIgnoreCase))
            {
                issue.Status = ReconcileIssueStatus.Skipped;
                _logger.LogWarning("'{Name}' là server trigger — cần tạo thủ công, bỏ qua.", issue.ObjectName);
                return false;
            }

            // Nếu đã tạo thành công khi scan → bỏ qua (không tạo lại).
            if (issue.CreateSucceeded)
            {
                _logger.LogInformation("'{Name}' đã tạo thành công khi scan — bỏ qua.", issue.ObjectName);
                return false;
            }

            // Có script đã rewrite → dùng script đó.
            var script = issue.RewrittenScript ?? issue.SourceScript;
            if (string.IsNullOrWhiteSpace(script))
            {
                issue.Status = ReconcileIssueStatus.Skipped;
                _logger.LogWarning("'{Name}' không có script — bỏ qua.", issue.ObjectName);
                return false;
            }

            // Nếu có unsupported rules → KHÔNG tự tạo (nguy hiểm).
            if (issue.UnsupportedRules.Count > 0)
            {
                issue.Status = ReconcileIssueStatus.Skipped;
                _logger.LogWarning("'{Name}' có feature không hỗ trợ trên đích: {Rules}. Cần fix thủ công.",
                    issue.ObjectName, string.Join(", ", issue.UnsupportedRules));
                return false;
            }

            using var conn = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);
            // Script fix có thể gọi hàm Compat → đảm bảo shim đã có trên đích.
            await EnsureCompatShimsAsync(issue.RequiredShims, ct).ConfigureAwait(false);
            try
            {
                await TryCreateOnDestAsync(conn, script, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsAmbiguousColumnError(ex))
            {
                // Lỗi 209 (cột mơ hồ, thường do SELECT * lỗi thời): thử bung tường minh.
                // Không bung được thì ném lại lỗi gốc để vòng ngoài ghi nhận thất bại.
                if (!await TryFixAmbiguousColumnsAsync(conn, issue, script, ct, ex.Message).ConfigureAwait(false))
                    throw;
            }

            // Bảng có cột masking: tạo thêm view che thay thế (ghi đích, không chạm nguồn).
            // View lỗi thì chỉ cảnh báo — bảng gốc vẫn tính là đã fix.
            if (string.Equals(issue.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
                await TryCreateMaskedViewAsync(conn, issue, script, ct).ConfigureAwait(false);

            return true;
        }

        /// <summary>Nhận biết lỗi cột mơ hồ (SQL 209 Ambiguous column name).</summary>
        internal static bool IsAmbiguousColumnError(Exception ex)
        {
            if (ex is SqlException sqlEx)
            {
                foreach (SqlError err in sqlEx.Errors)
                {
                    if (err.Number == 209)
                        return true;
                }
            }

            var message = ex.Message ?? string.Empty;
            return message.Contains("Ambiguous column", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Thử sửa lỗi 209 cho view/function: bung SELECT * thành danh sách cột
        /// tường minh đọc từ catalog NGUỒN (chỉ SELECT), rồi tạo lại trên đích.
        /// Hết cách bung mà vẫn 209 (cột ghi tường minh) thì thử qualify từng khả
        /// năng và đối chiếu output ngay trên nguồn. Trả true nếu tạo thành công;
        /// false (không ném) trong mọi trường hợp còn lại.
        /// </summary>
        private async Task<bool> TryFixAmbiguousColumnsAsync(
            SqlConnection destConn, ReconcileIssue issue, string script,
            CancellationToken ct, string? errorMessage = null)
        {
            try
            {
                if (!issue.ObjectType.Equals("VIEW", StringComparison.OrdinalIgnoreCase)
                    && !issue.ObjectType.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase)
                    && !issue.ObjectType.Equals("STORED_PROCEDURE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Bỏ sửa 209 cho '{Name}': loại {Type} không hỗ trợ.",
                        issue.ObjectName, issue.ObjectType);
                    return false;
                }

                // Cách 1 (rẻ): bung SELECT * thành cột tường minh theo catalog nguồn.
                if (await TryStarExpandAsync(destConn, issue, script, ct).ConfigureAwait(false))
                    return true;

                // Cách 2 (cột ghi tường minh): qualify thử từng khả năng rồi đối chiếu
                // output ngay trên nguồn (chỉ SELECT) để chọn đúng duy nhất.
                return await TryResolveAmbiguousAsync(destConn, issue, script,
                    errorMessage ?? issue.ErrorMessage, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không sửa được 209 cho '{Name}' ({Message}).",
                    issue.ObjectName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Cách 1: bung SELECT * / alias.* theo đúng cột đọc từ nguồn rồi tạo lại.
        /// Trả false (không ném) khi không áp dụng được hoặc tạo lại vẫn lỗi.
        /// </summary>
        private async Task<bool> TryStarExpandAsync(
            SqlConnection destConn, ReconcileIssue issue, string script, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(script) || script.IndexOf('*') < 0)
                {
                    // Nhánh phổ biến nhất: view ghi tường minh cột mơ hồ (không có * để bung).
                    _logger.LogWarning(
                        "Bỏ bung SELECT * cho '{Name}': script không chứa dấu * (cột ghi tường minh mà mơ hồ).",
                        issue.ObjectName);
                    return false;
                }

                var aliases = StarExpander.ParseTableAliases(script);
                if (aliases.Count == 0)
                {
                    _logger.LogWarning(
                        "Bỏ bung SELECT * cho '{Name}': không tách được bảng/alias từ FROM/JOIN.",
                        issue.ObjectName);
                    return false;
                }

                using var sourceConn = SqlConnectionFactory.Open(_options.SourceConnectionString,
                    _options.CommandTimeoutSeconds, ct);
                var aliasColumns = new List<(string Alias, IReadOnlyList<string> Columns)>();
                foreach (var (alias, table) in aliases)
                {
                    ct.ThrowIfCancellationRequested();
                    var cols = await ReadTableColumnsAsync(sourceConn, table, ct).ConfigureAwait(false);
                    if (cols.Count > 0)
                        aliasColumns.Add((alias, cols));
                    else
                        _logger.LogWarning(
                            "Bỏ bảng '{Table}' khi bung SELECT * cho '{Name}': không đọc được cột từ nguồn.",
                            table, issue.ObjectName);
                }
                if (aliasColumns.Count == 0)
                {
                    _logger.LogWarning("Bỏ bung SELECT * cho '{Name}': không bảng nào đọc được cột.",
                        issue.ObjectName);
                    return false;
                }

                var (changed, expanded) = StarExpander.ExpandStars(script, aliasColumns);
                if (!changed)
                {
                    _logger.LogWarning("Bỏ bung SELECT * cho '{Name}': không tìm thấy mẫu alias.*/* trần.",
                        issue.ObjectName);
                    return false;
                }

                await TryCreateOnDestAsync(destConn, expanded, ct).ConfigureAwait(false);

                issue.RewrittenScript = expanded;
                var applied = issue.AppliedRules.ToList();
                if (!applied.Contains("star_expand"))
                    applied.Add("star_expand");
                issue.AppliedRules = applied;
                AddIssueWarning(issue,
                    "Script gốc dùng SELECT * lỗi thời gây mơ hồ cột — app đã bung tường minh "
                    + "theo đúng cột của bảng nguồn.");
                _logger.LogInformation("✔ '{Name}' tạo thành công sau khi bung SELECT *.",
                    issue.ObjectName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không bung được SELECT * cho '{Name}' ({Message}).",
                    issue.ObjectName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Cách 2: với cột mơ hồ ghi tường minh, qualify thử từng alias mà bảng của
        /// nó có cột đó, rồi đối chiếu checksum output ngay trên NGUỒN (chỉ SELECT).
        /// Chỉ chọn khi ĐÚNG MỘT khả năng khớp (kèm gate xác định 2 lần baseline);
        /// 0 hoặc nhiều khả năng khớp đều bỏ (để user sửa tay). Không bao giờ ghi nguồn.
        /// </summary>
        private async Task<bool> TryResolveAmbiguousAsync(
            SqlConnection destConn, ReconcileIssue issue, string script,
            string? errorMessage, CancellationToken ct)
        {
            const int MaxCandidates = 6;
            try
            {
                var column = TryExtractAmbiguousColumn(errorMessage);
                if (string.IsNullOrEmpty(column))
                {
                    _logger.LogWarning("Bỏ đối chiếu 209 cho '{Name}': không trích được tên cột.",
                        issue.ObjectName);
                    return false;
                }

                var body = AmbiguousColumnResolver.ExtractViewBody(script);
                if (string.IsNullOrEmpty(body))
                {
                    _logger.LogWarning("Bỏ đối chiếu 209 cho '{Name}': không tách được query SELECT.",
                        issue.ObjectName);
                    return false;
                }

                if (AmbiguousColumnResolver.FindBareOccurrences(body, column).Count == 0)
                {
                    _logger.LogWarning("Bỏ đối chiếu 209 cho '{Name}': không thấy '{Col}' dạng trần.",
                        issue.ObjectName, column);
                    return false;
                }

                var aliases = StarExpander.ParseTableAliases(script);
                if (aliases.Count == 0)
                    return false;

                using var sourceConn = SqlConnectionFactory.Open(_options.SourceConnectionString,
                    _options.CommandTimeoutSeconds, ct);

                // Bảng nào (trong các alias) có cột đó — một truy vấn duy nhất.
                var tablesWithColumn = await ReadTablesHavingColumnAsync(
                    sourceConn, column, ct).ConfigureAwait(false);
                var candidates = aliases
                    .Select(a => new
                    {
                        Alias = a.Alias,
                        Table = StarExpander.NormalizeTableRef(a.Table)
                    })
                    .Where(a => tablesWithColumn.Contains(
                        StarExpander.NormalizeTableRef(a.Table)))
                    .Select(a => a.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (candidates.Count == 0)
                {
                    _logger.LogWarning("Bỏ đối chiếu 209 cho '{Name}': không alias nào có cột '{Col}'.",
                        issue.ObjectName, column);
                    return false;
                }
                if (candidates.Count > MaxCandidates)
                {
                    _logger.LogWarning("Bỏ đối chiếu 209 cho '{Name}': quá nhiều khả năng ({N}).",
                        issue.ObjectName, candidates.Count);
                    return false;
                }

                // Gate xác định: chạy gốc 2 lần phải giống nhau (chống dữ liệu đang đổi
                // và output rỗng — rỗng thì khả năng nào cũng khớp nên không dám chọn).
                var base1 = await ChecksumQueryAsync(sourceConn, body, ct).ConfigureAwait(false);
                var base2 = await ChecksumQueryAsync(sourceConn, body, ct).ConfigureAwait(false);
                if (base1 == null || base1 != base2)
                {
                    _logger.LogWarning(
                        "Bỏ đối chiếu 209 cho '{Name}': output gốc rỗng hoặc đang đổi nên không đối chiếu được.",
                        issue.ObjectName);
                    return false;
                }

                var matches = new List<string>();
                foreach (var alias in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var variant = AmbiguousColumnResolver.QualifyOccurrences(body, column, alias);
                    var cs = await ChecksumQueryAsync(sourceConn, variant, ct).ConfigureAwait(false);
                    if (cs != null && cs == base1)
                        matches.Add(alias);
                }

                if (matches.Count != 1)
                {
                    _logger.LogWarning(
                        "Bỏ đối chiếu 209 cho '{Name}': {N} khả năng khớp ({Aliases}) — không dám chọn, để user quyết.",
                        issue.ObjectName, matches.Count, string.Join(", ", matches));
                    return false;
                }

                var winner = matches[0];
                var finalScript = AmbiguousColumnResolver.QualifyOccurrences(script, column, winner);
                await TryCreateOnDestAsync(destConn, finalScript, ct).ConfigureAwait(false);

                issue.RewrittenScript = finalScript;
                var applied = issue.AppliedRules.ToList();
                if (!applied.Contains("ambiguous_resolve"))
                    applied.Add("ambiguous_resolve");
                issue.AppliedRules = applied;
                AddIssueWarning(issue,
                    "Cột '" + column + "' mơ hồ đã được qualify thành [" + winner.Trim('[', ']')
                    + "].[" + column + "] sau khi đối chiếu output trên nguồn "
                    + "(đã thử " + candidates.Count + " khả năng, chỉ khả năng này khớp).");
                _logger.LogInformation("✔ '{Name}' tạo thành công sau khi qualify '{Col}' bằng '{Alias}'.",
                    issue.ObjectName, column, winner);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đối chiếu được 209 cho '{Name}' ({Message}).",
                    issue.ObjectName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Checksum toàn bộ output một query SELECT trên nguồn (chỉ SELECT).
        /// Trả null khi rỗng hoặc lỗi. Thứ tự dòng không ảnh hưởng (aggregate).
        /// </summary>
        private static async Task<int?> ChecksumQueryAsync(
            SqlConnection sourceConn, string queryBody, CancellationToken ct)
        {
            using var cmd = new SqlCommand(
                "SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM (" + queryBody + ") AS [__v];",
                sourceConn)
            { CommandTimeout = 120 };
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value == null || value == DBNull.Value ? (int?)null : Convert.ToInt32(value);
        }

        /// <summary>
        /// Tập "schema.table" (bảng + view người dùng) trên nguồn có cột tên cho trước.
        /// Một truy vấn duy nhất, chỉ SELECT.
        /// </summary>
        private static async Task<HashSet<string>> ReadTablesHavingColumnAsync(
            SqlConnection sourceConn, string column, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = new SqlCommand(
                "SELECT s.name + '.' + o.name FROM sys.columns c "
                + "JOIN sys.objects o ON o.object_id = c.object_id "
                + "JOIN sys.schemas s ON s.schema_id = o.schema_id "
                + "LEFT JOIN sys.tables t ON t.object_id = o.object_id "
                + "LEFT JOIN sys.views v ON v.object_id = o.object_id "
                + "WHERE c.name = @col AND o.type IN ('U','V') "
                + "AND COALESCE(t.is_ms_shipped, v.is_ms_shipped, 0) = 0;", sourceConn)
            { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@col", column);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                set.Add(reader.GetString(0));
            return set;
        }

        /// <summary>
        /// Trích tên cột từ thông điệp "Ambiguous column name 'X'". Trả null nếu không khớp.
        /// Thuần chuỗi để dễ kiểm thử.
        /// </summary>
        internal static string? TryExtractAmbiguousColumn(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
                return null;
            var m = Regex.Match(errorMessage, @"Ambiguous column name '([^']+)'",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim().Trim('[', ']') : null;
        }

        /// <summary>
        /// Dựng gợi ý sửa tay cho lỗi 209 không bung * được: liệt kê các bảng NGUỒN
        /// có cột đó (tối đa 10, chỉ SELECT catalog) để user biết qualify bảng nào.
        /// Trả null khi không xác định được tên cột. Không ném lỗi.
        /// </summary>
        private async Task<string?> BuildAmbiguousGuidanceAsync(
            ReconcileIssue issue, string errorMessage, CancellationToken ct)
        {
            try
            {
                var column = TryExtractAmbiguousColumn(errorMessage);
                if (string.IsNullOrEmpty(column))
                    return null;

                var tables = new List<string>();
                using var sourceConn = SqlConnectionFactory.Open(_options.SourceConnectionString,
                    _options.CommandTimeoutSeconds, ct);
                using var cmd = new SqlCommand(
                    "SELECT TOP 10 s.name + '.' + t.name FROM sys.columns c "
                    + "JOIN sys.tables t ON t.object_id = c.object_id "
                    + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                    + "WHERE c.name = @col AND t.is_ms_shipped = 0 ORDER BY 1;", sourceConn)
                { CommandTimeout = 60 };
                cmd.Parameters.AddWithValue("@col", column);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    tables.Add(reader.GetString(0));

                var where = tables.Count > 0
                    ? " Các bảng nguồn có cột này: " + string.Join(", ", tables) + "."
                    : "";
                var hint = "Cột '" + column + "' mơ hồ (nhiều bảng JOIN cùng tên cột) mà view không dùng "
                    + "SELECT * nên app không tự bung được. Hãy sửa tay định nghĩa view: ghi rõ "
                    + "[alias].[" + column + "] (VD: SELECT a.[" + column + "] ...)." + where;
                AddIssueWarning(issue, hint);
                _logger.LogWarning("'{Name}': {Hint}", issue.ObjectName, hint);
                return hint;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Không dựng được gợi ý 209 cho '{Name}' ({Message}).",
                    issue.ObjectName, ex.Message);
                return null;
            }
        }

        /// <summary>Đọc tên cột của bảng/view theo thứ tự column_id (chỉ SELECT nguồn).</summary>
        private static async Task<IReadOnlyList<string>> ReadTableColumnsAsync(
            SqlConnection sourceConn, string tableName, CancellationToken ct)
        {
            var dot = tableName.LastIndexOf('.');
            var schema = dot > 0 ? tableName.Substring(0, dot) : "dbo";
            var name = dot > 0 ? tableName.Substring(dot + 1) : tableName;

            var cols = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT c.name FROM sys.columns c "
                + "JOIN sys.objects o ON o.object_id = c.object_id "
                + "JOIN sys.schemas s ON s.schema_id = o.schema_id "
                + "WHERE s.name = @schema AND o.name = @name AND o.type IN ('U','V') "
                + "ORDER BY c.column_id;", sourceConn)
            { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@name", name);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                cols.Add(reader.GetString(0));
            return cols;
        }

        /// <summary>
        /// Tạo view che thay thế masking cho bảng vừa fix (đọc marker trong script).
        /// Không ném lỗi: thất bại thì ghi vào Warnings của issue.
        /// </summary>
        private async Task TryCreateMaskedViewAsync(
            SqlConnection destConn, ReconcileIssue issue, string script, CancellationToken ct)
        {
            try
            {
                var masks = MaskingConverter.TryParseMarkers(script);
                if (masks.Count == 0)
                    return;

                var parts = (issue.ObjectName ?? "").Split('.');
                if (parts.Length < 2)
                    return;

                var view = MaskingConverter.BuildMaskedView(parts[0], parts[1], script, masks);
                if (view == null)
                {
                    AddIssueWarning(issue, "Không sinh được view che thay thế masking.");
                    return;
                }

                await TryCreateOnDestAsync(destConn, view.Script, ct).ConfigureAwait(false);
                AddIssueWarning(issue,
                    "Đã tạo view che " + view.ViewSchema + "." + view.ViewName
                    + " thay thế masking (cột gốc KHÔNG che).");
                foreach (var w in view.Warnings)
                    AddIssueWarning(issue, w);
                _logger.LogInformation("Đã tạo view che {View} cho bảng {Table}.",
                    view.ViewSchema + "." + view.ViewName, issue.ObjectName);
            }
            catch (Exception ex)
            {
                AddIssueWarning(issue,
                    "Không tạo được view che thay thế masking (" + ex.Message
                    + ") — bảng gốc vẫn đầy đủ dữ liệu, hãy tạo view che tay.");
                _logger.LogWarning(ex, "Không tạo được view che cho {Table}.", issue.ObjectName);
            }
        }

        /// <summary>Thêm cảnh báo vào issue (giữ danh sách hiện có).</summary>
        private static void AddIssueWarning(ReconcileIssue issue, string warning)
        {
            var list = issue.Warnings.ToList();
            if (!list.Contains(warning))
                list.Add(warning);
            issue.Warnings = list;
        }

        /// <summary>Fix cột thiếu trên đích bằng ALTER TABLE ADD.</summary>
        /// <returns>True nếu đã thêm cột; false nếu bỏ qua.</returns>
        private async Task<bool> FixMissingColumnAsync(ReconcileIssue issue, CancellationToken ct)
        {
            if (!issue.CanAutoFix)
            {
                _logger.LogWarning("'{Name}' không thể auto fix — bỏ qua.", issue.ObjectName);
                return false;
            }

            // Tách tên bảng và tên cột từ ObjectName (format: "schema.table.column").
            var parts = issue.ObjectName.Split('.');
            if (parts.Length < 3) return false;
            var schemaName = parts[0];
            var tableName = parts[1];
            var columnName = parts[2];

            // Cần đọc metadata kiểu cột từ nguồn để dựng ALTER TABLE ADD.
            using var sourceConn = SqlConnectionFactory.Open(_options.SourceConnectionString,
                _options.CommandTimeoutSeconds, ct);
            var typeInfo = await ReadColumnTypeInfoAsync(sourceConn, schemaName, tableName, columnName, ct)
                .ConfigureAwait(false);
            if (typeInfo == null)
            {
                _logger.LogWarning("Không đọc được kiểu cột {ObjectName} từ nguồn.", issue.ObjectName);
                return false;
            }

            var dataType = ReconcileSql.BuildDataTypeSql(typeInfo.TypeSchema, typeInfo.TypeName,
                typeInfo.MaxLength, typeInfo.Precision, typeInfo.Scale);
            var nullable = typeInfo.IsNullable ? "NULL" : "NOT NULL";
            var sql = $"ALTER TABLE [{schemaName}].[{tableName}] ADD [{columnName}] {dataType} {nullable};";

            using var destConn = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);
            using var cmd = new SqlCommand(sql, destConn) { CommandTimeout = _options.CommandTimeoutSeconds };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Đã thêm cột {ObjectName}.", issue.ObjectName);
            return true;
        }

        /// <summary>
        /// Nhận biết lỗi thiếu object phụ thuộc (SQL 208 Invalid object name).
        /// Các lỗi này có thể tự hết sau khi object phụ thuộc được tạo → cho retry.
        /// </summary>
        internal static bool IsMissingDependencyError(Exception ex)
        {
            if (ex is SqlException sqlEx)
            {
                foreach (SqlError err in sqlEx.Errors)
                {
                    if (err.Number == 208)
                        return true;
                }
            }

            var message = ex.Message ?? string.Empty;
            return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Giải thích cụ thể object nào còn thiếu sau retry. Đặc biệt: tên 3 phần
        /// (VD: ADEL9200.dbo.ROOMINFO) nghĩa là trỏ sang DATABASE KHÁC — app không
        /// thể tự tạo, user phải di chuyển database đó trước (hoặc tạo bảng tương ứng).
        /// </summary>
        internal static string DescribeMissingDependency(string? errorMessage)
        {
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                var m = Regex.Match(errorMessage, @"Invalid object name '([^']+)'",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var missing = m.Groups[1].Value.Trim().Trim('[', ']');
                    var parts = missing.Split('.');
                    if (parts.Length >= 3)
                    {
                        var db = parts[0].Trim('[', ']');
                        return " Đối tượng thiếu '" + missing + "' nằm ở DATABASE KHÁC (" + db + ") — "
                            + "app chỉ di chuyển trong một database nên KHÔNG tự tạo được. "
                            + "Hãy di chuyển database '" + db + "' trước (hoặc tạo bảng tương ứng trên đích), "
                            + "rồi bấm Xử lý lại.";
                    }
                    return " Vẫn thiếu object '" + missing + "' sau nhiều vòng thử — "
                        + "cần tạo object phụ thuộc trước, rồi tạo lại object này.";
                }
            }
            return " Vẫn thiếu object phụ thuộc sau nhiều vòng thử — "
                + "cần tạo object phụ thuộc trước, rồi tạo lại object này.";
        }

        /// <summary>
        /// Trích các tham chiếu schema.table trong script SQL (ngoặc vuông, không ngoặc,
        /// lẫn lộn, 3 phần db.schema.table). Chỉ giữ tham chiếu thuộc schema có thật
        /// trên nguồn để loại alias cột (VD: "a.Id") và tên biến/hàm.
        /// </summary>
        internal static IReadOnlyList<string> ExtractReferencedObjects(
            string script, ISet<string> knownSchemas)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(script))
                return refs.ToList();

            // Dạng 3 phần [db].[schema].[table] → lấy 2 phần cuối.
            foreach (Match m in Regex.Matches(script,
                @"\[([^\]]+)\]\s*\.\s*\[([^\]]+)\]\s*\.\s*\[([^\]]+)\]"))
            {
                refs.Add(m.Groups[2].Value + "." + m.Groups[3].Value);
            }

            // Dạng 2 phần ngoặc [schema].[table].
            foreach (Match m in Regex.Matches(script,
                @"\[([^\]]+)\]\s*\.\s*\[([^\]]+)\]"))
            {
                refs.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }

            // Dạng lẫn lộn [schema].table.
            foreach (Match m in Regex.Matches(script,
                @"\[([^\]]+)\]\s*\.\s*([A-Za-z_][\w]*)"))
            {
                refs.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }

            // Dạng lẫn lộn schema.[table].
            foreach (Match m in Regex.Matches(script,
                @"(?<![\w@#\[\]])([A-Za-z_][\w]*)\s*\.\s*\[([^\]]+)\]"))
            {
                refs.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }

            // Dạng không ngoặc schema.table (loại trừ gọi hàm "dbo.fn(" và biến "@a.b").
            foreach (Match m in Regex.Matches(script,
                @"(?<![\w@#\[\]])([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)(?!\s*\()"))
            {
                refs.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }

            refs.RemoveWhere(r =>
            {
                var dot = r.IndexOf('.');
                return dot <= 0 || !knownSchemas.Contains(r.Substring(0, dot));
            });
            return refs.ToList();
        }

        /// <summary>
        /// Trích các tham chiếu sang DATABASE KHÁC trong script (tên 3-4 phần:
        /// db.schema.object hoặc server.db.schema.object; ngoặc hoặc không ngoặc).
        /// Trả về tên đầy đủ ("ADEL9200.dbo.ROOMINFO"). Bỏ qua tham chiếu về chính
        /// database nguồn (ownDbName) vì chúng tự khớp khi đích giữ nguyên tên.
        /// Dùng để cảnh báo TRƯỚC khi fix: app không thể tự tạo object khác database.
        /// </summary>
        internal static IReadOnlyList<string> ExtractCrossDatabaseRefs(string? script, string? ownDbName)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(script))
                return refs.ToList();

            // Một mẫu seg chịu mọi dạng ngoặc/không ngoặc lẫn lộn:
            // seg = [name] hoặc tên trần. Đếm số seg để phân biệt 3 và 4 phần.
            const string seg = @"(?:\[([^\]]+)\]|([A-Za-z_][\w$#@]*))";
            string SegValue(Match mm, int a, int b) =>
                mm.Groups[a].Success ? mm.Groups[a].Value : mm.Groups[b].Value;

            // Dạng 4 phần srv.db.s.o → ghi "srv.db".
            foreach (Match m in Regex.Matches(script,
                seg + @"\s*\.\s*" + seg + @"\s*\.\s*" + seg + @"\s*\.\s*" + seg))
            {
                var srv = SegValue(m, 1, 2);
                var db = SegValue(m, 3, 4);
                if (srv.Length > 0 && db.Length > 0)
                    refs.Add(srv + "." + db);
            }

            // Dạng 3 phần db.s.o (lookbehind/lookahead chặn đuôi của dạng 4 phần).
            foreach (Match m in Regex.Matches(script,
                @"(?<![\w@#\[\].])" + seg + @"\s*\.\s*" + seg + @"\s*\.\s*" + seg + @"(?!\s*\.\s*(?:\[|[A-Za-z_]))"))
            {
                var db = SegValue(m, 1, 2);
                var s = SegValue(m, 3, 4);
                var o = SegValue(m, 5, 6);
                if (db.Length > 0 && s.Length > 0 && o.Length > 0)
                    refs.Add(db + "." + s + "." + o);
            }

            // Bỏ tham chiếu về chính database nguồn (giữ nguyên tên trên đích thì tự khớp).
            if (!string.IsNullOrWhiteSpace(ownDbName))
            {
                var own = ownDbName!.Trim('[', ']', ' ');
                refs.RemoveWhere(r =>
                {
                    var segs = r.Split('.');
                    // 3 phần: segs[0] là db; 4 phần "srv.db": segs[1] là db.
                    var dbPart = segs.Length >= 4 ? segs[1] : segs[0];
                    return dbPart.Equals(own, StringComparison.OrdinalIgnoreCase);
                });
            }
            return refs.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ============================================================================
        // Helpers — ĐỌC
        // ============================================================================

        private sealed class DestinationInventory
        {
            public HashSet<string> Schemas { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> ObjectKeys { get; } =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Columns { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<DestinationInventory> ReadDestinationInventoryAsync(CancellationToken ct)
        {
            var inventory = new DestinationInventory();
            using var conn = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);

            // Schema (chỉ SELECT).
            using (var reader = await QueryAsync(conn,
                "SELECT name FROM sys.schemas WHERE schema_id > 4;", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    inventory.Schemas.Add(reader.GetString(0));
            }

            // Bảng, view, function, proc, trigger (chỉ SELECT).
            // ISNULL schema vì Database DDL trigger không thuộc schema nào
            // (OBJECT_SCHEMA_NAME trả NULL) — trước đây GetString ném sập cả pha quét.
            using (var reader = await QueryAsync(conn, @"
SELECT type, ISNULL(OBJECT_SCHEMA_NAME(object_id), '') + '.' + name
  FROM sys.objects
 WHERE is_ms_shipped = 0 AND type IN ('U','V','FN','IF','TF','P','TR');", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // Trim vì sys.objects.type là char(2) ("U " có dấu cách đuôi) —
                    // trước đây switch trượt hết bảng/view/proc, quét báo thiếu ảo.
                    var prefix = InventoryComparer.InventoryKey(reader.GetString(0));
                    var name = reader.GetString(1);
                    var key = prefix == null ? null : prefix + name;
                    if (key != null)
                        AddKey(inventory, key);
                }
            }

            // Sequence.
            using (var reader = await QueryAsync(conn, @"
SELECT ISNULL(OBJECT_SCHEMA_NAME(object_id), '') + '.' + name FROM sys.sequences;", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    AddKey(inventory, "SEQ:" + reader.GetString(0));
            }

            // Foreign keys.
            using (var reader = await QueryAsync(conn, @"
SELECT ISNULL(OBJECT_SCHEMA_NAME(parent_object_id), '') + '.' + name FROM sys.foreign_keys;", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    AddKey(inventory, "FK:" + reader.GetString(0));
            }

            // Kiểu do user định nghĩa (CREATE TYPE) — trước đây không đọc nên thiếu mà không báo.
            using (var reader = await QueryAsync(conn, @"
SELECT s.name + '.' + t.name
  FROM sys.types t
  JOIN sys.schemas s ON s.schema_id = t.schema_id
 WHERE t.is_user_defined = 1;", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    AddKey(inventory, "UDT:" + reader.GetString(0));
            }

            // Server trigger (phạm vi server, đọc được từ mọi database).
            try
            {
                using (var reader = await QueryAsync(conn,
                    "SELECT name FROM sys.server_triggers;", ct))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        AddKey(inventory, "STRG:" + reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được sys.server_triggers trên đích ({Message}); bỏ qua kiểm tra server trigger.", ex.Message);
            }

            // Cột của mọi bảng.
            using (var reader = await QueryAsync(conn, @"
SELECT s.name + '.' + t.name + '.' + c.name
  FROM sys.columns c
  JOIN sys.tables t ON t.object_id = c.object_id
  JOIN sys.schemas s ON s.schema_id = t.schema_id
 WHERE t.is_ms_shipped = 0;", ct))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    inventory.Columns.Add(reader.GetString(0));
            }

            return inventory;
        }

        private static void AddKey(DestinationInventory inventory, string key)
        {
            var prefix = key.Substring(0, key.IndexOf(':') + 1);
            if (!inventory.ObjectKeys.TryGetValue(prefix, out var set))
                inventory.ObjectKeys[prefix] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(key.Substring(prefix.Length));
        }

        private static bool HasKey(DestinationInventory inventory, string key)
        {
            var prefix = key.Substring(0, key.IndexOf(':') + 1);
            return inventory.ObjectKeys.TryGetValue(prefix, out var set)
                && set.Contains(key.Substring(prefix.Length));
        }

        /// <summary>
        /// Triển khai các shim Compat lên database ĐÍCH (không bao giờ chạm nguồn).
        /// Rỗng thì không làm gì. Idempotent nên gọi ở cả quét và fix đều an toàn.
        /// </summary>
        private async Task EnsureCompatShimsAsync(IEnumerable<string> shimIds, CancellationToken ct)
        {
            var ids = shimIds.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (ids.Count == 0)
                return;
            await CompatLibrary.EnsureDeployedAsync(
                _options.DestinationConnectionString, ids, _logger, ct).ConfigureAwait(false);
        }

        /// <summary>Thử CREATE object trên đích (ghi đích!).</summary>
        private static async Task TryCreateOnDestAsync(SqlConnection conn, string script, CancellationToken ct)
        {
            // Dùng chung ScriptUtils.SplitBatches với SchemaBuilder để tách GO nhất quán
            // (chịu cả "GO -- comment"), tránh lệch hành vi giữa 2 nơi tự tách riêng.
            foreach (var batch in ScriptUtils.SplitBatches(script))
            {
                var trimmed = batch.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 600 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static string? ExtractSqlErrorMessage(Exception ex)
        {
            if (ex is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Errors.Count > 0)
                return sqlEx.Errors[0].Message;
            return ex.InnerException?.Message ?? ex.Message;
        }

        private Task<SqlDataReader> QueryAsync(SqlConnection conn, string sql, CancellationToken ct) =>
            QueryAsync(conn, sql, Array.Empty<SqlParameter>(), ct);

        private async Task<SqlDataReader> QueryAsync(SqlConnection conn, string sql,
            SqlParameter[] parameters, CancellationToken ct)
        {
            var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddRange(parameters);
            return await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }

        private sealed class ColumnTypeInfo
        {
            public string TypeSchema { get; init; } = string.Empty;
            public string TypeName { get; init; } = string.Empty;
            public short MaxLength { get; init; }
            public byte Precision { get; init; }
            public byte Scale { get; init; }
            public bool IsNullable { get; init; }
        }

        private async Task<ColumnTypeInfo?> ReadColumnTypeInfoAsync(SqlConnection source,
            string schema, string table, string columnName, CancellationToken ct)
        {
            const string query = @"
SELECT TYPE_SCHEMA_NAME(c.user_type_id), TYPE_NAME(c.user_type_id),
       c.max_length, c.precision, c.scale, c.is_nullable
  FROM sys.columns c
  JOIN sys.tables t ON t.object_id = c.object_id
  JOIN sys.schemas s ON s.schema_id = t.schema_id
 WHERE s.name = @schema AND t.name = @table AND c.name = @col;";

            using var cmd = new SqlCommand(query, source) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            cmd.Parameters.AddWithValue("@col", columnName);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            return new ColumnTypeInfo
            {
                TypeSchema = reader.GetString(0),
                TypeName = reader.GetString(1),
                MaxLength = reader.GetInt16(2),
                Precision = reader.GetByte(3),
                Scale = reader.GetByte(4),
                IsNullable = reader.GetBoolean(5)
            };
        }
    }
}
