using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 重试辅助类
    /// </summary>
    public static class RetryHelper
    {
        /// <summary>
        /// 执行带重试的操作
        /// </summary>
        /// <typeparam name="T">返回类型</typeparam>
        /// <param name="operation">要执行的操作</param>
        /// <param name="maxRetries">最大重试次数</param>
        /// <param name="delaySeconds">初始延迟秒数</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="operationName">操作名称（用于日志）</param>
        /// <returns>操作结果</returns>
        public static async Task<T> RetryAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 3,
            int delaySeconds = 2,
            ILogger? logger = null,
            string? operationName = null)
        {
            int attempt = 0;
            Exception? lastException = null;

            while (attempt < maxRetries)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < maxRetries - 1 && IsRetryableException(ex))
                {
                    attempt++;
                    lastException = ex;
                    
                    // 指数退避：延迟时间 = 初始延迟 * 2^(尝试次数-1)
                    var delay = TimeSpan.FromSeconds(delaySeconds * Math.Pow(2, attempt - 1));
                    
                    logger?.LogWarning(ex, 
                        "{OperationName} 失败，第 {Attempt} 次重试，延迟 {Delay} 秒", 
                        operationName ?? "操作", attempt, delay.TotalSeconds);
                    
                    await Task.Delay(delay);
                }
            }

            logger?.LogError(lastException, 
                "{OperationName} 重试 {MaxRetries} 次后仍然失败", 
                operationName ?? "操作", maxRetries);
            
            throw new Exception(
                $"{operationName ?? "操作"} 在重试 {maxRetries} 次后仍然失败", 
                lastException);
        }

        /// <summary>
        /// 执行带重试的操作（无返回值）
        /// </summary>
        public static async Task RetryAsync(
            Func<Task> operation,
            int maxRetries = 3,
            int delaySeconds = 2,
            ILogger? logger = null,
            string? operationName = null)
        {
            await RetryAsync<object?>(async () =>
            {
                await operation();
                return null;
            }, maxRetries, delaySeconds, logger, operationName);
        }

        /// <summary>
        /// 判断异常是否可重试
        /// </summary>
        private static bool IsRetryableException(Exception ex)
        {
            // 网络相关异常可重试
            if (ex is System.Net.Http.HttpRequestException ||
                ex is System.Net.Sockets.SocketException ||
                ex is TimeoutException ||
                ex is System.IO.IOException)
            {
                return true;
            }

            // SQL Server连接异常可重试
            if (ex is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                // 连接超时、网络相关错误可重试
                var retryableErrors = new[] { -2, 2, 53, 121, 10053, 10054, 10060 };
                return Array.Exists(retryableErrors, code => sqlEx.Number == code);
            }

            return false;
        }
    }
}

