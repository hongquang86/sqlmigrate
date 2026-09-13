using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Endpoint MongoDB cho di chuyển 2 chiều: nguồn thì survey mẫu document để
    /// suy schema quan hệ; đích thì ghi document (collection tự tạo khi insert).
    /// Nguồn chỉ đọc; đích chỉ ghi theo mover.
    /// </summary>
    public sealed class MongoEndpoint : IDbEndpoint
    {
        public DatabaseEngine Engine => DatabaseEngine.MongoDb;

        /// <summary>Số document tối đa lấy mẫu khi suy schema một collection.</summary>
        internal const int SurveySampleSize = 500;

        internal static IMongoDatabase OpenDatabase(DbProbe probe)
        {
            if (string.IsNullOrWhiteSpace(probe.Database) || probe.Database.Contains("://"))
                throw new InvalidOperationException("Chưa chọn database MongoDB (ô Database).");
            var client = new MongoClient(Manage.MongoManageProvider.BuildConnectionString(probe));
            return client.GetDatabase(probe.Database);
        }

        /// <summary>Liệt kê database người dùng (để UI nạp combobox).</summary>
        public static async Task<List<string>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var client = new MongoClient(Manage.MongoManageProvider.BuildConnectionString(probe));
            using var cursor = await client.ListDatabaseNamesAsync(ct).ConfigureAwait(false);
            var all = await cursor.ToListAsync(ct).ConfigureAwait(false);
            var result = new List<string>();
            foreach (var name in all)
            {
                if (name is "admin" or "local" or "config")
                    continue;
                result.Add(name);
            }
            return result;
        }

        public async Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default)
        {
            var db = OpenDatabase(probe);
            using var cursor = await db.ListCollectionNamesAsync(null, ct).ConfigureAwait(false);
            var names = (await cursor.ToListAsync(ct).ConfigureAwait(false))
                .Where(n => !n.StartsWith("system.", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var tables = new List<CanonicalTable>();
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(await ReadCollectionAsync(db, name, ct).ConfigureAwait(false));
            }
            return new CanonicalSchema { Tables = tables };
        }

        private static async Task<CanonicalTable> ReadCollectionAsync(
            IMongoDatabase db, string name, CancellationToken ct)
        {
            var coll = db.GetCollection<BsonDocument>(name);
            var sample = await coll.Find(FilterDefinition<BsonDocument>.Empty)
                .Limit(SurveySampleSize).ToListAsync(ct).ConfigureAwait(false);
            var fields = MongoSurvey.InferFields(sample, out var warnings);
            var columns = new List<CanonicalColumn>();
            foreach (var f in fields)
            {
                columns.Add(new CanonicalColumn
                {
                    Name = f.Name,
                    Type = f.Type,
                    MaxLength = f.MaxLength,
                    IsNullable = f.Nullable,
                    IsPrimaryKey = f.Name == "_id",
                    DefaultSql = null
                });
            }
            var pk = columns.Any(c => c.Name == "_id") ? new List<string> { "_id" } : new List<string>();
            return new CanonicalTable
            {
                Schema = null,
                Name = name,
                Columns = columns,
                PrimaryKeyColumns = pk,
                ForeignKeys = new List<CanonicalForeignKey>()
            };
        }

        public async Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            var db = OpenDatabase(probe);
            using var cursor = await db.ListCollectionNamesAsync(
                new ListCollectionNamesOptions
                {
                    Filter = new BsonDocument("name", table)
                }, ct).ConfigureAwait(false);
            return await cursor.AnyAsync(ct).ConfigureAwait(false);
        }

        public async Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default)
        {
            // MongoDB không có DDL cột: mover gửi "MONGO:tên" (hoặc CREATE TABLE —
            // trích tên) để tạo collection trước; insert sau cũng tự tạo.
            var name = ExtractCollectionName(ddl);
            if (string.IsNullOrWhiteSpace(name))
                return;
            var db = OpenDatabase(probe);
            if (await TableExistsAsync(probe, null, name, ct).ConfigureAwait(false))
                return;
            try
            {
                await db.CreateCollectionAsync(name, null, ct).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.Code == 48) // NamespaceExists
            {
            }
        }

        internal static string? ExtractCollectionName(string ddl)
        {
            if (string.IsNullOrWhiteSpace(ddl))
                return null;
            var t = ddl.Trim();
            if (t.StartsWith("MONGO:", StringComparison.OrdinalIgnoreCase))
                return t.Substring(6).Trim().Trim('`', '"', '[', ']', ' ', ';');
            var m = Regex.Match(t,
                @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:[`""\[]?[\w$#]+[`""\]]?\.)?[`""\[]?([\w$#]+)",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public async Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default)
        {
            var db = OpenDatabase(probe);
            try
            {
                await db.DropCollectionAsync(table, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.Message.IndexOf("ns not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
            }
        }

        public async Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            var db = OpenDatabase(probe);
            return await db.GetCollection<BsonDocument>(table)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, null, ct)
                .ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<object?[]>> ReadBatchAsync(
            DbProbe probe, CanonicalTable table, string? afterKeyExclusive, int batchSize,
            CancellationToken ct = default)
        {
            var db = OpenDatabase(probe);
            var coll = db.GetCollection<BsonDocument>(table.Name);
            var take = Math.Clamp(batchSize, 1, 5000);
            var rows = new List<object?[]>();
            // Keyset trên _id theo đúng kiểu BSON (ObjectId/số/chuỗi — thứ tự BSON
            // là toàn phần nên luôn tiến tới, không lặp vô hạn như SKIP/OFFSET).
            var filter = FilterDefinition<BsonDocument>.Empty;
            if (!string.IsNullOrEmpty(afterKeyExclusive))
                filter = KeyFilter(afterKeyExclusive);
            using var cursor = await coll.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .Limit(take).ToCursorAsync(ct).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            {
                foreach (var doc in cursor.Current)
                    rows.Add(MongoSurvey.ToRow(doc, table.Columns));
            }
            return rows;
        }

        internal static FilterDefinition<BsonDocument> KeyFilter(string afterKey)
        {
            if (ObjectId.TryParse(afterKey, out var objectId))
                return Builders<BsonDocument>.Filter.Gt("_id", objectId);
            if (long.TryParse(afterKey, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number))
                return Builders<BsonDocument>.Filter.Gt("_id", number);
            return Builders<BsonDocument>.Filter.Gt("_id", afterKey);
        }

        public async Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default)
        {
            if (rows.Count == 0)
                return 0;
            var db = OpenDatabase(probe);
            var coll = db.GetCollection<BsonDocument>(table.Name);
            string? idColumn = table.PrimaryKeyColumns.Count == 1 ? table.PrimaryKeyColumns[0] : null;
            var docs = new List<BsonDocument>(rows.Count);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var converted = new object?[table.Columns.Count];
                for (var i = 0; i < table.Columns.Count && i < row.Length; i++)
                    converted[i] = CanonicalValues.ConvertFor(
                        DatabaseEngine.MongoDb, row[i], table.Columns[i].Type);
                var full = new object?[table.Columns.Count];
                Array.Copy(converted, full, Math.Min(converted.Length, full.Length));
                docs.Add(MongoSurvey.ToDocument(table.Columns, full, idColumn));
            }
            await coll.InsertManyAsync(docs, null, ct).ConfigureAwait(false);
            return docs.Count;
        }

        internal static string SerializeKey(object? value)
        {
            if (value is string s && ObjectId.TryParse(s, out _))
                return s;
            return PostgresEndpoint.SerializeKey(value);
        }
    }
}
