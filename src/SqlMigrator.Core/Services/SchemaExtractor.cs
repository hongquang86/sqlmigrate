using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Captures the full schema of the source database using SMO. Produces two ordered
    /// collections: <see cref="SchemaModel.Objects"/> (run first) and
    /// <see cref="SchemaModel.DeferredObjects"/> (foreign keys, created after all tables).
    /// </summary>
    public sealed class SchemaExtractor : ISchemaExtractor
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public SchemaExtractor(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        public async Task<SchemaModel> ExtractAsync(CancellationToken ct = default)
        {
            var builder = new SqlConnectionStringBuilder(_options.SourceConnectionString);
            var dbName = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(dbName))
                throw new InvalidOperationException(
                    "The source connection string must include an Initial Catalog (Database).");

            var server = await Task.Run(() => ConnectServer(_options.SourceConnectionString), ct).ConfigureAwait(false);
            var db = server.Databases[dbName];
            if (db == null)
                throw new InvalidOperationException($"Source database '{dbName}' was not found on the server.");

            _logger.LogInformation("Extracting schema from '{Db}' (version {Ver}).", dbName, server.VersionString);

            var objects = new List<DatabaseObject>();
            var deferred = new List<DatabaseObject>();
            var tables = new List<TableSchema>();
            var warnings = new List<CompatibilityIssue>();
            var includedTables = BuildTableFilter();

            db.Refresh();
            RunCollector("schema", () => CollectSchemas(db, objects, warnings), warnings);
            RunCollector("user-defined types", () => CollectUserDefinedTypes(db, objects, warnings), warnings);
            RunCollector("sequences", () => CollectSequences(db, objects, warnings), warnings);
            RunCollector("tables", () => CollectTables(db, objects, tables, warnings, includedTables), warnings);
            RunCollector("views/functions/procedures", () => CollectViewsModules(db, objects, warnings), warnings);
            RunCollector("foreign keys", () => CollectForeignKeys(db, deferred), warnings);
            RunCollector("database-level triggers/extended objects", () => CollectExtendedObjects(db, objects, warnings), warnings);
            RunCollector("server-level triggers", () => CollectServerTriggers(server, objects, warnings), warnings);

            if (_options.CopyPermissions)
                await RunCollectorAsync("permissions",
                    async token =>
                    {
                        await CollectPermissionsAsync(builder.ConnectionString, objects, warnings, token).ConfigureAwait(false);
                        return true;
                    }, warnings, ct).ConfigureAwait(false);

            var schemaModel = new SchemaModel
            {
                Objects = ApplyObjectFilters(objects).ToList(),
                DeferredObjects = deferred,
                Tables = tables.Where(t => !t.IsSkipped).ToList(),
                Warnings = warnings,
                SourceVersion = server.VersionString ?? "unknown",
                DatabaseCollation = db.Collation ?? string.Empty,
                DatabaseName = dbName
            };

            _logger.LogInformation(
                "Extracted {ObjCount} object batches and {TableCount} tables with {WarningCount} warnings.",
                schemaModel.ObjectCount, tables.Count, warnings.Count);

            return schemaModel;
        }

        /// <summary>Chạy một collector schema; nếu thất bại chỉ ghi cảnh báo và tiếp tục để không hủy toàn bộ danh sách đối tượng.</summary>
        private void RunCollector(string group, Action collect, ICollection<CompatibilityIssue> warnings)
        {
            try
            {
                collect();
            }
            catch (Exception ex)
            {
                var message = $"Không trích xuất được nhóm '{group}': {ex.Message}";
                _logger.LogWarning("Nhóm '{Group}' không trích xuất được: {Message}", group, ex.Message);
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = group,
                    Feature = "schema",
                    Message = message
                });
            }
        }

        /// <summary>Bản async của <see cref="RunCollector"/> cho nhóm dùng truy vấn ADO.NET.</summary>
        private async Task RunCollectorAsync(string group, Func<CancellationToken, Task<bool>> collect,
            ICollection<CompatibilityIssue> warnings, CancellationToken ct)
        {
            try
            {
                await collect(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message = $"Không trích xuất được nhóm '{group}': {ex.Message}";
                _logger.LogWarning("Nhóm '{Group}' không trích xuất được: {Message}", group, ex.Message);
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = group,
                    Feature = "schema",
                    Message = message
                });
            }
        }

        private Server ConnectServer(string connectionString)
        {
            // ServerConnection tự parse chuỗi kết nối theo cách riêng và vấp các từ khóa
            // hiện đại (Encrypt/TrustServerCertificate). Thay vào đó dựng trực tiếp từ các
            // thuộc tính để SMO tôn trọng chứng chỉ tự ký / TLS cũ của SQL 2008 → 2025.
            var builder = new SqlConnectionStringBuilder(connectionString);
            var useLogin = !builder.IntegratedSecurity;
            var trustUser = builder.TrustServerCertificate;
            var encryptUser = !builder.Encrypt.Equals(SqlConnectionEncryptOption.Optional);

            // Thứ tự thử: giữ cấu hình người dùng → bật tin cậy chứng chỉ → tắt mã hóa (mạng nội bộ).
            var attempts = new (bool encrypt, bool trust)[]
            {
                (encryptUser, trustUser),
                (encryptUser, true),
                (false, true)
            };

            Exception? last = null;
            foreach (var (encrypt, trust) in attempts)
            {
                try
                {
                    var conn = new ServerConnection
                    {
                        ServerInstance = builder.DataSource,
                        LoginSecure = builder.IntegratedSecurity,
                        DatabaseName = builder.InitialCatalog,
                        EncryptConnection = encrypt,
                        TrustServerCertificate = trust,
                        StatementTimeout = Math.Max(_options.CommandTimeoutSeconds, 0),
                        ApplicationName = "SqlMigrator"
                    };
                    if (useLogin)
                    {
                        conn.Login = builder.UserID;
                        conn.Password = builder.Password;
                    }

                    var server = new Server(conn);
                    server.ConnectionContext.Connect();
                    server.ConnectionContext.Disconnect();
                    return server;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            // Không bao giờ để thông điệp chứa chuỗi kết nối / mật khẩu lọt ra ngoài.
            var baseMessage = last?.GetBaseException().Message ?? "lỗi không xác định";
            var safeMessage = SqlConnectionFactory.ScrubConnectionStringSecrets(baseMessage);
            throw new InvalidOperationException("Không kết nối được server nguồn: " + safeMessage);
        }

        private static ScriptingOptions BuildTableOptions()
        {
            return new ScriptingOptions
            {
                ScriptDrops = false,
                IncludeIfNotExists = false,
                SchemaQualify = true,
                ScriptBatchTerminator = false,
                NoCommandTerminator = false,
                DriAll = false,
                DriPrimaryKey = true,
                DriUniqueKeys = true,
                DriDefaults = true,
                DriChecks = true,
                DriForeignKeys = false,
                ClusteredIndexes = true,
                NonClusteredIndexes = true,
                Indexes = true,
                Triggers = true,
                Permissions = false,
                ExtendedProperties = false,
                IncludeDatabaseContext = false,
                NoCollation = false,
                FullTextIndexes = true,
                XmlIndexes = true,
                SpatialIndexes = true
            };
        }

        private static ScriptingOptions BuildModuleOptions()
        {
            return new ScriptingOptions
            {
                ScriptDrops = false,
                IncludeIfNotExists = false,
                SchemaQualify = true,
                ScriptBatchTerminator = false,
                NoCommandTerminator = false,
                DriAll = false,
                Permissions = false,
                IncludeDatabaseContext = false,
                ExtendedProperties = false
            };
        }

        private static ScriptingOptions BuildForeignKeyOptions()
        {
            return new ScriptingOptions
            {
                ScriptDrops = false,
                IncludeIfNotExists = false,
                SchemaQualify = true,
                ScriptBatchTerminator = false,
                NoCommandTerminator = false,
                DriAll = false,
                DriForeignKeys = true,
                Permissions = false,
                ExtendedProperties = false
            };
        }

        private void CollectSchemas(Database db, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            foreach (Schema schema in db.Schemas)
            {
                if (schema.IsSystemObject)
                    continue;
                if (IsReservedSchema(schema.Name))
                    continue;

                string owner = "dbo";
                try
                {
                    if (!string.IsNullOrWhiteSpace(schema.Owner))
                        owner = schema.Owner;
                }
                catch
                {
                    // SMO may not expose Owner on all versions; fall back to dbo.
                }

                objects.Add(new DatabaseObject
                {
                    Type = DatabaseObjectType.Schema,
                    Schema = schema.Name,
                    Name = schema.Name,
                    Definition = $"CREATE SCHEMA {Quoting.QuoteIdentifier(schema.Name)} AUTHORIZATION {Quoting.QuoteIdentifier(owner)}"
                });
            }
        }

        private void CollectUserDefinedTypes(Database db, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            var scripter = new Scripter { Server = db.Parent };
            scripter.Options = BuildModuleOptions();

            // Script từng kiểu riêng để gán đúng Schema/Name (trước đây gán chung Name = "udt").
            foreach (UserDefinedDataType t in db.UserDefinedDataTypes)
            {
                if (IsSystemType(t.Name)) continue;
                ScriptSingleUdt(scripter, objects, warnings, t.Urn, t.Schema, t.Name, DatabaseObjectType.UserDefinedDataType);
            }

            foreach (UserDefinedTableType t in db.UserDefinedTableTypes)
            {
                ScriptSingleUdt(scripter, objects, warnings, t.Urn, t.Schema, t.Name, DatabaseObjectType.UserDefinedTableType);
            }

            foreach (UserDefinedType t in db.UserDefinedTypes)
            {
                ScriptSingleUdt(scripter, objects, warnings, t.Urn, t.Schema, t.Name, DatabaseObjectType.UserDefinedClrType);
            }
        }

        /// <summary>Script một kiểu do user định nghĩa và thêm vào danh sách với đúng Schema/Name.</summary>
        private void ScriptSingleUdt(Scripter scripter, ICollection<DatabaseObject> objects,
            ICollection<CompatibilityIssue> warnings, Urn urn, string schema, string name, DatabaseObjectType type)
        {
            try
            {
                var urns = new UrnCollection { urn };
                var batches = ScriptAll(scripter, urns)
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .ToList();
                if (batches.Count == 0)
                    return;

                objects.Add(new DatabaseObject
                {
                    Type = type,
                    Schema = schema,
                    Name = name,
                    Definition = string.Join("\r\nGO\r\n", batches)
                });
            }
            catch (Exception ex)
            {
                var objectName = schema + "." + name;
                _logger.LogWarning("Không script được kiểu {T}: {Message}", objectName, ex.Message);
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = objectName,
                    Feature = "udt-script",
                    Message = $"Không trích xuất được script kiểu '{objectName}': {ex.Message}"
                });
            }
        }

        private void CollectSequences(Database db, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            if (!db.Sequences.Any()) return;

            var scripter = new Scripter { Server = db.Parent };
            scripter.Options = BuildModuleOptions();

            // Script từng sequence riêng để gán đúng Schema/Name (trước đây gán chung Name = "sequence").
            foreach (Sequence seq in db.Sequences)
            {
                try
                {
                    var urns = new UrnCollection { seq.Urn };
                    var batches = ScriptAll(scripter, urns)
                        .Where(b => !string.IsNullOrWhiteSpace(b))
                        .ToList();
                    if (batches.Count == 0)
                        continue;

                    objects.Add(new DatabaseObject
                    {
                        Type = DatabaseObjectType.Sequence,
                        Schema = seq.Schema,
                        Name = seq.Name,
                        Definition = string.Join("\r\nGO\r\n", batches)
                    });
                }
                catch (Exception ex)
                {
                    var objectName = seq.Schema + "." + seq.Name;
                    _logger.LogWarning("Không script được sequence {T}: {Message}", objectName, ex.Message);
                    warnings.Add(new CompatibilityIssue
                    {
                        ObjectName = objectName,
                        Feature = "sequence-script",
                        Message = $"Không trích xuất được script sequence '{objectName}': {ex.Message}"
                    });
                }
            }
        }

        /// <summary>
        /// Bảng Diagrams của SSMS (dbo.sysdiagrams): SMO luôn gắn cờ hệ thống nhưng
        /// catalog tính là bảng người dùng. Nhận diện để di chuyển như bảng thường.
        /// </summary>
        internal static bool IsDiagramTable(string? schema, string? name) =>
            string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, "sysdiagrams", StringComparison.OrdinalIgnoreCase);

        private void CollectTables(Database db, ICollection<DatabaseObject> objects, ICollection<TableSchema> tables,
            ICollection<CompatibilityIssue> warnings, TableFilter filter)
        {
            // Đọc metadata bảng từ catalog để lọc và ghi cảnh báo tính năng đặc biệt.
            var tableMetadata = ReadTableMetadataAsync(db.Name).GetAwaiter().GetResult();

            // Pha 1: lọc + gom metadata (cần đủ danh sách để biết bảng lịch sử
            // của temporal có nằm trong phạm vi di chuyển không).
            var included = new List<(Table SmoTable, TableSchema Meta, bool HasMeta)>();
            foreach (Table table in db.Tables)
            {
                // Ngoại lệ: dbo.sysdiagrams (bảng Diagrams của SSMS) bị SMO đánh dấu
                // IsSystemObject nhưng thực chất là bảng người dùng (is_ms_shipped = 0)
                // nên mọi kiểm đếm catalog đều tính nó — phải di chuyển để khỏi lệch.
                if (table.IsSystemObject && !IsDiagramTable(table.Schema, table.Name))
                    continue;

                var meta = tableMetadata.FirstOrDefault(t =>
                    string.Equals(t.Schema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase));

                if (meta != null && !filter.ShouldInclude(meta.Schema, meta.Name))
                {
                    _logger.LogInformation("Table {T} excluded by filter.", meta.PlainName);
                    continue;
                }

                if (meta != null && (meta.IsMemoryOptimized || meta.IsTemporal || meta.IsFileTable))
                {
                    var reason = meta.IsMemoryOptimized ? "memory-optimized"
                        : meta.IsTemporal ? "system-versioned (temporal)"
                        : "FileTable";
                    warnings.Add(new CompatibilityIssue
                    {
                        ObjectName = meta.PlainName,
                        Feature = reason,
                        Message = $"Table uses an advanced feature ({reason}). The table will be scripted, but it may fail on the destination and be skipped with a warning."
                    });
                }

                if (meta != null)
                {
                    tables.Add(meta);
                    included.Add((table, meta, true));
                }
                else
                {
                    // Không đọc được metadata (hiếm): vẫn script để không mất bảng.
                    included.Add((table, new TableSchema { Schema = table.Schema, Name = table.Name }, false));
                }
            }

            var scripter = new Scripter { Server = db.Parent };
            scripter.Options = BuildTableOptions();

            // Sắp xếp bảng theo phụ thuộc bảng-bảng (trigger trong batch của bảng A
            // có thể tham chiếu bảng B) để CREATE không lỗi Invalid object name.
            // FK không cần sắp xếp vì đã hoãn tạo tới cuối.
            try
            {
                var tableDeps = ReadTableReferenceMapAsync().GetAwaiter().GetResult();
                if (tableDeps.Count > 0)
                {
                    var order = included
                        .Select(x => x.SmoTable.Schema + "." + x.SmoTable.Name)
                        .ToList();
                    var sortedNames = ModuleDependencySorter.SortKeys(order, tableDeps);
                    var byName = new Dictionary<string, (Table SmoTable, TableSchema Meta, bool HasMeta)>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in included)
                    {
                        var key = entry.SmoTable.Schema + "." + entry.SmoTable.Name;
                        if (!byName.ContainsKey(key))
                            byName[key] = entry;
                    }
                    var reordered = new List<(Table SmoTable, TableSchema Meta, bool HasMeta)>(included.Count);
                    foreach (var name in sortedNames)
                    {
                        if (byName.TryGetValue(name, out var entry))
                        {
                            reordered.Add(entry);
                            byName.Remove(name);
                        }
                    }
                    foreach (var leftover in byName.Values)
                        reordered.Add(leftover);
                    included = reordered;
                    _logger.LogInformation("Đã sắp xếp {Count} bảng theo phụ thuộc.", included.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không sắp xếp được thứ tự bảng ({Message}); giữ thứ tự trích xuất.",
                    ex.Message);
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = "table-dependencies",
                    Feature = "dependency-order",
                    Message = "Không đọc được phụ thuộc giữa các bảng nên giữ thứ tự tên — "
                        + "trigger tham chiếu bảng khác có thể lỗi, dùng 'Đồng bộ 100%' để tạo lại."
                });
            }

            // Pha 2: script từng bảng riêng để gán đúng Schema/Name cho DatabaseObject.
            foreach (var (smoTable, meta, hasMeta) in included)
            {
                var objectName = smoTable.Schema + "." + smoTable.Name;
                List<string> batches;
                try
                {
                    var urns = new UrnCollection { smoTable.Urn };
                    batches = ScriptAll(scripter, urns)
                        .Where(b => !string.IsNullOrWhiteSpace(b))
                        .ToList();
                    if (batches.Count == 0)
                        continue;
                }
                catch (Exception ex)
                {
                    // Không để một bảng lỗi script làm mất cả nhóm bảng.
                    _logger.LogWarning("Không script được bảng {T}: {Message}", objectName, ex.Message);
                    warnings.Add(new CompatibilityIssue
                    {
                        ObjectName = objectName,
                        Feature = "table-script",
                        Message = $"Không trích xuất được script bảng '{objectName}': {ex.Message}"
                    });
                    continue;
                }

                var combined = string.Join("\r\nGO\r\n", batches);

                // Cột Dynamic Data Masking (2016+): 2014 không hiểu MASKED nên lột
                // mệnh đề để tạo bảng được, đồng thời đính marker để khâu fix
                // sinh view che thay thế. Dữ liệu gốc giữ nguyên giá trị.
                var masks = MaskingConverter.ExtractMasks(combined);
                if (masks.Count > 0)
                {
                    combined = MaskingConverter.AttachMarkers(
                        MaskingConverter.StripMasking(combined), masks);
                    var maskedCols = string.Join(", ", masks.Select(m => m.Column));
                    warnings.Add(new CompatibilityIssue
                    {
                        ObjectName = objectName,
                        Feature = "data-masking",
                        Message = $"Bảng '{objectName}' có {masks.Count} cột che ({maskedCols}). "
                            + "Đích 2014 không hỗ trợ masking nên app tạo cột KHÔNG che + view che thay thế; "
                            + "dữ liệu nhạy cảm sẽ hiện nguyên văn với ai có SELECT bảng gốc."
                    });
                    _logger.LogWarning(
                        "Bảng {T} có cột masking ({Cols}) — sẽ lột khi tạo trên đích và sinh view che.",
                        objectName, maskedCols);
                }

                // Bảng temporal (2016+): chuyển sang bundle tương đương 2014
                // (bảng thường + lịch sử + trigger) ngay tại trích xuất để cả
                // migration thường lẫn Đồng bộ 100% đều dùng được. Chỉ ghi đích.
                if (hasMeta && meta.NeedsTemporalConversion)
                {
                    var histSchema = meta.HistorySchema ?? meta.Schema;
                    var histTable = meta.HistoryTable ?? (meta.Name + "_History");
                    var historyInScope = tableMetadata.Any(t =>
                        string.Equals(t.Schema, histSchema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.Name, histTable, StringComparison.OrdinalIgnoreCase)
                        && tables.Contains(t));
                    var conversion = TemporalTableConverter.TryConvert(
                        combined, meta, histSchema, histTable, historyInScope);
                    if (conversion != null)
                    {
                        foreach (var w in conversion.Warnings)
                        {
                            warnings.Add(new CompatibilityIssue
                            {
                                ObjectName = objectName,
                                Feature = "temporal-convert",
                                Message = w
                            });
                        }
                        _logger.LogInformation(
                            "Bảng temporal {T} đã chuyển sang bundle 2014 (lịch sử {H}).",
                            objectName, histSchema + "." + histTable);
                        combined = conversion.CombinedScript;
                    }
                    else
                    {
                        warnings.Add(new CompatibilityIssue
                        {
                            ObjectName = objectName,
                            Feature = "temporal-convert",
                            Message = $"Bảng temporal '{objectName}' không chuyển tự động được; cần xử lý thủ công trên đích 2014."
                        });
                    }
                }

                objects.Add(new DatabaseObject
                {
                    Type = DatabaseObjectType.Table,
                    Schema = smoTable.Schema,
                    Name = smoTable.Name,
                    Definition = combined
                });
            }
        }

        private void CollectViewsModules(Database db, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            // Liệt kê module bằng SQL thuần (sys.objects + sys.sql_modules) thay vì SMO enum:
            // db.Views / db.UserDefinedFunctions / db.StoredProcedures có thể vấp
            // InvalidCastException trên nguồn SQL cũ, khiến cả nhóm view/function/proc biến mất.
            // Đọc thẳng sys.sql_modules.definition (thêm header ANSI_NULLS/QUOTED_IDENTIFIER).
            // Mô-đun mã hóa (definition NULL) hoặc CLR (FS/FT/AF) thì ghi cảnh báo riêng.
            // Thứ tự giữ nguyên: views → functions → procedures.
            var modules = ReadModuleScriptsAsync(db.Name).GetAwaiter().GetResult();

            // Đánh dấu điểm bắt đầu để sắp xếp lại đúng đoạn module vừa thêm
            // (không đụng thứ tự schema/UDT/sequence/bảng đứng trước).
            var segmentStart = objects.Count;
            foreach (var module in modules)
            {
                if (string.IsNullOrWhiteSpace(module.Definition))
                {
                    var reason = module.IsClr ? "module CLR không có body SQL"
                        : "module bị mã hóa (definition rỗng)";
                    warnings.Add(new CompatibilityIssue
                    {
                        ObjectName = module.Schema + "." + module.Name,
                        Feature = "module",
                        Message = $"'{module.Schema}.{module.Name}' bị bỏ qua: {reason}. Cần xử lý thủ công trên đích."
                    });
                    _logger.LogWarning("Module {Schema}.{Name} skipped: {Reason}.", module.Schema, module.Name, reason);
                    continue;
                }

                objects.Add(new DatabaseObject
                {
                    Type = module.Type,
                    Schema = module.Schema,
                    Name = module.Name,
                    Definition = BuildModuleHeader(module.UsesAnsiNulls, module.UsesQuotedIdentifier) + module.Definition.TrimEnd()
                });
            }

            // Sắp xếp view/function/proc theo phụ thuộc (view gốc trước view con)
            // để SchemaBuilder tạo được ngay lần đầu, khỏi lỗi Invalid object name.
            OrderModulesByDependency(objects, segmentStart, warnings);
        }

        /// <summary>
        /// Sắp xếp lại đoạn module [segmentStart..] theo đồ thị phụ thuộc đọc từ
        /// sys.sql_expression_dependencies của database nguồn (có từ SQL 2008+).
        /// Lỗi đọc catalog (server quá cũ/thiếu quyền) thì giữ nguyên thứ tự
        /// cũ + cảnh báo.
        /// </summary>
        private void OrderModulesByDependency(
            ICollection<DatabaseObject> objects, int segmentStart,
            ICollection<CompatibilityIssue> warnings)
        {
            if (objects is not List<DatabaseObject> list || segmentStart >= list.Count)
                return;

            Dictionary<string, List<string>> dependencies;
            try
            {
                dependencies = ReadModuleDependenciesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được phụ thuộc module ({Message}); giữ thứ tự trích xuất.",
                    ex.Message);
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = "module-dependencies",
                    Feature = "dependency-order",
                    Message = "Không đọc được sys.sql_expression_dependencies nên view/function/proc "
                        + "giữ thứ tự tên — view con có thể tạo trước view gốc và lỗi, "
                        + "dùng 'Đồng bộ 100%' để tạo lại theo đúng thứ tự."
                });
                return;
            }

            if (dependencies.Count == 0)
                return;

            var names = new List<string>(list.Count - segmentStart);
            for (var i = segmentStart; i < list.Count; i++)
                names.Add(list[i].DisplayName);

            var ordered = ModuleDependencySorter.SortKeys(names, dependencies);
            if (ordered.Count != names.Count)
                return;

            var byName = new Dictionary<string, DatabaseObject>(StringComparer.OrdinalIgnoreCase);
            for (var i = segmentStart; i < list.Count; i++)
            {
                if (!byName.ContainsKey(list[i].DisplayName))
                    byName[list[i].DisplayName] = list[i];
            }

            var moved = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!byName.TryGetValue(ordered[i], out var obj))
                    continue;
                if (!ReferenceEquals(list[segmentStart + i], obj))
                    moved++;
                list[segmentStart + i] = obj;
            }

            if (moved > 0)
                _logger.LogInformation("Đã sắp xếp lại {Count} module theo phụ thuộc.", moved);
        }

        /// <summary>
        /// Đọc đồ thị tham chiếu xuất phát từ bảng (trigger/computed trong batch của
        /// bảng có thể chạm bảng khác hoặc function): "schema.table" → danh sách
        /// "schema.object" tham chiếu. Chỉ SELECT catalog nguồn.
        /// </summary>
        private async Task<Dictionary<string, List<string>>> ReadTableReferenceMapAsync()
        {
            const string query = @"
SELECT OBJECT_SCHEMA_NAME(d.referencing_id) + '.' + OBJECT_NAME(d.referencing_id),
       COALESCE(d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referencing_id)) + '.' + d.referenced_entity_name
  FROM sys.sql_expression_dependencies d
  JOIN sys.tables t ON t.object_id = d.referencing_id
 WHERE d.referencing_class = 1
   AND d.referenced_class = 1
   AND d.referenced_entity_name IS NOT NULL
   AND t.is_ms_shipped = 0;";

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(_options.SourceConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    continue;
                if (!map.TryGetValue(from, out var list))
                    map[from] = list = new List<string>();
                if (!list.Contains(to, StringComparer.OrdinalIgnoreCase))
                    list.Add(to);
            }
            return map;
        }

        /// <summary>
        /// Đọc đồ thị phụ thuộc giữa các module trên database nguồn (kết nối theo
        /// chuỗi nguồn nên sys.* luôn là database nguồn): tên "schema.object" →
        /// danh sách "schema.object" mà nó tham chiếu trực tiếp. Chỉ SELECT.
        /// </summary>
        private async Task<Dictionary<string, List<string>>> ReadModuleDependenciesAsync()
        {
            const string query = @"
SELECT OBJECT_SCHEMA_NAME(d.referencing_id) + '.' + OBJECT_NAME(d.referencing_id),
       COALESCE(d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referencing_id)) + '.' + d.referenced_entity_name
  FROM sys.sql_expression_dependencies d
 WHERE d.referencing_class = 1
   AND d.referenced_class = 1
   AND d.referenced_entity_name IS NOT NULL;";

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqlConnection(_options.SourceConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    continue;
                if (!map.TryGetValue(from, out var list))
                    map[from] = list = new List<string>();
                if (!list.Contains(to, StringComparer.OrdinalIgnoreCase))
                    list.Add(to);
            }
            return map;
        }

        /// <summary>Đọc danh sách module (view/function/procedure) kèm định nghĩa SQL từ catalog nguồn.</summary>
        private async Task<List<ModuleScript>> ReadModuleScriptsAsync(string databaseName)
        {
            const string query = @"
SELECT s.name AS [schema],
       o.name,
       CASE o.type WHEN 'V' THEN 0 WHEN 'P' THEN 2 ELSE 1 END AS [kind],
       m.definition,
       CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit) AS ansi,
       CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit) AS quoted,
       CAST(CASE WHEN o.type IN ('FS','FT','AF') THEN 1 ELSE 0 END AS bit) AS is_clr
  FROM sys.objects o
  JOIN sys.schemas s ON s.schema_id = o.schema_id
  LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
 WHERE o.is_ms_shipped = 0
   AND o.type IN ('V','P','FN','IF','TF','AF','FS','FT')
 ORDER BY [kind], s.name, o.name;";

            var modules = new List<ModuleScript>();
            using var conn = new SqlConnection(_options.SourceConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var isClr = reader.GetBoolean(6);
                var type = reader.GetInt32(2) switch
                {
                    0 => DatabaseObjectType.View,
                    2 => DatabaseObjectType.StoredProcedure,
                    _ => DatabaseObjectType.Function
                };
                modules.Add(new ModuleScript
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Type = type,
                    Definition = isClr ? null : (reader.IsDBNull(3) ? null : reader.GetString(3)),
                    UsesAnsiNulls = reader.GetBoolean(4),
                    UsesQuotedIdentifier = reader.GetBoolean(5),
                    IsClr = isClr
                });
            }

            return modules;
        }

        private sealed class ModuleScript
        {
            public string Schema { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public DatabaseObjectType Type { get; init; }
            public string? Definition { get; init; }
            public bool UsesAnsiNulls { get; init; }
            public bool UsesQuotedIdentifier { get; init; }
            public bool IsClr { get; init; }
        }

        private void CollectForeignKeys(Database db, ICollection<DatabaseObject> deferred)
        {
            var scripter = new Scripter { Server = db.Parent };
            scripter.Options = BuildForeignKeyOptions();

            foreach (Table table in db.Tables)
            {
                if (table.IsSystemObject)
                    continue;

                foreach (ForeignKey fk in table.ForeignKeys)
                {
                    try
                    {
                        var script = fk.Script(scripter.Options);
                        foreach (var rawBatch in script)
                        {
                            var def = rawBatch?.Trim() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(def))
                                continue;
                            deferred.Add(new DatabaseObject
                            {
                                Type = DatabaseObjectType.ForeignKey,
                                Schema = table.Schema,
                                Name = fk.Name,
                                Definition = def
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not script foreign key {Fk} on {T}.", fk.Name, table.Name);
                    }
                }
            }
        }

        private void CollectExtendedObjects(Database db, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            try
            {
                foreach (DatabaseDdlTrigger trigger in db.Triggers)
                {
                    if (trigger.IsSystemObject) continue;
                    var header = BuildModuleHeader(trigger.AnsiNullsStatus, trigger.QuotedIdentifierStatus);
                    objects.Add(new DatabaseObject
                    {
                        Type = DatabaseObjectType.Trigger,
                        Name = trigger.Name,
                        Definition = header + trigger.TextBody
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Database-level triggers were not extracted: {Message}", ex.Message);
            }
        }

        private void CollectServerTriggers(Server server, ICollection<DatabaseObject> objects, ICollection<CompatibilityIssue> warnings)
        {
            if (!_options.CopyServerTriggers) return;

            try
            {
                var scripterOptions = BuildModuleOptions();
                foreach (ServerDdlTrigger trigger in server.Triggers)
                {
                    if (trigger.IsSystemObject) continue;

                    try
                    {
                        var script = trigger.Script(scripterOptions);
                        var merged = new StringBuilder();
                        var first = true;
                        foreach (var batch in script)
                        {
                            if (string.IsNullOrWhiteSpace(batch))
                                continue;
                            if (!first)
                                merged.AppendLine("GO");
                            merged.Append(batch).AppendLine();
                            first = false;
                        }

                        objects.Add(new DatabaseObject
                        {
                            Type = DatabaseObjectType.ServerTrigger,
                            Schema = "SERVER",
                            Name = trigger.Name,
                            Definition = merged.ToString()
                        });
                        _logger.LogInformation("Captured server-level trigger '{Name}'.", trigger.Name);
                    }
                    catch (Exception ex)
                    {
                        var message = $"Không trích xuất được server trigger '{trigger.Name}': {ex.Message}";
                        warnings.Add(new CompatibilityIssue
                        {
                            ObjectName = "SERVER:" + trigger.Name,
                            Feature = "server trigger",
                            Message = message
                        });
                        _logger.LogWarning(ex, "Server-level trigger '{Name}' was not extracted: {Message}",
                            trigger.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = "server triggers",
                    Feature = "server trigger",
                    Message = $"Server-level triggers could not be enumerated: {ex.Message}"
                });
                _logger.LogWarning(ex, "Server-level triggers were not extracted: {Message}", ex.Message);
            }
        }

        private async Task CollectPermissionsAsync(string connectionString, ICollection<DatabaseObject> objects,
            ICollection<CompatibilityIssue> warnings, CancellationToken ct)
        {
            const string query = @"
SELECT dp.class, dp.class_desc, dp.permission_name, dp.state_desc, dp.major_id,
       COALESCE(USER_NAME(dp.grantee_principal_id), '') AS grantee,
       OBJECT_SCHEMA_NAME(dp.major_id) AS obj_schema,
       OBJECT_NAME(dp.major_id) AS obj_name,
       dp.minor_id
  FROM sys.database_permissions dp
 WHERE dp.state NOT IN ('C') -- skip column permissions (minor_id <> 0)
   AND dp.minor_id = 0
   AND dp.grantee_principal_id NOT IN (0, 1) -- public, dbo
   AND dp.grantee_principal_id > 4
   AND USER_NAME(dp.grantee_principal_id) IS NOT NULL
 ORDER BY dp.class, dp.major_id, dp.permission_name;";

            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(query, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                var permissions = new List<DatabaseObject>();
                var seen = new HashSet<string>();

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var permission = reader.GetString(2);
                    var state = reader.GetString(3);
                    var grantee = reader.GetString(6);

                    if (string.IsNullOrWhiteSpace(grantee))
                        continue;

                    // Giữ nguyên WITH GRANT OPTION (trước đây bị nuốt thành GRANT thường, sai quyền).
                    var withGrantOption = state.Equals("GRANT_WITH_GRANT_OPTION", StringComparison.OrdinalIgnoreCase);
                    if (state.Equals("GRANT", StringComparison.OrdinalIgnoreCase) || withGrantOption)
                        permission = "GRANT " + permission + " TO ";
                    else if (state.Equals("DENY", StringComparison.OrdinalIgnoreCase))
                        permission = "DENY " + permission + " TO ";
                    else
                        continue;

                    string target;
                    if (reader.GetInt32(0) == 0) // DATABASE level
                    {
                        target = string.Empty; // DB-scoped grant
                        permission += Quoting.QuoteIdentifier(grantee);
                    }
                    else
                    {
                        var objSchema = reader.IsDBNull(7) ? null : reader.GetString(7);
                        var objName = reader.IsDBNull(8) ? null : reader.GetString(8);
                        if (string.IsNullOrWhiteSpace(objName))
                            continue;

                        if (reader.GetInt32(0) == 3) // SCHEMA level
                            target = "SCHEMA::" + Quoting.QuoteQualifiedName(null, objSchema ?? string.Empty) + " TO ";
                        else
                            target = "ON " + Quoting.QuoteQualifiedName(objSchema, objName) + " TO ";

                        permission += target + Quoting.QuoteIdentifier(grantee);
                    }

                    permission += withGrantOption ? " WITH GRANT OPTION;" : ";";

                    if (seen.Add(permission))
                    {
                        permissions.Add(new DatabaseObject
                        {
                            Type = DatabaseObjectType.DatabasePermission,
                            Name = "permissions",
                            Definition = permission
                        });
                    }
                }

                foreach (var permission in permissions)
                    {
                        objects.Add(permission);
                    }
            }
            catch (Exception ex)
            {
                warnings.Add(new CompatibilityIssue
                {
                    ObjectName = "permissions",
                    Message = $"Permissions could not be extracted and will be skipped: {ex.Message}"
                });
            }
        }

        private IEnumerable<DatabaseObject> ApplyObjectFilters(IEnumerable<DatabaseObject> objects)
        {
            var excluded = new HashSet<string>(_options.ExcludeObjects ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var obj in objects)
            {
                var key = obj.Name.TrimStart('[').TrimEnd(']');
                if (excluded.Contains(key) || excluded.Contains(obj.DisplayName))
                {
                    _logger.LogInformation("Object {Obj} excluded by filter.", obj.DisplayName);
                    continue;
                }

                yield return obj;
            }
        }

        private TableFilter BuildTableFilter()
        {
            return new TableFilter(_options.IncludeTablesOnly ?? new List<string>(), _options.ExcludeTables ?? new List<string>());
        }

        /// <summary>Queries the source catalog for the rich per-table metadata used downstream.</summary>
        internal async Task<List<TableSchema>> ReadTableMetadataAsync(string databaseName)
        {
            var tables = new List<TableSchema>();

            // sys.columns doesn't expose PK membership; syscheck via sys.index_columns join.
            using var conn = new SqlConnection(_options.SourceConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var tableQuery = @"
SELECT OBJECT_SCHEMA_NAME(t.object_id) AS [schema],
       t.name,
       t.is_memory_optimized,
       CAST(CASE WHEN t.temporal_type IN (1,3,4) THEN 1 ELSE 0 END AS bit) AS is_temporal,
       t.is_filetable,
       t.is_external,
       t.is_replicated,
       t.is_ms_shipped
  FROM sys.tables t
 WHERE t.is_ms_shipped = 0;";

            // Guard: temporal_type / is_filetable / is_external only exist on 2012/2016+.
            string tableSql = tableQuery;
            var versionQuery = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)";
            int major = 0;
            using (var vcmd = new SqlCommand(versionQuery, conn))
            {
                major = Convert.ToInt32(await vcmd.ExecuteScalarAsync().ConfigureAwait(false));
            }

            if (major < 13)
            {
                tableSql = tableSql.Replace("CAST(CASE WHEN t.temporal_type IN (1,3,4) THEN 1 ELSE 0 END AS bit) AS is_temporal,",
                    "CAST(0 AS bit) AS is_temporal,");
            }

            if (major < 11)
            {
                tableSql = tableSql.Replace("       t.is_filetable,", "       CAST(0 AS bit) AS is_filetable,");
            }

            if (major < 13)
            {
                tableSql = tableSql.Replace("       t.is_external,", "       CAST(0 AS bit) AS is_external,");
            }

            using (var cmd = new SqlCommand(tableSql, conn) { CommandTimeout = _options.CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    tables.Add(new TableSchema
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        IsMemoryOptimized = reader.GetBoolean(2),
                        IsTemporal = reader.GetBoolean(3),
                        IsFileTable = reader.GetBoolean(4),
                        IsExternal = reader.GetBoolean(5),
                        IsReplicated = reader.GetBoolean(6)
                    });
                }
            }

            // Columns per table.
            var columnQuery = @"
SELECT OBJECT_SCHEMA_NAME(c.object_id), OBJECT_NAME(c.object_id),
       c.name, TYPE_NAME(c.user_type_id) AS type_name,
       c.is_computed, c.is_identity, c.is_nullable,
       CAST(CASE WHEN TYPE_NAME(c.user_type_id) IN ('timestamp','rowversion') THEN 1 ELSE 0 END AS bit) AS is_rowversion,
       CAST(ISNULL((SELECT 1 FROM sys.index_columns ic
                JOIN sys.indexes ix ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id AND ix.is_primary_key = 1
               WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id), 0) AS bit) AS is_pk
  FROM sys.columns c
 ORDER BY OBJECT_ID(c.object_id), c.column_id;";

            using (var cmd = new SqlCommand(columnQuery, conn) { CommandTimeout = _options.CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var table = tables.FirstOrDefault(t =>
                        string.Equals(t.Schema, reader.GetString(0), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.Name, reader.GetString(1), StringComparison.OrdinalIgnoreCase));
                    if (table == null)
                        continue;

                    var column = new ColumnSchema
                    {
                        Name = reader.GetString(2),
                        DataTypeName = reader.GetString(3),
                        IsComputed = reader.GetBoolean(4),
                        IsIdentity = reader.GetBoolean(5),
                        IsNullable = reader.GetBoolean(6),
                        IsRowVersion = reader.GetBoolean(7),
                        IsPrimaryKey = reader.GetBoolean(8)
                    };

                    table.AddColumn(column);
                }
            }

            // Foreign keys per table.
            var fkQuery = @"
SELECT OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id),
       fk.name,
       OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id),
       fk.is_disabled, fk.is_not_trusted,
       CAST(CASE WHEN fk.name LIKE 'FK__%' THEN 1 ELSE 0 END AS bit) AS is_system_named
  FROM sys.foreign_keys fk
 ORDER BY OBJECT_NAME(fk.parent_object_id);";

            using (var cmd = new SqlCommand(fkQuery, conn) { CommandTimeout = _options.CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var table = tables.FirstOrDefault(t =>
                        string.Equals(t.Schema, reader.GetString(0), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.Name, reader.GetString(1), StringComparison.OrdinalIgnoreCase));
                    if (table == null)
                        continue;

                    var fk = new ForeignKeySchema
                    {
                        TableSchema = reader.GetString(0),
                        TableName = reader.GetString(1),
                        Name = reader.GetString(2),
                        ReferencedTableSchema = reader.GetString(3),
                        ReferencedTableName = reader.GetString(4),
                        IsDisabled = reader.GetBoolean(5),
                        IsNotTrusted = reader.GetBoolean(6),
                        IsSystemNamed = reader.GetBoolean(7)
                    };

                    table.AddForeignKey(fk);
                }
            }

            // Mark HasIdentity from column metadata.
            foreach (var table in tables)
            {
                var cols = (table.Columns as List<ColumnSchema>) ?? new List<ColumnSchema>();
                table.HasIdentity = cols.Any(c => c.IsIdentity);
            }

            // Thông tin temporal (bảng lịch sử + 2 cột kỳ hạn) cho SQL 2016+.
            // Bọc try/catch vì sys.periods/history_table_id không tồn tại trước 2016.
            await ReadTemporalDetailsAsync(conn, tables).ConfigureAwait(false);

            return tables;
        }

        /// <summary>
        /// Đọc tên bảng lịch sử và 2 cột kỳ hạn của bảng system-versioned temporal.
        /// Chỉ chạy được trên SQL 2016+; server cũ hơn thì bỏ qua lặng lẽ.
        /// </summary>
        private async Task ReadTemporalDetailsAsync(SqlConnection conn, List<TableSchema> tables)
        {
            const string temporalSql = @"
SELECT OBJECT_SCHEMA_NAME(t.object_id), t.name,
       OBJECT_SCHEMA_NAME(t.history_table_id), OBJECT_NAME(t.history_table_id),
       c1.name, c2.name
  FROM sys.tables t
  LEFT JOIN sys.periods p ON p.object_id = t.object_id
  LEFT JOIN sys.columns c1 ON c1.object_id = t.object_id AND c1.column_id = p.start_column_id
  LEFT JOIN sys.columns c2 ON c2.object_id = t.object_id AND c2.column_id = p.end_column_id
 WHERE t.temporal_type = 2;";
            try
            {
                using (var cmd = new SqlCommand(temporalSql, conn) { CommandTimeout = _options.CommandTimeoutSeconds })
                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var table = tables.FirstOrDefault(t =>
                            string.Equals(t.Schema, reader.GetString(0), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(t.Name, reader.GetString(1), StringComparison.OrdinalIgnoreCase));
                        if (table == null)
                            continue;
                        table.SetTemporalInfo(
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5));
                    }
                }
            }
            catch (Exception ex)
            {
                // Server cũ không có catalog temporal — không phải lỗi.
                _logger.LogDebug("Bỏ qua đọc chi tiết temporal ({Message}).", ex.Message);
            }
        }

        private static IEnumerable<string> ScriptAll(Scripter scripter, UrnCollection urns)
        {
            if (urns.Count == 0)
                yield break;

            var script = scripter.Script(urns);
            foreach (var batch in script)
            {
                var text = batch;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // SMO emits a trailing GO when ScriptBatchTerminator is enabled; strip it
                // so the model truly represents one batch without terminators.
                var trimmed = text.TrimEnd();
                if (trimmed.EndsWith("GO", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(0, trimmed.Length - 2).TrimEnd();

                yield return trimmed;
            }
        }

        private static string BuildModuleHeader(bool ansiNulls, bool quotedIdentifier)
        {
            var sb = new StringBuilder();
            sb.Append("SET ANSI_NULLS ").Append(ansiNulls ? "ON" : "OFF").AppendLine();
            sb.AppendLine("GO");
            sb.Append("SET QUOTED_IDENTIFIER ").Append(quotedIdentifier ? "ON" : "OFF").AppendLine();
            sb.AppendLine("GO");
            return sb.ToString();
        }

        private static bool IsSystemType(string name) =>
            name.StartsWith("sys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("geography", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase);

        private static bool IsReservedSchema(string name) =>
            name.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("guest", StringComparison.OrdinalIgnoreCase);

        private sealed class TableFilter
        {
            private readonly List<string>? _include;
            private readonly List<string>? _exclude;

            public TableFilter(IList<string> include, IList<string> exclude)
            {
                _include = include?.Count > 0 ? include.Select(Normalize).ToList() : null;
                _exclude = exclude?.Count > 0 ? exclude.Select(Normalize).ToList() : null;
            }

            public bool ShouldInclude(string schema, string name)
            {
                var key = Normalize(schema, name);
                if (_exclude != null && _exclude.Contains(key))
                    return false;
                if (_include != null)
                    return _include.Contains(key);
                return true;
            }

            private static string Normalize(string schema, string name) =>
                (schema.Trim('[', ']') + "." + name.Trim('[', ']')).ToLowerInvariant();

            private static string Normalize(string value) =>
                value.Trim('[', ']').ToLowerInvariant();
        }
    }
}