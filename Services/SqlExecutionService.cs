using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// SQL执行服务 - 通用的SQL执行服务
    /// 功能：
    /// 1. 自动执行SqlExecutionConfig表中待执行的SQL（支持所有类型，包括手动插入的）
    /// 2. 手动添加SQL配置到数据库（供其他模块调用）
    /// 3. 直接执行SQL（带验证和日志记录）
    /// </summary>
    public class SqlExecutionService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SqlExecutionService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly string _connectionString;

        public SqlExecutionService(
            IConfiguration configuration,
            ILogger<SqlExecutionService> logger,
            DatabaseLogService? databaseLogService = null)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseLogService = databaseLogService;
            _connectionString = configuration.GetConnectionString("SQLServer")
                ?? throw new InvalidOperationException("数据库连接字符串未配置");
        }

        /// <summary>
        /// 验证SQL语句（完整校验）
        /// </summary>
        public async Task<(bool IsValid, string ErrorMessage)> ValidateSqlAsync(string sql, Dictionary<string, object?>? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return (false, "SQL语句不能为空");
            }

            try
            {
                // 1. 基本格式检查
                var trimmedSql = sql.Trim();
                if (trimmedSql.Length == 0)
                {
                    return (false, "SQL语句不能为空");
                }

                // 2. 检查SQL是否包含危险操作
                var upperSql = trimmedSql.ToUpper();
                var dangerousKeywords = new[] { "DROP", "TRUNCATE", "ALTER", "CREATE", "EXEC", "EXECUTE" };
                
                foreach (var keyword in dangerousKeywords)
                {
                    if (upperSql.Contains(keyword))
                    {
                        // 禁止的危险操作
                        return (false, $"SQL包含禁止的危险关键字: {keyword}");
                    }
                }

                // 3. 检查SQL类型（允许UPDATE、INSERT、DELETE）
                var isDmlStatement = upperSql.StartsWith("UPDATE") || upperSql.StartsWith("INSERT") || upperSql.StartsWith("DELETE") || upperSql.StartsWith("MERGE");
                
                if (!isDmlStatement)
                {
                    return (false, "只允许执行UPDATE、INSERT、DELETE或MERGE语句");
                }

                // 4. UPDATE和DELETE语句必须包含WHERE条件
                if ((upperSql.StartsWith("UPDATE") || upperSql.StartsWith("DELETE")) && !upperSql.Contains("WHERE"))
                {
                    return (false, "UPDATE和DELETE语句必须包含WHERE条件，以防止误操作所有记录");
                }

                // 5. 对于DML语句，检查是否包含行数限制机制（TOP或SET ROWCOUNT）
                // 注意：即使SQL中没有，执行时也会通过SET ROWCOUNT 10来限制，所以这里只是警告性检查
                if (isDmlStatement)
                {
                    // 检查是否包含 TOP (N) 或 SET ROWCOUNT N（可选，因为执行时会强制设置）
                    var hasTopLimit = Regex.IsMatch(upperSql, @"TOP\s*\(\s*\d+\s*\)", RegexOptions.IgnoreCase);
                    var hasRowCountLimit = Regex.IsMatch(upperSql, @"SET\s+ROWCOUNT\s+\d+", RegexOptions.IgnoreCase);
                    
                    // 如果SQL中没有行数限制，记录警告但不阻止（因为执行时会强制设置SET ROWCOUNT 10）
                    if (!hasTopLimit && !hasRowCountLimit)
                    {
                        _logger.LogDebug("SQL语句未包含行数限制（TOP或SET ROWCOUNT），执行时将自动设置SET ROWCOUNT 10");
                    }
                }

                // 6. 语法验证（使用SET PARSEONLY ON）
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                try
                {
                    // 设置只解析模式
                    using var setCommand = new SqlCommand("SET PARSEONLY ON", connection);
                    await setCommand.ExecuteNonQueryAsync();

                    try
                    {
                        // 尝试解析SQL（带参数）
                        using var validateCommand = new SqlCommand(sql, connection);
                        
                        // 添加参数（如果提供）
                        if (parameters != null)
                        {
                            foreach (var param in parameters)
                            {
                                validateCommand.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                            }
                        }

                        await validateCommand.ExecuteNonQueryAsync();
                        
                        // 重置解析模式
                        using var resetCommand = new SqlCommand("SET PARSEONLY OFF", connection);
                        await resetCommand.ExecuteNonQueryAsync();
                        
                        return (true, string.Empty);
                    }
                    catch (SqlException ex)
                    {
                        // 重置解析模式
                        try
                        {
                            using var resetCommand = new SqlCommand("SET PARSEONLY OFF", connection);
                            await resetCommand.ExecuteNonQueryAsync();
                        }
                        catch { }
                        
                        return (false, $"SQL语法错误: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"SQL验证过程出错: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"SQL验证失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 手动添加SQL配置到数据库（通用方法，供其他模块调用）
        /// 注意：所有SQL都必须通过验证才能添加
        /// </summary>
        /// <param name="configName">配置名称</param>
        /// <param name="sqlStatement">SQL语句</param>
        /// <param name="sqlType">SQL类型（如'Manual', 'Mapping', 'Scheduled'等）</param>
        /// <param name="parameters">SQL参数（JSON格式字符串，可选）</param>
        /// <param name="description">描述（可选）</param>
        /// <param name="executionOrder">执行顺序（可选，默认0）</param>
        /// <returns>返回插入的配置ID，失败返回-1</returns>
        public async Task<int> AddSqlConfigAsync(
            string configName,
            string sqlStatement,
            string sqlType = "Manual",
            string? parameters = null,
            string? description = null,
            int executionOrder = 0)
        {
            if (string.IsNullOrWhiteSpace(configName))
            {
                throw new ArgumentException("配置名称不能为空", nameof(configName));
            }

            if (string.IsNullOrWhiteSpace(sqlStatement))
            {
                throw new ArgumentException("SQL语句不能为空", nameof(sqlStatement));
            }

            // 强制验证SQL（必须通过验证才能添加）
            Dictionary<string, object?>? paramDict = null;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                try
                {
                    paramDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(parameters);
                }
                catch
                {
                    _logger.LogWarning("参数JSON格式无效，将使用空参数进行验证");
                }
            }

            var (isValid, errorMessage) = await ValidateSqlAsync(sqlStatement, paramDict);
            if (!isValid)
            {
                throw new InvalidOperationException($"SQL验证失败: {errorMessage}");
            }

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO SqlExecutionConfig 
                (ConfigName, SqlType, SqlStatement, Parameters, Description, 
                 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
                VALUES 
                (@ConfigName, @SqlType, @SqlStatement, @Parameters, @Description,
                 1, @ExecutionOrder, 1, GETDATE(), GETDATE());
                SELECT CAST(SCOPE_IDENTITY() AS INT);";

            try
            {
                using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@ConfigName", configName);
                command.Parameters.AddWithValue("@SqlType", sqlType);
                command.Parameters.AddWithValue("@SqlStatement", sqlStatement);
                command.Parameters.AddWithValue("@Parameters", (object?)parameters ?? DBNull.Value);
                command.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
                command.Parameters.AddWithValue("@ExecutionOrder", executionOrder);

                var result = await command.ExecuteScalarAsync();
                var configId = result != null ? Convert.ToInt32(result) : -1;

                _logger.LogInformation("成功添加SQL配置: {ConfigName} (ID: {ConfigId}, Type: {SqlType})", 
                    configName, configId, sqlType);

                return configId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加SQL配置失败: {ConfigName}", configName);
                throw;
            }
        }

        /// <summary>
        /// 执行指定的SQL配置
        /// </summary>
        public async Task<(bool Success, string Message, int RowsAffected)> ExecuteSqlConfigByIdAsync(
            int configId,
            Dictionary<string, object?>? parameters = null)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var config = await GetSqlConfigByIdAsync(configId, connection, null);
            if (config == null)
            {
                return (false, "SQL配置不存在", 0);
            }

            var sql = ReplaceSqlPlaceholders(config.SqlStatement, null, null, parameters);

            // 强制验证SQL（所有SQL都必须通过验证才能执行）
            var (isValid, errorMessage) = await ValidateSqlAsync(sql);
            if (!isValid)
            {
                return (false, $"SQL验证失败: {errorMessage}", 0);
            }

            return await ExecuteSqlConfigAsync(config, sql, parameters, null, connection, null);
        }

        /// <summary>
        /// 执行SQL配置
        /// </summary>
        private async Task<(bool Success, string Message, int RowsAffected)> ExecuteSqlConfigAsync(
            SqlExecutionConfigModel config,
            string sql,
            Dictionary<string, object?>? parameters,
            string? sourceFileName,
            SqlConnection connection,
            SqlTransaction? transaction)
        {
            var stopwatch = Stopwatch.StartNew();
            var rowsAffected = 0;
            var isSuccess = false;
            var errorMessage = string.Empty;
            var rowCountLimitSet = false;

            try
            {
                // 检查是否为DML语句（UPDATE、INSERT、DELETE）
                var upperSql = sql.Trim().ToUpper();
                var isDmlStatement = upperSql.StartsWith("UPDATE") || upperSql.StartsWith("INSERT") || 
                                    upperSql.StartsWith("DELETE") || upperSql.StartsWith("MERGE");

                // 对于DML语句，设置SET ROWCOUNT限制为10行
                if (isDmlStatement)
                {
                    await SetRowCountLimitAsync(connection, transaction, 10);
                    rowCountLimitSet = true;
                    _logger.LogDebug("已设置SET ROWCOUNT 10限制，防止影响超过10行数据");
                }

                using var command = new SqlCommand(sql, connection, transaction);

                // 添加参数
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }

                // 解析配置中的参数定义
                if (!string.IsNullOrWhiteSpace(config.Parameters))
                {
                    try
                    {
                        var paramDefs = JsonSerializer.Deserialize<Dictionary<string, string>>(config.Parameters);
                        if (paramDefs != null)
                        {
                            foreach (var paramDef in paramDefs)
                            {
                                if (!command.Parameters.Contains(paramDef.Key))
                                {
                                    // 从参数中获取值，如果没有则使用默认值
                                    var paramValue = parameters?.GetValueOrDefault(paramDef.Key);
                                    if (paramValue == null && !string.IsNullOrWhiteSpace(paramDef.Value))
                                    {
                                        paramValue = paramDef.Value;
                                    }
                                    command.Parameters.AddWithValue(paramDef.Key, paramValue ?? DBNull.Value);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析参数定义失败: {ConfigName}", config.ConfigName);
                    }
                }

                rowsAffected = await command.ExecuteNonQueryAsync();
                
                stopwatch.Stop();
                
                // 如果受影响行数为0，不标记为执行成功（需要重复执行）
                if (rowsAffected == 0)
                {
                    isSuccess = false;
                    errorMessage = "受影响行数为0，需要重复执行";
                    _logger.LogWarning(
                        "SQL执行完成但受影响行数为0: {ConfigName}, 耗时: {Duration}ms，将标记为需要重复执行",
                        config.ConfigName, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    isSuccess = true;
                    _logger.LogInformation(
                        "SQL执行成功: {ConfigName}, 影响行数: {RowsAffected}, 耗时: {Duration}ms",
                        config.ConfigName, rowsAffected, stopwatch.ElapsedMilliseconds);
                }

                // 更新配置的执行信息（受影响行数为0时不标记为成功）
                await UpdateSqlConfigExecutionInfoAsync(config.Id, isSuccess, rowsAffected, connection, transaction);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                isSuccess = false;
                errorMessage = ex.Message;
                rowsAffected = 0;

                _logger.LogError(ex, "SQL执行失败: {ConfigName}", config.ConfigName);
                await UpdateSqlConfigExecutionInfoAsync(config.Id, false, 0, connection, transaction);
            }
            finally
            {
                // 重置SET ROWCOUNT（如果之前设置了）
                if (rowCountLimitSet)
                {
                    await ResetRowCountLimitAsync(connection, transaction);
                    _logger.LogDebug("已重置SET ROWCOUNT限制");
                }

                stopwatch.Stop();
                // 记录执行日志
                await LogSqlExecutionAsync(
                    config.Id,
                    config.ConfigName,
                    sql,
                    parameters,
                    isSuccess,
                    errorMessage,
                    sourceFileName,
                    connection,
                    transaction,
                    stopwatch.ElapsedMilliseconds,
                    rowsAffected);
            }

            return (isSuccess, errorMessage, rowsAffected);
        }

        /// <summary>
        /// 设置SET ROWCOUNT限制
        /// </summary>
        private async Task SetRowCountLimitAsync(SqlConnection connection, SqlTransaction? transaction, int limit)
        {
            var sql = $"SET ROWCOUNT {limit}";
            using var command = new SqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 重置SET ROWCOUNT
        /// </summary>
        private async Task ResetRowCountLimitAsync(SqlConnection connection, SqlTransaction? transaction)
        {
            var sql = "SET ROWCOUNT 0";
            using var command = new SqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }


        /// <summary>
        /// 获取待执行的SQL配置列表
        /// 包括：从未执行过的、3天内未执行的、受影响行数为0需要重复执行的
        /// </summary>
        public async Task<List<(int Id, string ConfigName, string SqlStatement, string? Parameters)>> GetPendingSqlConfigsAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT c.Id, c.ConfigName, c.SqlStatement, c.Parameters
                FROM SqlExecutionConfig c
                LEFT JOIN (
                    SELECT ConfigId, RowsAffected, ExecutionTime,
                           ROW_NUMBER() OVER (PARTITION BY ConfigId ORDER BY ExecutionTime DESC) as rn
                    FROM SqlExecutionLog
                    WHERE IsSuccess = 1
                ) log ON c.Id = log.ConfigId AND log.rn = 1
                WHERE c.IsActive = 1
                  AND (
                      c.LastExecuteTime IS NULL 
                      OR c.LastExecuteTime < DATEADD(day, -3, GETDATE())
                      OR (log.RowsAffected = 0 AND log.ExecutionTime >= DATEADD(day, -3, GETDATE()))
                  )
                ORDER BY c.ExecutionOrder, c.Id";

            var sqlConfigs = new List<(int Id, string ConfigName, string SqlStatement, string? Parameters)>();

            using var command = new SqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sqlConfigs.Add((
                    reader.GetInt32(reader.GetOrdinal("Id")),
                    reader.GetString(reader.GetOrdinal("ConfigName")),
                    reader.GetString(reader.GetOrdinal("SqlStatement")),
                    reader.IsDBNull(reader.GetOrdinal("Parameters")) ? null : reader.GetString(reader.GetOrdinal("Parameters"))
                ));
            }

            return sqlConfigs;
        }

        /// <summary>
        /// 根据ID获取SQL配置
        /// </summary>
        private async Task<SqlExecutionConfigModel?> GetSqlConfigByIdAsync(
            int configId,
            SqlConnection connection,
            SqlTransaction? transaction)
        {
            var sql = @"
                SELECT Id, ConfigName, SqlType, SqlStatement, Parameters, Description,
                       ExecutionOrder, ValidationEnabled
                FROM SqlExecutionConfig
                WHERE Id = @Id AND IsActive = 1";

            try
            {
                using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@Id", configId);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new SqlExecutionConfigModel
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("Id")),
                        ConfigName = reader.GetString(reader.GetOrdinal("ConfigName")),
                        SqlType = reader.GetString(reader.GetOrdinal("SqlType")),
                        SqlStatement = reader.GetString(reader.GetOrdinal("SqlStatement")),
                        Parameters = reader.IsDBNull(reader.GetOrdinal("Parameters")) ? null : reader.GetString(reader.GetOrdinal("Parameters")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                        ExecutionOrder = reader.GetInt32(reader.GetOrdinal("ExecutionOrder")),
                        ValidationEnabled = reader.GetBoolean(reader.GetOrdinal("ValidationEnabled"))
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取SQL配置失败: {ConfigId}", configId);
                throw;
            }

            return null;
        }

        /// <summary>
        /// 替换SQL中的占位符
        /// </summary>
        private string ReplaceSqlPlaceholders(
            string sql,
            string? sourceFileName,
            string? fileType,
            Dictionary<string, object?>? parameters)
        {
            var result = sql;

            // 替换文件相关占位符
            if (!string.IsNullOrWhiteSpace(sourceFileName))
            {
                result = result.Replace("{SourceFileName}", sourceFileName);
            }
            if (!string.IsNullOrWhiteSpace(fileType))
            {
                result = result.Replace("{FileType}", fileType);
            }

            // 替换参数占位符
            if (parameters != null)
            {
                foreach (var param in parameters)
                {
                    var placeholder = $"{{{param.Key}}}";
                    var value = param.Value?.ToString() ?? string.Empty;
                    result = result.Replace(placeholder, value);
                }
            }

            return result;
        }

        /// <summary>
        /// 更新SQL配置的执行信息
        /// </summary>
        private async Task UpdateSqlConfigExecutionInfoAsync(
            int configId,
            bool isSuccess,
            int rowsAffected,
            SqlConnection connection,
            SqlTransaction? transaction)
        {
            var sql = @"
                UPDATE SqlExecutionConfig
                SET LastExecuteTime = @LastExecuteTime,
                    LastExecuteResult = @LastExecuteResult,
                    ExecuteCount = ExecuteCount + 1,
                    UpdateTime = @UpdateTime
                WHERE Id = @Id";

            try
            {
                using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@Id", configId);
                command.Parameters.AddWithValue("@LastExecuteTime", DateTime.Now);
                
                // 受影响行数为0时，标记为需要重复执行，不标记为成功
                string resultMessage;
                if (rowsAffected == 0)
                {
                    resultMessage = "受影响行数为0，需要重复执行";
                }
                else if (isSuccess)
                {
                    resultMessage = $"成功，影响行数: {rowsAffected}";
                }
                else
                {
                    resultMessage = "失败";
                }
                
                command.Parameters.AddWithValue("@LastExecuteResult", resultMessage);
                command.Parameters.AddWithValue("@UpdateTime", DateTime.Now);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新SQL配置执行信息失败: {ConfigId}", configId);
            }
        }

        /// <summary>
        /// 记录SQL执行日志
        /// </summary>
        private async Task LogSqlExecutionAsync(
            int configId,
            string configName,
            string sql,
            Dictionary<string, object?>? parameters,
            bool isSuccess,
            string? errorMessage,
            string? sourceFileName,
            SqlConnection connection,
            SqlTransaction? transaction,
            long? executionDuration = null,
            int? rowsAffected = null)
        {
            var sqlLog = @"
                INSERT INTO SqlExecutionLog 
                (ConfigId, ConfigName, SqlStatement, Parameters, ExecutionTime, ExecutionDuration, RowsAffected, IsSuccess, ErrorMessage, SourceFileName)
                VALUES 
                (@ConfigId, @ConfigName, @SqlStatement, @Parameters, @ExecutionTime, @ExecutionDuration, @RowsAffected, @IsSuccess, @ErrorMessage, @SourceFileName)";

            try
            {
                using var command = new SqlCommand(sqlLog, connection, transaction);
                command.Parameters.AddWithValue("@ConfigId", configId);
                command.Parameters.AddWithValue("@ConfigName", configName);
                command.Parameters.AddWithValue("@SqlStatement", sql);
                command.Parameters.AddWithValue("@Parameters", parameters != null ? JsonSerializer.Serialize(parameters) : (object)DBNull.Value);
                command.Parameters.AddWithValue("@ExecutionTime", DateTime.Now);
                command.Parameters.AddWithValue("@ExecutionDuration", executionDuration.HasValue ? (object)executionDuration.Value : DBNull.Value);
                command.Parameters.AddWithValue("@RowsAffected", rowsAffected.HasValue ? (object)rowsAffected.Value : DBNull.Value);
                command.Parameters.AddWithValue("@IsSuccess", isSuccess);
                command.Parameters.AddWithValue("@ErrorMessage", errorMessage ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@SourceFileName", sourceFileName ?? (object)DBNull.Value);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "记录SQL执行日志失败: {ConfigName}", configName);
            }
        }
    }

    /// <summary>
    /// SQL执行配置模型
    /// </summary>
    public class SqlExecutionConfigModel
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = string.Empty;
        public string SqlType { get; set; } = string.Empty;
        public string SqlStatement { get; set; } = string.Empty;
        public string? Parameters { get; set; }
        public string? Description { get; set; }
        public int ExecutionOrder { get; set; }
        public bool ValidationEnabled { get; set; }
    }
}

