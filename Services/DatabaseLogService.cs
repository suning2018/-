using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 数据库日志服务
    /// </summary>
    public class DatabaseLogService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DatabaseLogService> _logger;
        private readonly string _connectionString;

        public DatabaseLogService(IConfiguration configuration, ILogger<DatabaseLogService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("SQLServer") ?? throw new InvalidOperationException("数据库连接字符串未配置");
            // InitializeLogTable(); // 已禁用：表已提前创建，不需要自动创建
        }


        /// <summary>
        /// 记录日志到数据库
        /// </summary>
        public async Task LogAsync(
            LogLevel logLevel,
            string message,
            string? category = null,
            Exception? exception = null,
            string? source = null,
            string? fileName = null,
            string? operation = null,
            string? userId = null,
            string? additionalData = null)
        {
            try
            {
                var insertSql = @"
                    INSERT INTO [dbo].[SystemLog] 
                    (LogTime, LogLevel, Category, Message, Exception, Source, FileName, Operation, UserId, AdditionalData)
                    VALUES 
                    (@LogTime, @LogLevel, @Category, @Message, @Exception, @Source, @FileName, @Operation, @UserId, @AdditionalData)
                ";

                var parameters = new Dictionary<string, object>
                {
                    { "@LogTime", DateTime.Now },
                    { "@LogLevel", logLevel.ToString() },
                    { "@Category", category ?? string.Empty },
                    { "@Message", message ?? string.Empty },
                    { "@Exception", exception?.ToString() ?? string.Empty },
                    { "@Source", source ?? string.Empty },
                    { "@FileName", fileName ?? string.Empty },
                    { "@Operation", operation ?? string.Empty },
                    { "@UserId", userId ?? string.Empty },
                    { "@AdditionalData", additionalData ?? string.Empty }
                };

                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(insertSql, connection);
                
                foreach (var param in parameters)
                {
                    command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                }

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // 如果数据库日志失败，只记录到文件日志，避免循环
                _logger.LogError(ex, "写入数据库日志失败: {Message}", message);
            }
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public async Task LogInformationAsync(string message, string? category = null, string? source = null, string? fileName = null, string? operation = null)
        {
            await LogAsync(LogLevel.Information, message, category, null, source, fileName, operation);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public async Task LogWarningAsync(string message, string? category = null, string? source = null, string? fileName = null, string? operation = null)
        {
            await LogAsync(LogLevel.Warning, message, category, null, source, fileName, operation);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public async Task LogErrorAsync(string message, Exception? exception = null, string? category = null, string? source = null, string? fileName = null, string? operation = null)
        {
            await LogAsync(LogLevel.Error, message, category, exception, source, fileName, operation);
        }

        /// <summary>
        /// 清理旧日志
        /// </summary>
        public async Task CleanOldLogsAsync(int retainDays)
        {
            try
            {
                var deleteSql = @"
                    DELETE FROM [dbo].[SystemLog]
                    WHERE LogTime < DATEADD(DAY, -@RetainDays, GETDATE())
                ";

                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var command = new SqlCommand(deleteSql, connection);
                command.Parameters.AddWithValue("@RetainDays", retainDays);

                var deletedCount = await command.ExecuteNonQueryAsync();
                _logger.LogInformation("清理了 {Count} 条超过 {Days} 天的数据库日志", deletedCount, retainDays);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理旧日志失败");
            }
        }
    }
}

