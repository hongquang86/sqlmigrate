using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MongoDB.Bson;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Suy luận schema quan hệ từ mẫu document MongoDB: hợp nhất field + kiểu
    /// BSON qua các document. Kiểu hỗn hợp → String + cảnh báo; document/array
    /// lồng nhau → Json + cảnh báo. Thuần logic để kiểm thử không cần server.
    /// </summary>
    public static class MongoSurvey
    {
        public sealed class FieldSpec
        {
            public string Name { get; init; } = string.Empty;
            public CanonicalType Type { get; init; }
            public int MaxLength { get; init; }
            public bool Nullable { get; init; } = true;
            public string? Warning { get; init; }
        }

        /// <summary>Suy luận field từ một document mẫu (đệ quy 1 cấp cho kiểu lồng).</summary>
        public static IReadOnlyList<FieldSpec> InferFields(
            IReadOnlyList<BsonDocument> sample, out List<string> warnings)
        {
            warnings = new List<string>();
            var order = new List<string>();
            var seen = new Dictionary<string, (HashSet<CanonicalType> Types, int MaxLen, int Present)>(
                StringComparer.Ordinal);
            for (var i = 0; i < sample.Count; i++)
            {
                foreach (var element in sample[i].Elements)
                {
                    if (!seen.TryGetValue(element.Name, out var entry))
                    {
                        entry = (new HashSet<CanonicalType>(), 0, 0);
                        seen[element.Name] = entry;
                        order.Add(element.Name);
                    }
                    var t = MapBsonType(element.Value, out var len);
                    entry.Types.Add(t);
                    if (len > entry.MaxLen)
                        seen[element.Name] = (entry.Types, len, entry.Present + 1);
                    else
                        seen[element.Name] = (entry.Types, entry.MaxLen, entry.Present + 1);
                }
            }
            var result = new List<FieldSpec>();
            foreach (var name in order)
            {
                var entry = seen[name];
                CanonicalType type;
                string? warning = null;
                if (entry.Types.Count > 1)
                {
                    type = CanonicalType.String;
                    warning = $"Field '{name}' kiểu hỗn hợp ({string.Join("/", entry.Types)}) — chép dạng chuỗi.";
                    if (!warnings.Contains(warning))
                        warnings.Add(warning);
                }
                else
                {
                    type = entry.Types.First();
                }
                if (type == CanonicalType.Json)
                {
                    warning = $"Field '{name}' lồng nhau (document/array) — lưu JSON text.";
                    if (!warnings.Contains(warning))
                        warnings.Add(warning);
                }
                result.Add(new FieldSpec
                {
                    Name = name,
                    Type = type,
                    MaxLength = type == CanonicalType.String
                        ? (entry.MaxLen > 0 && entry.MaxLen <= 4000 ? entry.MaxLen : -1)
                        : 0,
                    Nullable = entry.Present < sample.Count,
                    Warning = warning
                });
            }
            return result;
        }

        internal static CanonicalType MapBsonType(BsonValue value, out int length)
        {
            length = 0;
            if (value == null || value.IsBsonNull || value.IsBsonUndefined)
                return CanonicalType.String;
            switch (value.BsonType)
            {
                case BsonType.ObjectId:
                    length = 24;
                    return CanonicalType.String;
                case BsonType.String:
                    length = value.AsString.Length;
                    return CanonicalType.String;
                case BsonType.Int32:
                    return CanonicalType.Int32;
                case BsonType.Int64:
                    return CanonicalType.Int64;
                case BsonType.Double:
                    return CanonicalType.Double;
                case BsonType.Decimal128:
                    return CanonicalType.Decimal;
                case BsonType.Boolean:
                    return CanonicalType.Bool;
                case BsonType.DateTime:
                    return CanonicalType.DateTime;
                case BsonType.Binary:
                    return CanonicalType.Binary;
                case BsonType.Document:
                case BsonType.Array:
                    return CanonicalType.Json;
                default:
                    return CanonicalType.String;
            }
        }

        /// <summary>
        /// Chuyển một document thành dòng theo thứ tự cột canonical (thiếu field → null).
        /// ObjectId/date/guid giữ dạng BSON gốc để endpoint đích tự chuyển.
        /// </summary>
        public static object?[] ToRow(BsonDocument doc, IReadOnlyList<CanonicalColumn> columns)
        {
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                if (!doc.TryGetValue(columns[i].Name, out var value)
                    || value.IsBsonNull || value.IsBsonUndefined)
                {
                    row[i] = null;
                    continue;
                }
                row[i] = value.BsonType switch
                {
                    BsonType.ObjectId => value.AsObjectId.ToString(),
                    BsonType.DateTime => value.ToUniversalTime(),
                    BsonType.Document or BsonType.Array => value.ToJson(),
                    BsonType.Binary => value.AsBsonBinaryData.Bytes,
                    BsonType.Decimal128 => value.AsDecimal,
                    BsonType.Int32 => value.AsInt32,
                    BsonType.Int64 => value.AsInt64,
                    BsonType.Double => value.AsDouble,
                    BsonType.Boolean => value.AsBoolean,
                    _ => value.ToString()
                };
            }
            return row;
        }

        /// <summary>
        /// Chuyển một dòng canonical thành document MongoDB (cột "_id"/"id" đơn
        /// làm _id nếu có, còn lại giữ tên cột). Null thì bỏ field.
        /// </summary>
        public static BsonDocument ToDocument(
            IReadOnlyList<CanonicalColumn> columns, object?[] row, string? idColumn)
        {
            var doc = new BsonDocument();
            for (var i = 0; i < columns.Count && i < row.Length; i++)
            {
                var value = row[i];
                if (value == null)
                    continue;
                BsonValue bson = value switch
                {
                    Guid g => new BsonString(g.ToString("D")),
                    DateTimeOffset dto => new BsonDateTime(dto.UtcDateTime),
                    DateTime dt => new BsonDateTime(dt.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
                    DateOnly d => new BsonDateTime(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
                    TimeOnly t => new BsonString(t.ToString("HH:mm:ss.fffffff")),
                    TimeSpan ts => new BsonString(ts.ToString("c")),
                    bool b => new BsonBoolean(b),
                    int n => new BsonInt32(n),
                    long n => new BsonInt64(n),
                    short n => new BsonInt32(n),
                    byte n => new BsonInt32(n),
                    decimal n => new BsonDecimal128(n),
                    double n => new BsonDouble(n),
                    float n => new BsonDouble(n),
                    byte[] bytes => new BsonBinaryData(bytes),
                    _ => new BsonString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                };
                if (!string.IsNullOrEmpty(idColumn)
                    && columns[i].Name.Equals(idColumn, StringComparison.OrdinalIgnoreCase))
                    doc.Set("_id", bson);
                else if (columns[i].Name != "_id" || !doc.Contains("_id"))
                    doc.Set(columns[i].Name, bson);
            }
            return doc;
        }
    }
}
