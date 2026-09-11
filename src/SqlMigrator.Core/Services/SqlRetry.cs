using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Retry cho các lỗi kết nối tạm thời (timeout, mất kết nối giữa chừng...) giữa các
    /// tầng của ứng dụng: preflight, chunk transfer. Không retry lỗi dữ liệu (sai cú pháp,
    /// vi phạm khóa...).
    /// </summary>
    public static class SqlRetry
    {
        private static readonly int[] TransientErrorNumbers =
        {
            -2,      // timeout
            20,      // instance does not exist / không mở nổi
            53,      // không kết nối được
            64,      // network name not available
            10054,   // connection reset
            10060,   // connection timeout
            10061,   // connection refused
            4060,    // database chưa sẵn sàng
            1205,    // deadlock
            -1
        };

        public static bool IsTransient(Exception ex)
        {
            if (ex is not SqlException sqlEx)
                return ex is TimeoutException;
            return TransientErrorNumbers.Contains(Math.Abs(sqlEx.Number))
                   || sqlEx.Message.Contains("RECEIVE_TIMEOUT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Thực thi <paramref name="action"/> với tối đa <paramref name="maxAttempts"/> lần thử,
        /// chỉ thử lại khi gặp lỗi tạm thời; nghỉ giữa các lần theo backoff (800ms → 1600ms → …).
        /// </summary>
        public static async Task WithRetryAsync(Func<CancellationToken, Task> action, ILogger logger,
            string description, CancellationToken ct, int maxAttempts = 3)
        {
            await WithRetryAsync(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, logger, description, ct, maxAttempts).ConfigureAwait(false);
        }

        /// <summary>
        /// Thực thi <paramref name="action"/> với tối đa <paramref name="maxAttempts"/> lần thử,
        /// chỉ thử lại khi gặp lỗi tạm thời; nghỉ giữa các lần theo backoff (800ms → 1600ms → …).
        /// </summary>
        public static async Task<T> WithRetryAsync<T>(
            Func<CancellationToken, Task<T>> action,
            ILogger logger,
            string description,
            CancellationToken ct,
            int maxAttempts = 3)
        {
            var delayMs = 800;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await action(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
                {
                    logger.LogWarning(
                        "{Description} gặp lỗi tạm thời (lần {Attempt}/{Max}); thử lại sau {Delay}ms. {Message}",
                        description, attempt, maxAttempts, delayMs, ex.Message);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs *= 2;
                }
            }
        }
    }
}