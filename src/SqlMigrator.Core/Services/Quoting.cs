using System.Text;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Safe identifier quoting. Never concatenate identifiers into SQL directly;
    /// always route them through these helpers (the T-SQL equivalent of QUOTENAME).
    /// </summary>
    public static class Quoting
    {
        /// <summary>Quotes a single identifier: dbo -> [dbo], str]ange -> [str]]ange.</summary>
        public static string QuoteIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                throw new System.ArgumentException("Identifier cannot be null or empty.", nameof(identifier));

            var sb = new StringBuilder(identifier.Length + 2);
            sb.Append('[');
            foreach (var ch in identifier)
            {
                if (ch == ']')
                    sb.Append("]]");
                else
                    sb.Append(ch);
            }

            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Quotes "schema"."name": schema can be empty for object-level names.</summary>
        public static string QuoteQualifiedName(string? schema, string name)
        {
            var qualified = QuoteIdentifier(name);
            if (!string.IsNullOrEmpty(schema))
                qualified = QuoteIdentifier(schema!) + "." + qualified;
            return qualified;
        }
    }

    /// <summary>
    /// Central place for opening <c>Microsoft.Data.SqlClient</c> connections.
    /// Kept separate so services can share connection-string logic consistently.
    /// </summary>
    public static class SqlConnectionFactory
    {
        public static Microsoft.Data.SqlClient.SqlConnection Open(
            string connectionString,
            int timeoutSeconds,
            System.Threading.CancellationToken ct = default)
        {
            var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var open = connection.OpenAsync(ct);
                open.GetAwaiter().GetResult();
            }

            return connection;
        }

        /// <summary>
        /// Mở kết nối có "auto-fallback" để tương thích SQL Server 2008 → 2025:
        ///  1) thử đúng chuỗi kết nối người dùng cấu hình;
        ///  2) nếu lỗi chứng chỉ không được tin tưởng (server tự ký/không thuộc CA) →
        ///     thử lại với TrustServerCertificate=True;
        ///  3) nếu server quá cũ không hỗ trợ TLS (.NET 8 tắt TLS 1.0/1.1) →
        ///     thử lại với Encrypt=False (mạng nội bộ).
        /// Mỗi bước fallback ghi chú vào <paramref name="log"/> (nếu có) để người dùng biết.
        /// Không bao giờ in chuỗi kết nối hay mật khẩu.
        /// </summary>
        public static Microsoft.Data.SqlClient.SqlConnection OpenWithFallback(
            string connectionString,
            int timeoutSeconds,
            System.Action<string>? log = null,
            System.Threading.CancellationToken ct = default)
        {
            return TryOpen(connectionString, timeoutSeconds, log, ct);
        }

        /// <summary>
        /// Trả về chuỗi kết nối "đã điều chỉnh" phù hợp với server (tự quyết định có cần
        /// TrustServerCertificate / Encrypt=False hay không) để các thành phần di chuyển
        /// (SchemaExtractor, DataCopier, v.v.) mở kết nối thành công ngay từ lần đầu,
        /// tránh lỗi chứng chỉ tự ký hoặc TLS thiếu ở SQL 2008 → 2025.
        /// Nếu kết nối gốc thành công thường trả về chuỗi gốc nguyên vẹn.
        /// Log quyết định fallback (nếu có) qua <paramref name="log"/>, không in bí mật.
        /// </summary>
        public static string NormalizeConnectionStringWithFallback(
            string connectionString,
            int timeoutSeconds,
            System.Action<string>? log = null,
            System.Threading.CancellationToken ct = default)
        {
            try
            {
                using (Open(connectionString, timeoutSeconds, ct)) { }
                return connectionString;
            }
            catch (System.Exception ex) when (IsCertificateTrustError(ex))
            {
                log?.Invoke("[THÔNG TIN] Server dùng chứng chỉ tự ký — điều chỉnh chuỗi kết nối với TrustServerCertificate=True.");

                var trustCs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
                {
                    TrustServerCertificate = true
                }.ConnectionString;
                try
                {
                    using (Open(trustCs, timeoutSeconds, ct)) { }
                    return trustCs;
                }
                catch (System.Exception trustEx) when (IsLegacyTlsError(trustEx))
                {
                    return DropEncryption(connectionString, timeoutSeconds, log, ct, trustEx);
                }
            }
            catch (System.Exception ex) when (IsLegacyTlsError(ex))
            {
                return DropEncryption(connectionString, timeoutSeconds, log, ct, ex);
            }
        }

        private static string DropEncryption(
            string connectionString, int timeoutSeconds, System.Action<string>? log,
            System.Threading.CancellationToken ct, System.Exception original)
        {
            log?.Invoke("[THÔNG TIN] Server SQL cũ không hỗ trợ TLS hiện tại — điều chỉnh chuỗi kết nối Encrypt=False (kết nối nội bộ, không mã hóa).");

            var plainCs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
            {
                TrustServerCertificate = true,
                Encrypt = false
            }.ConnectionString;
            try
            {
                using (Open(plainCs, timeoutSeconds, ct)) { }
                log?.Invoke("[CẢNH BÁO] Toàn bộ lộ trình di chuyển sẽ chạy KHÔNG mã hóa (server quá cũ). Khuyến nghị chỉ dùng trong mạng nội bộ tin cậy.");
                return plainCs;
            }
            catch (System.Exception plainEx)
            {
                throw new System.Exception("Không kết nối được ngay cả khi không mã hóa: " + plainEx.Message, plainEx);
            }
        }

        /// <summary>Nhận diện lỗi SSL do chứng chỉ server không được hệ thống tin tưởng.</summary>
        public static bool IsCertificateTrustError(System.Exception ex)
        {
            var m = ex.Message;
            return m.Contains("authority that is not trusted", System.StringComparison.OrdinalIgnoreCase)
                || m.Contains("certificate chain", System.StringComparison.OrdinalIgnoreCase)
                || m.Contains("SSL Provider", System.StringComparison.OrdinalIgnoreCase)
                || m.IndexOf("TrustServerCertificate", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Nhận diện lỗi do server cũ không hỗ trợ TLS phiên bản mà .NET 8 mặc định dùng.</summary>
        public static bool IsLegacyTlsError(System.Exception ex)
        {
            var m = ex.Message;
            return m.Contains("SSL", System.StringComparison.OrdinalIgnoreCase)
                || m.Contains("TLS", System.StringComparison.OrdinalIgnoreCase)
                || m.Contains("protocol", System.StringComparison.OrdinalIgnoreCase)
                || m.Contains("decrypt", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Che mật khẩu trong thông điệp lỗi để không bao giờ lộ ra log hoặc giao diện.</summary>
        public static string ScrubConnectionStringSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(?i)(Password|Pwd)\s*=\s*[^;\r\n]*",
                "<mật khẩu đã bị che>");
        }

        /// <summary>
        /// So sánh 2 chuỗi kết nối có trỏ cùng một database trên cùng một server không.
        /// Dùng làm chốt chặn an toàn: nguồn và đích không bao giờ được trùng nhau
        /// (tránh app ghi đè lên nguồn). So sánh chuẩn hóa DataSource + InitialCatalog.
        /// Lưu ý: alias server (".", "localhost", tên máy) không được phân giải —
        /// chỉ bắt được trùng lặp tường minh.
        /// </summary>
        public static bool IsSameDatabase(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            try
            {
                var ba = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(a);
                var bb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(b);
                return string.Equals(NormalizeServer(ba.DataSource), NormalizeServer(bb.DataSource),
                           System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals((ba.InitialCatalog ?? "").Trim(),
                           (bb.InitialCatalog ?? "").Trim(),
                           System.StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(ba.InitialCatalog);
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeServer(string dataSource)
        {
            var s = (dataSource ?? "").Trim().TrimEnd('\\');
            // Gộp các cách viết localhost phổ biến.
            if (s.Equals(".", System.StringComparison.Ordinal)
                || s.Equals("(local)", System.StringComparison.OrdinalIgnoreCase)
                || s.Equals("localhost", System.StringComparison.OrdinalIgnoreCase))
                return "localhost";
            return s;
        }

        private static Microsoft.Data.SqlClient.SqlConnection TryOpen(
            string connectionString, int timeoutSeconds, System.Action<string>? log,
            System.Threading.CancellationToken ct)
        {
            try
            {
                return Open(connectionString, timeoutSeconds, ct);
            }
            catch (System.Exception ex) when (IsCertificateTrustError(ex))
            {
                log?.Invoke("[THÔNG TIN] Server dùng chứng chỉ tự ký — đang thử lại với TrustServerCertificate=True...");

                var trustCs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
                {
                    TrustServerCertificate = true
                }.ConnectionString;
                try
                {
                    var conn = Open(trustCs, timeoutSeconds, ct);
                    log?.Invoke("[THÔNG TIN] Kết nối thành công sau khi chấp nhận chứng chỉ tự ký của server.");
                    return conn;
                }
                catch (System.Exception trustEx) when (IsLegacyTlsError(trustEx))
                {
                    return OpenPlainTextFallback(connectionString, timeoutSeconds, log, ct, trustEx);
                }
            }
            catch (System.Exception ex) when (IsLegacyTlsError(ex))
            {
                return OpenPlainTextFallback(connectionString, timeoutSeconds, log, ct, ex);
            }
        }

        private static Microsoft.Data.SqlClient.SqlConnection OpenPlainTextFallback(
            string connectionString, int timeoutSeconds, System.Action<string>? log,
            System.Threading.CancellationToken ct, System.Exception original)
        {
            log?.Invoke("[THÔNG TIN] Server SQL cũ không hỗ trợ TLS hiện tại — thử lại với Encrypt=False (kết nối nội bộ, không mã hóa)...");

            var plainCs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
            {
                TrustServerCertificate = true,
                Encrypt = false
            }.ConnectionString;
            try
            {
                var conn = Open(plainCs, timeoutSeconds, ct);
                log?.Invoke("[CẢNH BÁO] Đã kết nối KHÔNG mã hóa (server quá cũ). Khuyến nghị chỉ dùng trong mạng nội bộ tin cậy.");
                return conn;
            }
            catch (System.Exception plainEx)
            {
                throw new System.Exception("Không kết nối được ngay cả khi không mã hóa: " + plainEx.Message, plainEx);
            }
        }
    }
}