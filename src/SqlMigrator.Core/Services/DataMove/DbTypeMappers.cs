using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>Đặc tả kiểu đã tách: loại chuẩn + độ dài/precision/scale.</summary>
    public readonly record struct TypeSpec(CanonicalType Type, int MaxLength, byte Precision, byte Scale);

    /// <summary>
    /// Phân tích + sinh kiểu dữ liệu 3 engine (SQL Server/PostgreSQL/SQLite) và
    /// chuyển đổi qua lại. Thuần chuỗi để kiểm thử không cần server.
    /// MaxLength: -1 = max/không giới hạn, 0 = không áp dụng.
    /// </summary>
    public static class DbTypeMappers
    {
        private static readonly Regex TypePattern =
            new(@"^\s*\[?([\w$#]+)\]?\s*(?:\(\s*([^()]*)\s*\))?\s*$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Tách "nvarchar(50)" thành (tên, danh sách tham số).</summary>
        internal static (string Base, string[] Args) SplitType(string dbType)
        {
            var m = TypePattern.Match(dbType?.Trim() ?? "");
            if (!m.Success)
                return ("", Array.Empty<string>());
            var args = new System.Collections.Generic.List<string>();
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            {
                foreach (var part in m.Groups[2].Value.Split(','))
                    args.Add(part.Trim().Trim('\'', '"', 'N', 'n'));
            }
            return (m.Groups[1].Value.ToLowerInvariant(), args.ToArray());
        }

        private static int ParseLength(string[] args, bool unicode)
        {
            if (args.Length == 0)
                return 0;
            if (args[0].Equals("max", StringComparison.OrdinalIgnoreCase))
                return -1;
            if (int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return unicode ? n : n;
            return 0;
        }

        private static byte ParseByte(string[] args, int index)
        {
            if (args.Length > index
                && byte.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                return b;
            return 0;
        }

        // ------------------------------------------------------------------
        // SQL Server
        // ------------------------------------------------------------------

        public static TypeSpec ParseSqlServer(string dbType)
        {
            var (baseName, args) = SplitType(dbType);
            return baseName switch
            {
                "tinyint" or "smallint" => new TypeSpec(CanonicalType.Int16, 0, 0, 0),
                "int" => new TypeSpec(CanonicalType.Int32, 0, 0, 0),
                "bigint" => new TypeSpec(CanonicalType.Int64, 0, 0, 0),
                "decimal" or "numeric" => new TypeSpec(CanonicalType.Decimal, 0,
                    ParseByte(args, 0) is byte p && p > 0 ? p : (byte)18,
                    ParseByte(args, 1)),
                "money" or "smallmoney" => new TypeSpec(CanonicalType.Money, 0, 0, 0),
                "float" => new TypeSpec(CanonicalType.Float, 0, 0, 0),
                "real" => new TypeSpec(CanonicalType.Float, 0, 24, 0),
                "char" or "varchar" or "text" => new TypeSpec(CanonicalType.String,
                    baseName == "text" ? -1 : ParseLength(args, false), 0, 0),
                "nchar" or "nvarchar" or "ntext" => new TypeSpec(CanonicalType.String,
                    baseName == "ntext" ? -1 : ParseLength(args, true), 0, 0),
                "bit" => new TypeSpec(CanonicalType.Bool, 0, 0, 0),
                "date" => new TypeSpec(CanonicalType.Date, 0, 0, 0),
                "time" => new TypeSpec(CanonicalType.Time, 0, 0, ParseByte(args, 0)),
                "datetime" or "smalldatetime" => new TypeSpec(CanonicalType.DateTime, 0, 0, 0),
                "datetime2" => new TypeSpec(CanonicalType.DateTime, 0, 0, ParseByte(args, 0)),
                "datetimeoffset" => new TypeSpec(CanonicalType.DateTimeTz, 0, 0, ParseByte(args, 0)),
                "binary" or "varbinary" or "image" => new TypeSpec(CanonicalType.Binary,
                    baseName == "image" ? -1 : ParseLength(args, false), 0, 0),
                "uniqueidentifier" => new TypeSpec(CanonicalType.Guid, 0, 0, 0),
                "timestamp" or "rowversion" => new TypeSpec(CanonicalType.Binary, 8, 0, 0),
                "xml" => new TypeSpec(CanonicalType.String, -1, 0, 0),
                _ => new TypeSpec(CanonicalType.Unknown, 0, 0, 0)
            };
        }

        public static string EmitSqlServer(CanonicalType type, int maxLength, byte precision, byte scale, bool unicode)
        {
            // MaxLength ở đây là số KÝ TỰ (metadata nguồn đã quy đổi từ byte nếu cần).
            string Len() => maxLength < 0 ? "(max)" : "(" + maxLength + ")";
            return type switch
            {
                CanonicalType.Int16 => "SMALLINT",
                CanonicalType.Int32 => "INT",
                CanonicalType.Int64 => "BIGINT",
                CanonicalType.Decimal => precision > 0 ? $"DECIMAL({precision},{scale})" : "DECIMAL",
                CanonicalType.Money => "MONEY",
                CanonicalType.Float => "FLOAT",
                CanonicalType.Double => "FLOAT(53)",
                CanonicalType.String => (unicode ? "NVARCHAR" : "VARCHAR") + (maxLength > 0 ? Len() : "(MAX)"),
                CanonicalType.Bool => "BIT",
                CanonicalType.Date => "DATE",
                CanonicalType.Time => scale > 0 ? $"TIME({scale})" : "TIME",
                CanonicalType.DateTime => scale > 0 ? $"DATETIME2({scale})" : "DATETIME2",
                CanonicalType.DateTimeTz => scale > 0 ? $"DATETIMEOFFSET({scale})" : "DATETIMEOFFSET",
                CanonicalType.Binary => maxLength > 0 ? $"VARBINARY({maxLength})" : "VARBINARY(MAX)",
                CanonicalType.Guid => "UNIQUEIDENTIFIER",
                CanonicalType.Json => "NVARCHAR(MAX)",
                _ => "NVARCHAR(MAX)"
            };
        }

        // ------------------------------------------------------------------
        // PostgreSQL
        // ------------------------------------------------------------------

        public static TypeSpec ParsePostgres(string dbType)
        {
            // Chuẩn hóa kiểu nhiều từ trước khi tách ("double precision" → 1 token...).
            var normalized = (dbType ?? "").Trim().ToLowerInvariant()
                .Replace("double precision", "doubleprecision")
                .Replace("timestamp with time zone", "timestamptz")
                .Replace("timestamp without time zone", "timestamp")
                .Replace("timestamp with tz", "timestamptz")
                .Replace("time with time zone", "timetz")
                .Replace("time without time zone", "time")
                .Replace("character varying", "charactervarying");
            var (baseName, args) = SplitType(normalized);
            var compact = baseName.Replace(" ", "");
            return compact switch
            {
                "smallint" or "int2" => new TypeSpec(CanonicalType.Int16, 0, 0, 0),
                "integer" or "int" or "int4" => new TypeSpec(CanonicalType.Int32, 0, 0, 0),
                "bigint" or "int8" => new TypeSpec(CanonicalType.Int64, 0, 0, 0),
                "serial" => new TypeSpec(CanonicalType.Int32, 0, 0, 0),
                "bigserial" => new TypeSpec(CanonicalType.Int64, 0, 0, 0),
                "smallserial" => new TypeSpec(CanonicalType.Int16, 0, 0, 0),
                "numeric" or "decimal" => new TypeSpec(CanonicalType.Decimal, 0,
                    ParseByte(args, 0), ParseByte(args, 1)),
                "money" => new TypeSpec(CanonicalType.Money, 0, 0, 0),
                "real" or "float4" => new TypeSpec(CanonicalType.Float, 0, 0, 0),
                "doubleprecision" or "float8" => new TypeSpec(CanonicalType.Double, 0, 0, 0),
                "character" or "char" => new TypeSpec(CanonicalType.String, ParseLength(args, true), 0, 0),
                "charactervarying" or "varchar" => new TypeSpec(CanonicalType.String, ParseLength(args, true), 0, 0),
                "text" => new TypeSpec(CanonicalType.String, -1, 0, 0),
                "boolean" or "bool" => new TypeSpec(CanonicalType.Bool, 0, 0, 0),
                "date" => new TypeSpec(CanonicalType.Date, 0, 0, 0),
                "time" or "timetz" => new TypeSpec(CanonicalType.Time, 0, 0, ParseByte(args, 0)),
                "timestamp" => new TypeSpec(CanonicalType.DateTime, 0, 0, ParseByte(args, 0)),
                "timestamptz" => new TypeSpec(CanonicalType.DateTimeTz, 0, 0, ParseByte(args, 0)),
                "bytea" => new TypeSpec(CanonicalType.Binary, 0, 0, 0),
                "uuid" => new TypeSpec(CanonicalType.Guid, 0, 0, 0),
                "json" or "jsonb" => new TypeSpec(CanonicalType.Json, 0, 0, 0),
                "xml" => new TypeSpec(CanonicalType.String, -1, 0, 0),
                _ => new TypeSpec(CanonicalType.Unknown, 0, 0, 0)
            };
        }

        public static string EmitPostgres(CanonicalType type, int maxLength, byte precision, byte scale)
        {
            return type switch
            {
                CanonicalType.Int16 => "SMALLINT",
                CanonicalType.Int32 => "INTEGER",
                CanonicalType.Int64 => "BIGINT",
                CanonicalType.Decimal => precision > 0 ? $"NUMERIC({precision},{scale})" : "NUMERIC",
                CanonicalType.Money => "NUMERIC(19,4)",
                CanonicalType.Float => "REAL",
                CanonicalType.Double => "DOUBLE PRECISION",
                CanonicalType.String => maxLength > 0 ? $"VARCHAR({maxLength})" : "TEXT",
                CanonicalType.Bool => "BOOLEAN",
                CanonicalType.Date => "DATE",
                CanonicalType.Time => scale > 0 ? $"TIME({scale})" : "TIME",
                CanonicalType.DateTime => scale > 0 ? $"TIMESTAMP({scale})" : "TIMESTAMP",
                CanonicalType.DateTimeTz => "TIMESTAMPTZ",
                CanonicalType.Binary => "BYTEA",
                CanonicalType.Guid => "UUID",
                CanonicalType.Json => "JSONB",
                _ => "TEXT"
            };
        }

        // ------------------------------------------------------------------
        // SQLite (type affinity)
        // ------------------------------------------------------------------

        public static TypeSpec ParseSqlite(string dbType)
        {
            var upper = (dbType ?? "").Trim().ToUpperInvariant();
            if (upper.Contains("INT"))
                return new TypeSpec(CanonicalType.Int64, 0, 0, 0);
            if (upper.Contains("CHAR") || upper.Contains("CLOB") || upper.Contains("TEXT"))
                return new TypeSpec(CanonicalType.String, -1, 0, 0);
            if (upper.Contains("BLOB"))
                return new TypeSpec(CanonicalType.Binary, 0, 0, 0);
            if (upper.Contains("REAL") || upper.Contains("FLOA") || upper.Contains("DOUB"))
                return new TypeSpec(CanonicalType.Double, 0, 0, 0);
            if (upper.Contains("BOOL"))
                return new TypeSpec(CanonicalType.Bool, 0, 0, 0);
            if (upper.Contains("DATETIME") || upper.Contains("TIMESTAMP"))
                return new TypeSpec(CanonicalType.DateTime, 0, 0, 0);
            if (upper.Contains("DATE") && !upper.Contains("TIME"))
                return new TypeSpec(CanonicalType.Date, 0, 0, 0);
            if (upper.Contains("TIME"))
                return new TypeSpec(CanonicalType.Time, 0, 0, 0);
            if (upper.Contains("NUMERIC") || upper.Contains("DECIMAL"))
                return new TypeSpec(CanonicalType.Decimal, 0, 0, 0);
            if (upper.Contains("GUID") || upper.Contains("UUID"))
                return new TypeSpec(CanonicalType.Guid, 0, 0, 0);
            if (upper.Contains("JSON"))
                return new TypeSpec(CanonicalType.Json, 0, 0, 0);
            return upper.Length == 0
                ? new TypeSpec(CanonicalType.Unknown, 0, 0, 0)
                : new TypeSpec(CanonicalType.String, -1, 0, 0);
        }

        public static string EmitSqlite(CanonicalType type, int maxLength, byte precision, byte scale)
        {
            return type switch
            {
                CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64 => "INTEGER",
                CanonicalType.Decimal => precision > 0 ? $"NUMERIC({precision},{scale})" : "NUMERIC",
                CanonicalType.Money => "NUMERIC(19,4)",
                CanonicalType.Float or CanonicalType.Double => "REAL",
                CanonicalType.String => maxLength > 0 ? $"VARCHAR({maxLength})" : "TEXT",
                CanonicalType.Bool => "INTEGER",
                CanonicalType.Date or CanonicalType.Time or CanonicalType.DateTime
                    or CanonicalType.DateTimeTz => "TEXT",
                CanonicalType.Binary => "BLOB",
                CanonicalType.Guid => "TEXT",
                CanonicalType.Json => "TEXT",
                _ => "TEXT"
            };
        }

        // ------------------------------------------------------------------
        // Chuyển đổi chéo engine (giữ nguyên nếu tương thích, cảnh báo nếu hao hụt).
        // ------------------------------------------------------------------

        /// <summary>
        /// Chuyển đặc tả kiểu sang engine đích. Trả (type, len, prec, scale, cảnh báo?).
        /// Kiểu Unknown luôn kèm cảnh báo (đích nhận TEXT).
        /// </summary>
        public static (CanonicalType Type, int MaxLength, byte Precision, byte Scale, string? Warning)
            ConvertTo(CanonicalType type, int maxLength, byte precision, byte scale,
                Models.DatabaseEngine target)
        {
            if (type == CanonicalType.Unknown)
                return (CanonicalType.String, -1, 0, 0,
                    "Kiểu gốc không nhận diện được — đích dùng TEXT, cần kiểm tra tay.");

            if (target == Models.DatabaseEngine.PostgreSql && type == CanonicalType.Money)
                return (CanonicalType.Decimal, 0, 19, 4,
                    "MONEY chuyển sang NUMERIC(19,4) — kiểm tra làm tròn tiền tệ.");

            if (target == Models.DatabaseEngine.Sqlite)
            {
                return type switch
                {
                    CanonicalType.DateTimeTz => (CanonicalType.String, -1, 0, 0,
                        "Múi giờ lưu dạng TEXT ISO8601 (SQLite không có kiểu giờ chuẩn)."),
                    CanonicalType.Guid => (CanonicalType.String, 36, 0, 0, null),
                    CanonicalType.Json => (CanonicalType.String, -1, 0, 0, null),
                    CanonicalType.Money => (CanonicalType.Decimal, 0, 19, 4,
                        "MONEY chuyển sang NUMERIC(19,4) — kiểm tra làm tròn tiền tệ."),
                    CanonicalType.Date or CanonicalType.Time or CanonicalType.DateTime
                        => (CanonicalType.String, -1, 0, 0,
                            "Ngày/giờ lưu dạng TEXT ISO8601 trên SQLite."),
                    _ => (type, maxLength, precision, scale, null)
                };
            }

            if (target == Models.DatabaseEngine.SqlServer && type == CanonicalType.Json)
                return (CanonicalType.String, -1, 0, 0,
                    "JSON về SQL Server lưu NVARCHAR(MAX) (mất kiểm tra well-formed).");

            return (type, maxLength, precision, scale, null);
        }
    }
}
