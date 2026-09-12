using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Sinh DDL CREATE TABLE từ mô hình chuẩn cho 3 engine (PostgreSQL/SQLite/SQL Server).
    /// Thuần chuỗi để kiểm thử không cần server. Identifier luôn bọc ngoặc kép
    /// (PG/SQLite) hoặc ngoặc vuông (SQL Server) — đặc biệt PG hạ chữ thường
    /// tên không quote nên bắt buộc quote 100%.
    /// </summary>
    public static class CanonicalDdl
    {
        internal static string PgQuote(string name) =>
            "\"" + (name ?? "").Replace("\"", "\"\"") + "\"";

        internal static string LiteQuote(string name) =>
            "\"" + (name ?? "").Replace("\"", "\"\"") + "\"";

        /// <summary>Tên schema đích trên Postgres (giữ nguyên, mover tự CREATE SCHEMA).</summary>
        internal static string PgSchema(string? schema) =>
            string.IsNullOrWhiteSpace(schema) ? "public" : schema!;

        /// <summary>
        /// Dịch DEFAULT đơn giản sang engine đích. Trả (giữ lại?, cảnh báo?).
        /// Chỉ giữ literal + hàm giờ chuẩn; còn lại bỏ + cảnh báo.
        /// </summary>
        internal static (string? Kept, string? Warning) TranslateDefault(
            string? defaultSql, DatabaseEngine target)
        {
            if (string.IsNullOrWhiteSpace(defaultSql))
                return (null, null);
            // Giữ 2 dạng: nguyên văn (có ngoặc, cho hàm) và đã lột ngoặc ngoài (cho literal).
            var t = defaultSql.Trim().TrimEnd(';').Trim();
            var bare = t.Trim('(', ')', ' ');
            // Số literal.
            if (Regex.IsMatch(bare, @"^[+-]?(\d+\.?\d*|\.\d+)$"))
                return (bare, null);
            // Chuỗi literal (thử cả 2 dạng).
            var sm = Regex.Match(t, @"^(N?)'((?:[^']|'')*)'$", RegexOptions.IgnoreCase);
            if (!sm.Success)
                sm = Regex.Match(bare, @"^(N?)'((?:[^']|'')*)'$", RegexOptions.IgnoreCase);
            if (sm.Success)
            {
                var body = sm.Groups[2].Value.Replace("''", "'");
                return target == DatabaseEngine.SqlServer && sm.Groups[1].Length > 0
                    ? ("N'" + body.Replace("'", "''") + "'", null)
                    : ("'" + body.Replace("'", "''") + "'", null);
            }
            if (bare.Equals("true", StringComparison.OrdinalIgnoreCase)
                || bare.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                var bit = bare.Equals("true", StringComparison.OrdinalIgnoreCase);
                return target == DatabaseEngine.SqlServer
                    ? (bit ? "1" : "0", null)
                    : (bit ? "true" : "false", null);
            }
            if (bare.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return ("NULL", null);
            // Hàm giờ chuẩn các engine (so cả 2 dạng có/không ngoặc ngoài).
            foreach (var candidate in new[] { t.ToUpperInvariant(), bare.ToUpperInvariant() })
            {
                if (candidate is "CURRENT_TIMESTAMP" or "NOW()" or "GETDATE()" or "SYSDATETIME()"
                    or "SYSUTCDATETIME()" or "SYSDATETIMEOFFSET()" or "CURRENT_DATE" or "CURRENT_TIME")
                {
                    if (target == DatabaseEngine.SqlServer)
                        return candidate.Contains("UTC") || candidate.Contains("OFFSET")
                            ? ("SYSUTCDATETIME()", null) : ("GETDATE()", null);
                    return ("CURRENT_TIMESTAMP", null);
                }
                if (candidate is "NEWID()" or "GEN_RANDOM_UUID()" or "UUID_GENERATE_V4()")
                {
                    return target switch
                    {
                        DatabaseEngine.PostgreSql => ("gen_random_uuid()",
                            "NEWID() chuyển sang gen_random_uuid() — cần extension pgcrypto trên đích."),
                        DatabaseEngine.SqlServer => ("NEWID()", null),
                        _ => (null, "DEFAULT sinh GUID bị bỏ (engine đích không hỗ trợ tương đương).")
                    };
                }
            }
            return (null, $"DEFAULT '{defaultSql}' không dịch được nên bị bỏ — kiểm tra tay.");
        }

        // ------------------------------------------------------------------
        // PostgreSQL
        // ------------------------------------------------------------------

        public static string EmitPostgres(CanonicalTable table, out List<string> warnings)
        {
            warnings = new List<string>();
            var schema = PgSchema(table.Schema);
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(PgQuote(schema)).Append('.').Append(PgQuote(table.Name)).Append(" (\n");
            var defs = new List<string>();
            foreach (var c in table.Columns)
            {
                var (mapped, _, _, _, warn) = DbTypeMappers.ConvertTo(
                    c.Type, c.MaxLength, c.Precision, c.Scale, DatabaseEngine.PostgreSql);
                if (warn != null)
                    warnings.Add($"Cột {c.Name}: {warn}");
                var typeSql = EmitPostgresColumn(mapped, c);
                var col = PgQuote(c.Name) + " " + typeSql;
                if (c.IsIdentity)
                    col += " GENERATED BY DEFAULT AS IDENTITY";
                if (!c.IsNullable)
                    col += " NOT NULL";
                var (kept, dwarn) = TranslateDefault(c.DefaultSql, DatabaseEngine.PostgreSql);
                if (dwarn != null)
                    warnings.Add($"Cột {c.Name}: {dwarn}");
                if (kept != null)
                    col += " DEFAULT " + kept;
                defs.Add("    " + col);
            }
            var pk = new List<string>(table.PrimaryKeyColumns);
            if (pk.Count > 0)
                defs.Add("    PRIMARY KEY (" + string.Join(", ", pk.ConvertAll(PgQuote)) + ")");
            foreach (var fk in table.ForeignKeys)
                defs.Add("    " + EmitPostgresFk(fk));
            sb.Append(string.Join(",\n", defs));
            sb.Append("\n);");
            return sb.ToString();
        }

        private static string EmitPostgresColumn(CanonicalType mapped, CanonicalColumn c)
        {
            return mapped switch
            {
                CanonicalType.Decimal => c.Precision > 0
                    ? $"NUMERIC({c.Precision},{c.Scale})" : "NUMERIC",
                CanonicalType.String => c.MaxLength > 0 ? $"VARCHAR({c.MaxLength})" : "TEXT",
                CanonicalType.Time => c.Scale > 0 ? $"TIME({c.Scale})" : "TIME",
                CanonicalType.DateTime => c.Scale > 0 ? $"TIMESTAMP({c.Scale})" : "TIMESTAMP",
                _ => DbTypeMappers.EmitPostgres(mapped, c.MaxLength, c.Precision, c.Scale)
            };
        }

        private static string EmitPostgresFk(CanonicalForeignKey fk)
        {
            var head = string.IsNullOrWhiteSpace(fk.Name)
                ? "FOREIGN KEY"
                : "CONSTRAINT " + PgQuote(fk.Name) + " FOREIGN KEY";
            var cols = new List<string>();
            foreach (var c in fk.Columns)
                cols.Add(PgQuote(c));
            var refs = new List<string>();
            foreach (var c in fk.RefColumns)
                refs.Add(PgQuote(c));
            var target = string.IsNullOrWhiteSpace(fk.RefSchema)
                ? PgQuote(fk.RefTable)
                : PgQuote(fk.RefSchema!) + "." + PgQuote(fk.RefTable);
            return $"{head} ({string.Join(", ", cols)}) REFERENCES {target} ({string.Join(", ", refs)})";
        }

        // ------------------------------------------------------------------
        // SQLite
        // ------------------------------------------------------------------

        public static string EmitSqlite(CanonicalTable table, out List<string> warnings)
        {
            warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(table.Schema)
                && !table.Schema!.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                && !table.Schema!.Equals("main", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Schema '{table.Schema}' không tồn tại trên SQLite — dùng tên bảng trần.");

            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(LiteQuote(table.Name)).Append(" (\n");
            var defs = new List<string>();
            var pk = new List<string>(table.PrimaryKeyColumns);
            var singleIntegerPk = pk.Count == 1 && IsIntegerPk(table, pk[0]);
            foreach (var c in table.Columns)
            {
                var (mapped, _, _, _, warn) = DbTypeMappers.ConvertTo(
                    c.Type, c.MaxLength, c.Precision, c.Scale, DatabaseEngine.Sqlite);
                if (warn != null && !warnings.Contains("Cột " + c.Name + ": " + warn))
                    warnings.Add($"Cột {c.Name}: {warn}");
                var col = LiteQuote(c.Name) + " " + DbTypeMappers.EmitSqlite(
                    mapped, c.MaxLength, c.Precision, c.Scale);
                if (singleIntegerPk && c.Name.Equals(pk[0], StringComparison.OrdinalIgnoreCase))
                    col += " PRIMARY KEY";
                if (!c.IsNullable)
                    col += " NOT NULL";
                var (kept, dwarn) = TranslateDefault(c.DefaultSql, DatabaseEngine.Sqlite);
                if (dwarn != null)
                    warnings.Add($"Cột {c.Name}: {dwarn}");
                if (kept != null)
                    col += " DEFAULT " + kept;
                defs.Add("    " + col);
            }
            if (pk.Count > 1)
            {
                var quoted = new List<string>();
                foreach (var c in pk)
                    quoted.Add(LiteQuote(c));
                defs.Add("    PRIMARY KEY (" + string.Join(", ", quoted) + ")");
            }
            foreach (var fk in table.ForeignKeys)
                defs.Add("    " + EmitSqliteFk(fk));
            sb.Append(string.Join(",\n", defs));
            sb.Append("\n);");
            return sb.ToString();
        }

        private static bool IsIntegerPk(CanonicalTable table, string pkColumn)
        {
            foreach (var c in table.Columns)
            {
                if (c.Name.Equals(pkColumn, StringComparison.OrdinalIgnoreCase))
                    return c.Type is CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64;
            }
            return false;
        }

        private static string EmitSqliteFk(CanonicalForeignKey fk)
        {
            var cols = new List<string>();
            foreach (var c in fk.Columns)
                cols.Add(LiteQuote(c));
            var refs = new List<string>();
            foreach (var c in fk.RefColumns)
                refs.Add(LiteQuote(c));
            var head = string.IsNullOrWhiteSpace(fk.Name)
                ? "FOREIGN KEY"
                : "CONSTRAINT " + LiteQuote(fk.Name) + " FOREIGN KEY";
            return $"{head} ({string.Join(", ", cols)}) REFERENCES {LiteQuote(fk.RefTable)} ({string.Join(", ", refs)})";
        }

        // ------------------------------------------------------------------
        // SQL Server (cho chiều ngược PG/SQLite → MSSQL)
        // ------------------------------------------------------------------

        public static string EmitSqlServer(CanonicalTable table, out List<string> warnings)
        {
            warnings = new List<string>();
            var schema = string.IsNullOrWhiteSpace(table.Schema) ? "dbo" : table.Schema!;
            if (string.IsNullOrWhiteSpace(table.Schema))
                warnings.Add("Bảng không có schema nên dùng dbo trên SQL Server.");
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(Quoting.QuoteQualifiedName(schema, table.Name)).Append(" (\n");
            var defs = new List<string>();
            foreach (var c in table.Columns)
            {
                var (mapped, _, _, _, warn) = DbTypeMappers.ConvertTo(
                    c.Type, c.MaxLength, c.Precision, c.Scale, DatabaseEngine.SqlServer);
                if (warn != null)
                    warnings.Add($"Cột {c.Name}: {warn}");
                var col = Quoting.QuoteIdentifier(c.Name) + " "
                    + DbTypeMappers.EmitSqlServer(mapped, c.MaxLength, c.Precision, c.Scale, c.IsUnicode);
                if (c.IsIdentity)
                    col += " IDENTITY(1,1)";
                if (!c.IsNullable)
                    col += " NOT NULL";
                var (kept, dwarn) = TranslateDefault(c.DefaultSql, DatabaseEngine.SqlServer);
                if (dwarn != null)
                    warnings.Add($"Cột {c.Name}: {dwarn}");
                if (kept != null)
                    col += " DEFAULT " + kept;
                defs.Add("    " + col);
            }
            var pk = new List<string>(table.PrimaryKeyColumns);
            if (pk.Count > 0)
            {
                var quoted = new List<string>();
                foreach (var c in pk)
                    quoted.Add(Quoting.QuoteIdentifier(c));
                defs.Add("    PRIMARY KEY (" + string.Join(", ", quoted) + ")");
            }
            foreach (var fk in table.ForeignKeys)
                defs.Add("    " + EmitSqlServerFk(fk, schema));
            sb.Append(string.Join(",\n", defs));
            sb.Append("\n);");
            return sb.ToString();
        }

        private static string EmitSqlServerFk(CanonicalForeignKey fk, string defaultSchema)
        {
            var head = string.IsNullOrWhiteSpace(fk.Name)
                ? "FOREIGN KEY"
                : "CONSTRAINT " + Quoting.QuoteIdentifier(fk.Name) + " FOREIGN KEY";
            var cols = new List<string>();
            foreach (var c in fk.Columns)
                cols.Add(Quoting.QuoteIdentifier(c));
            var refs = new List<string>();
            foreach (var c in fk.RefColumns)
                refs.Add(Quoting.QuoteIdentifier(c));
            var target = Quoting.QuoteQualifiedName(
                string.IsNullOrWhiteSpace(fk.RefSchema) ? defaultSchema : fk.RefSchema!,
                fk.RefTable);
            return $"{head} ({string.Join(", ", cols)}) REFERENCES {target} ({string.Join(", ", refs)})";
        }
    }
}
