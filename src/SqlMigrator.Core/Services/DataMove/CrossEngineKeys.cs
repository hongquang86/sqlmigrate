using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>Chuẩn hóa khóa keyset thành chuỗi trung tính và ngược lại (mọi engine).</summary>
    internal static class CrossEngineKeys
    {
        public static string Serialize(object? value)
        {
            return value switch
            {
                null => "",
                long l => l.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                short s => s.ToString(CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };
        }
    }
}
