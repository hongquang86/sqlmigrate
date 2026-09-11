using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Xây dựng kế hoạch chuyển cho từng bảng và sinh SQL chunk keyset:
    ///  - chunk khóa: lấy TOP(n) khóa &gt; mốc để biết dải dữ liệu tiếp theo (endKey);
    ///  - chunk dữ liệu: SELECT dữ liệu trong (startKey, endKey].
    /// Truy vấn khóa đọc trên index nên rẻ; dải dữ liệu luôn bounded → khóa ngắn,
    /// hồi tục được, server nguồn không bị một scan dài giữ khóa.
    /// </summary>
    public static class TransferChunker
    {
        /// <summary>Chọn cột khóa để chunk: ưu tiên khóa chính 1 cột, rồi identity, rồi rowversion.</summary>
        public static TransferTablePlan BuildPlan(TableSchema table, bool preserveIdentity,
            bool useKeysetChunking, long estimatedRows)
        {
            var dataColumns = table.GetDataColumns(preserveIdentity)
                .Where(c => !c.IsRowVersion)
                .ToList();

            var keyKind = TransferKeyKind.None;
            string? keyColumn = null;
            var strategy = TransferStrategy.FullScan;

            if (useKeysetChunking)
            {
                var pk = table.Columns.Where(c => c.IsPrimaryKey).ToList();
                if (pk.Count == 1)
                {
                    keyColumn = pk[0].Name;
                    keyKind = KeyKindOf(pk[0]);
                }
                else
                {
                    var identity = table.Columns.FirstOrDefault(c => c.IsIdentity);
                    if (identity != null)
                    {
                        keyColumn = identity.Name;
                        keyKind = KeyKindOf(identity);
                    }
                    else
                    {
                        var rv = table.Columns.FirstOrDefault(c => c.IsRowVersion);
                        if (rv != null)
                        {
                            keyColumn = rv.Name;
                            keyKind = TransferKeyKind.RowVersion;
                        }
                    }
                }

                if (keyColumn != null && keyKind != TransferKeyKind.None)
                    strategy = TransferStrategy.KeysetChunked;
            }

            return new TransferTablePlan
            {
                Schema = table.Schema,
                Name = table.Name,
                Strategy = strategy,
                KeyKind = keyKind,
                KeyColumn = keyColumn,
                Columns = dataColumns.Select(c => c.Name).ToList(),
                EstimatedRows = estimatedRows,
                EstimatedRowWidthBytes = Math.Max(32, dataColumns.Sum(c => EstimateColumnWidth(c.DataTypeName)))
            };
        }

        internal static TransferKeyKind KeyKindOf(ColumnSchema column)
        {
            if (column.IsRowVersion)
                return TransferKeyKind.RowVersion;

            var baseName = BaseTypeName(column.DataTypeName);
            return baseName switch
            {
                "bigint" or "int" or "smallint" or "tinyint" or "bit"
                    or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real"
                    => TransferKeyKind.Numeric,
                "date" or "time" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime"
                    => TransferKeyKind.DateTime,
                "uniqueidentifier" => TransferKeyKind.Guid,
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => TransferKeyKind.String,
                _ => TransferKeyKind.None
            };
        }

        private static string BaseTypeName(string dataTypeName)
        {
            var name = dataTypeName.Split('(', ')')[0].Trim().ToLowerInvariant();
            return name switch
            {
                "timestamp" => "rowversion",
                _ => name
            };
        }

        private static string KeyExpression(TransferTablePlan plan) =>
            plan.KeyKind == TransferKeyKind.RowVersion
                ? "CONVERT(bigint, " + Quoting.QuoteIdentifier(plan.KeyColumn!) + ")"
                : Quoting.QuoteIdentifier(plan.KeyColumn!);

        /// <summary>SQL lấy TOP(n) khóa lớn hơn mốc để đóng dải dữ liệu tiếp theo.</summary>
        public static string BuildChunkKeysSql(TransferTablePlan plan, int topRows, string? startKey,
            out SqlParameter[] parameters)
        {
            if (plan.Strategy != TransferStrategy.KeysetChunked || plan.KeyColumn == null)
                throw new InvalidOperationException("Keyset chunking requires a chunkable key.");

            var keyExpr = KeyExpression(plan);
            var @params = new List<SqlParameter>
            {
                new("@rows", SqlDbType.Int) { Value = topRows }
            };

            var where = string.Empty;
            if (!string.IsNullOrEmpty(startKey))
            {
                @params.Add(new SqlParameter("@start", SqlDbTypeFor(plan.KeyKind)) { Value = ParseKey(plan.KeyKind, startKey) });
                where = " WHERE " + keyExpr + " > @start";
            }

            parameters = @params.ToArray();
            return "SELECT TOP (@rows) " + keyExpr + " AS k FROM " + plan.QualifiedName + where + " ORDER BY k;";
        }

        /// <summary>
        /// SQL đọc dữ liệu trong dải (startKey, endKey]. startKey/endKey là chuỗi trung tính;
        /// endKey = null nghĩa là đến hết bảng.
        /// </summary>
        public static string BuildChunkDataSql(TransferTablePlan plan, string? startKey, string? endKey,
            out SqlParameter[] parameters)
        {
            if (plan.KeyColumn == null)
                throw new InvalidOperationException("Chunk keyset cần cột khóa.");

            var keyExpr = KeyExpression(plan);
            var clauses = new List<string>();
            var @params = new List<SqlParameter>();

            if (!string.IsNullOrEmpty(startKey))
            {
                @params.Add(new SqlParameter("@start", SqlDbTypeFor(plan.KeyKind)) { Value = ParseKey(plan.KeyKind, startKey) });
                clauses.Add(keyExpr + " > @start");
            }
            if (!string.IsNullOrEmpty(endKey))
            {
                @params.Add(new SqlParameter("@end", SqlDbTypeFor(plan.KeyKind)) { Value = ParseKey(plan.KeyKind, endKey) });
                clauses.Add(keyExpr + " <= @end");
            }

            var cols = string.Join(", ", plan.Columns.Select(Quoting.QuoteIdentifier));
            var sql = "SELECT " + cols + " FROM " + plan.QualifiedName;
            if (clauses.Count > 0)
                sql += " WHERE " + string.Join(" AND ", clauses);
            sql += ";";

            parameters = @params.ToArray();
            return sql;
        }

        /// <summary>SQL scan toàn bộ bảng (fallback cho bảng không chunk được).</summary>
        public static string BuildFullScanSql(TransferTablePlan plan)
        {
            var cols = string.Join(", ", plan.Columns.Select(Quoting.QuoteIdentifier));
            return "SELECT " + cols + " FROM " + plan.QualifiedName + ";";
        }

        internal static object ParseKey(TransferKeyKind kind, string value) => kind switch
        {
            TransferKeyKind.Numeric or TransferKeyKind.RowVersion =>
                long.Parse(value, CultureInfo.InvariantCulture),
            TransferKeyKind.DateTime =>
                DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            TransferKeyKind.Guid => Guid.Parse(value),
            _ => value
        };

        internal static string SerializeKey(TransferKeyKind kind, object value) => kind switch
        {
            TransferKeyKind.Numeric or TransferKeyKind.RowVersion =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            TransferKeyKind.DateTime =>
                ((DateTime)value).ToString("O", CultureInfo.InvariantCulture),
            TransferKeyKind.Guid => (value.ToString() ?? string.Empty),
            _ => (value as string) ?? string.Empty
        };

        internal static SqlDbType SqlDbTypeFor(TransferKeyKind kind) => kind switch
        {
            TransferKeyKind.Numeric or TransferKeyKind.RowVersion => SqlDbType.BigInt,
            TransferKeyKind.DateTime => SqlDbType.DateTime2,
            TransferKeyKind.Guid => SqlDbType.UniqueIdentifier,
            _ => SqlDbType.NVarChar
        };

        /// <summary>Ước lượng bề rộng trung bình (byte) một cột để tính ngân sách bộ nhớ.</summary>
        internal static int EstimateColumnWidth(string dataTypeName)
        {
            if (string.IsNullOrWhiteSpace(dataTypeName) || dataTypeName == "timestamp")
                dataTypeName = "rowversion";

            var body = dataTypeName.ToLowerInvariant().Trim();
            var baseName = BaseTypeName(body);
            var isMax = body.Contains("(max)");

            var length = 0;
            var open = body.IndexOf('(');
            var close = body.IndexOf(')');
            if (open >= 0 && close > open && !isMax)
            {
                var inner = body.Substring(open + 1, close - open - 1);
                var first = inner.Split(',')[0].Trim();
                if (int.TryParse(first, out var n))
                    length = n;
            }

            return baseName switch
            {
                "bigint" or "rowversion" => 8,
                "int" => 4,
                "smallint" => 2,
                "tinyint" => 1,
                "bit" or "float" or "real" => 8,
                "decimal" or "numeric" or "money" or "smallmoney" => 16,
                "date" => 3,
                "time" => 5,
                "datetime" or "smalldatetime" => 8,
                "datetime2" or "datetimeoffset" => 10,
                "uniqueidentifier" => 16,
                "char" or "binary" => Math.Max(1, length),
                "varchar" or "varbinary" => isMax ? 512 : Math.Max(8, length + 4),
                "nchar" => Math.Max(2, length * 2),
                "nvarchar" => isMax ? 512 : Math.Max(8, length * 2 + 4),
                "xml" or "text" or "ntext" or "image" or "sql_variant" or "hierarchyid" or "geography" or "geometry"
                    => 512,
                _ => 64
            };
        }

        /// <summary>SQL ước lượng số dòng từ sys.dm_db_partition_stats (rẻ hơn COUNT_BIG trên bảng lớn).</summary>
        public static string BuildEstimatedCountSql() =>
            "SELECT COALESCE(SUM(rows), 0) FROM sys.partitions "
            + "WHERE object_id = OBJECT_ID(@qname) AND index_id IN (0, 1);";

        public static string BuildExactCountSql(string qualifiedTableName) =>
            "SELECT COUNT_BIG(*) FROM " + qualifiedTableName + ";";
    }
}