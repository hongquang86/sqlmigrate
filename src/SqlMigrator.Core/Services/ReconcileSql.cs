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
    /// Hàm thuần dựng SQL và so sánh giá trị cho "đồng bộ 100%" (Reconcile):
    /// dựng câu lệnh sửa dữ liệu (UPDATE/DELETE theo khóa), chia dải khóa con,
    /// dựng kiểu cột từ metadata sys.columns, và so khóa/nội dung với nhau.
    /// Tách thuần để kiểm thử xUnit không cần server thật.
    /// </summary>
    public static class ReconcileSql
    {
        /// <summary>Biểu thức cột khóa (giống DataVerifier.KeyExpression) để dùng chung trong Reconcile.</summary>
        internal static string KeyExpression(TransferTablePlan plan)
        {
            if (plan.KeyColumn == null)
                return "1=1";
            if (plan.KeyKind == TransferKeyKind.RowVersion)
                return "CONVERT(bigint, " + Quoting.QuoteIdentifier(plan.KeyColumn) + ")";
            return Quoting.QuoteIdentifier(plan.KeyColumn);
        }

        /// <summary>
        /// Lấy TOP(n) khóa trong dải (lo, hi] để xử lý destination theo sub-chunk bounded.
        /// lo/hi là chuỗi trung tính; lo = null bỏ điều kiện dưới, hi = null bỏ điều kiện trên.
        /// </summary>
        internal static string BuildKeysRangeSql(TransferTablePlan plan, string? lo, string? hi, int topRows,
            out SqlParameter[] parameters)
        {
            var keyExpr = KeyExpression(plan);
            var clauses = new List<string>();
            var @params = new List<SqlParameter>
            {
                new("@rows", SqlDbType.Int) { Value = topRows }
            };

            if (!string.IsNullOrEmpty(lo))
            {
                @params.Add(new SqlParameter("@lo", TransferChunker.SqlDbTypeFor(plan.KeyKind))
                {
                    Value = TransferChunker.ParseKey(plan.KeyKind, lo!)
                });
                clauses.Add(keyExpr + " > @lo");
            }

            if (!string.IsNullOrEmpty(hi))
            {
                @params.Add(new SqlParameter("@hi", TransferChunker.SqlDbTypeFor(plan.KeyKind))
                {
                    Value = TransferChunker.ParseKey(plan.KeyKind, hi!)
                });
                clauses.Add(keyExpr + " <= @hi");
            }

            parameters = @params.ToArray();
            var sql = "SELECT TOP (@rows) " + keyExpr + " AS k FROM " + plan.QualifiedName;
            if (clauses.Count > 0)
                sql += " WHERE " + string.Join(" AND ", clauses);
            sql += " ORDER BY k;";
            return sql;
        }

        /// <summary>UPDATE dòng theo khóa: chỉ cập nhật các cột dữ liệu (không chạm cột khóa).</summary>
        internal static string BuildUpdateSql(TransferTablePlan plan)
        {
            var setColumns = plan.Columns
                .Where(c => !string.Equals(c, plan.KeyColumn, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (setColumns.Count == 0)
                return string.Empty;
            var sets = string.Join(", ", setColumns.Select((c, i) =>
                Quoting.QuoteIdentifier(c) + " = @p" + i.ToString(CultureInfo.InvariantCulture)));
            return "UPDATE " + plan.QualifiedName + " SET " + sets + " WHERE " + KeyExpression(plan) + " = @pk;";
        }

        /// <summary>DELETE một dòng theo khóa (dòng thừa trên đích).</summary>
        internal static string BuildDeleteSql(TransferTablePlan plan) =>
            "DELETE FROM " + plan.QualifiedName + " WHERE " + KeyExpression(plan) + " = @pk;";

        /// <summary>Xóa toàn bộ dòng đích có khóa vượt quá mốc cuối của nguồn.</summary>
        internal static string BuildTailDeleteSql(TransferTablePlan plan) =>
            "DELETE FROM " + plan.QualifiedName + " WHERE " + KeyExpression(plan) + " > @max;";

        /// <summary>ALTER TABLE ADD một cột; nullability theo cột nguồn.</summary>
        internal static string BuildAddColumnSql(string? schema, string tableName, string columnName,
            string dataTypeSql, bool isNullable)
        {
            var table = Quoting.QuoteQualifiedName(schema, tableName);
            return "ALTER TABLE " + table + " ADD " + Quoting.QuoteIdentifier(columnName) + " "
                + dataTypeSql + (isNullable ? " NULL;" : " NOT NULL;");
        }

        /// <summary>
        /// Dựng chuỗi kiểu dữ liệu đầy đủ cho ALTER TABLE ADD từ metadata sys.columns
        /// (max_length/precision/scale). Handle max_length = -1 (varchar(max)...), kiểu
        /// do người dùng định nghĩa (schema-qualified) và các kiểu hệ thống thường gặp.
        /// </summary>
        internal static string BuildDataTypeSql(string? typeSchema, string typeName,
            short maxLength, byte precision, byte scale)
        {
            var lower = typeName.ToLowerInvariant();

            // Kiểu do người dùng định nghĩa (schema hệ thống khác sys) → dùng tên schema.type.
            if (!string.IsNullOrEmpty(typeSchema) &&
                !string.Equals(typeSchema, "sys", StringComparison.OrdinalIgnoreCase) &&
                !typeName.StartsWith("sys.", StringComparison.OrdinalIgnoreCase))
            {
                return Quoting.QuoteQualifiedName(typeSchema, typeName);
            }

            return lower switch
            {
                "int" or "bigint" or "smallint" or "tinyint" or "bit" or "money" or "smallmoney"
                    or "uniqueidentifier" or "date" or "datetime" or "smalldatetime"
                    or "xml" or "text" or "ntext" or "image" or "sql_variant" or "geometry" or "geography"
                    or "hierarchyid" or "rowversion" or "timestamp" => typeName,

                "float" => precision is > 0 and not 53 ? "float(" + precision + ")" : "float",
                "real" => "real",

                "decimal" or "numeric" => "decimal(" + precision + "," + scale + ")",

                "time" or "datetime2" or "datetimeoffset" =>
                    scale > 0 ? lower + "(" + scale + ")" : lower,

                "char" or "varchar" => FormatLength(typeName, maxLength, isUnicode: false),
                "nchar" or "nvarchar" => FormatLength(typeName, maxLength, isUnicode: true),
                "binary" or "varbinary" => FormatBinary(typeName, maxLength),

                _ => typeName
            };
        }

        private static string FormatLength(string typeName, short maxLength, bool isUnicode)
        {
            if (maxLength == -1)
                return typeName + "(max)";
            var length = isUnicode ? maxLength / 2 : maxLength;
            return typeName + "(" + Math.Max(0, length).ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string FormatBinary(string typeName, short maxLength)
        {
            if (maxLength == -1)
                return typeName + "(max)";
            return typeName + "(" + Math.Max(0, (int)maxLength).ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// So khóa/nội dung hai dòng đọc từ SqlDataReader theo cùng thứ tự cột của plan.
        /// Trả về danh sách keys của dòng thừa (chỉ đích), cặp cần UPDATE, keys của dòng
        /// thiếu (chỉ nguồn). Thuần — dùng cho kiểm thử diff mà không cần server.
        /// </summary>
        internal static RowActions DiffRows(
            IDictionary<string, object[]> sourceByKey,
            IEnumerable<(string Key, object[] Values)> destRows,
            bool skipUpdate = false)
        {
            var actions = new RowActions();
            var pending = new Dictionary<string, object[]>(sourceByKey, StringComparer.Ordinal);

            foreach (var (key, destValues) in destRows)
            {
                if (pending.TryGetValue(key, out var sourceValues))
                {
                    if (!skipUpdate && !RowsEqual(sourceValues, destValues))
                        actions.Updates.Add((key, sourceValues));
                    pending.Remove(key);
                }
                else
                {
                    actions.Deletes.Add(key);
                }
            }

            actions.Inserts.AddRange(pending.Keys);
            return actions;
        }

        /// <summary>Kết quả diff một dải: dòng thiếu (insert), lệch nội dung (update), dòng thừa (delete).</summary>
        internal sealed class RowActions
        {
            public List<string> Inserts { get; } = new();
            public List<(string Key, object[] Values)> Updates { get; } = new();
            public List<string> Deletes { get; } = new();
        }

        /// <summary>So hai dòng theo từng cột (null-safe, mảng byte, số → decimal).</summary>
        internal static bool RowsEqual(object[] a, object[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (!ValuesEqual(a[i], b[i]))
                    return false;
            }

            return true;
        }

        /// <summary>So hai giá trị ô: null/DBNull coi như nhau; byte[] so theo từng byte; số nối rộng.</summary>
        internal static bool ValuesEqual(object? a, object? b)
        {
            if (a is null || a is DBNull || b is null || b is DBNull)
                return (a is null || a is DBNull) && (b is null || b is DBNull);

            switch (a)
            {
                case byte[] aa when b is byte[] bb:
                    return aa.AsSpan().SequenceEqual(bb);
                case byte[] aa when b is System.Data.SqlTypes.SqlBinary sb:
                    return aa.AsSpan().SequenceEqual(sb.Value);
            }

            if (a is System.Data.SqlTypes.SqlBinary sbA && b is byte[] bb2)
                return sbA.Value.AsSpan().SequenceEqual(bb2);

            if (IsNumeric(a.GetType()) && IsNumeric(b.GetType()))
            {
                var da = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
                var db = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
                return da == db;
            }

            if (a is DateTime dtA && b is DateTime dtB)
                return dtA == dtB;

            if (a is Guid gA && b is Guid gB)
                return gA == gB;

            if (a is string sA && b is string sB)
                return string.Equals(sA, sB, StringComparison.Ordinal);

            return a.Equals(b);
        }

        private static bool IsNumeric(Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }
    }
}