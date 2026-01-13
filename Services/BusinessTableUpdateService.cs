using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 业务表更新服务 - 根据配置规则将FileData数据更新到业务表
    /// </summary>
    public class BusinessTableUpdateService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<BusinessTableUpdateService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly string _connectionString;
        private readonly SqlExecutionService? _sqlExecutionService;

        public BusinessTableUpdateService(
            IConfiguration configuration,
            ILogger<BusinessTableUpdateService> logger,
            DatabaseLogService? databaseLogService = null,
            SqlExecutionService? sqlExecutionService = null)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseLogService = databaseLogService;
            _sqlExecutionService = sqlExecutionService;
            _connectionString = configuration.GetConnectionString("SQLServer")
                ?? throw new InvalidOperationException("数据库连接字符串未配置");
        }

        /// <summary>
        /// 根据配置规则更新业务表
        /// </summary>
        public async Task UpdateBusinessTablesFromFileDataAsync(
            int fileInfoId,
            string fileType,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            try
            {
                // 1. 获取所有启用的映射配置
                var mappingConfigs = await GetActiveMappingConfigsAsync(fileType, connection, transaction);

                if (mappingConfigs.Count == 0)
                {
                    _logger.LogDebug("没有找到启用的数据映射配置，跳过业务表更新");
                    return;
                }

                _logger.LogInformation("找到 {Count} 个数据映射配置", mappingConfigs.Count);

                // 2. 从FileData表读取该文件的所有数据（使用FileInfoId关联）
                var fileDataRows = await GetFileDataRowsAsync(fileInfoId, connection, transaction);

                if (fileDataRows.Count == 0)
                {
                    _logger.LogWarning("文件 FileInfoId={FileInfoId} 没有数据行", fileInfoId);
                    return;
                }

                // 3. 按配置规则分组处理
                var configGroups = mappingConfigs.GroupBy(c => new { c.TargetTable, c.TargetMatchField });

                foreach (var configGroup in configGroups)
                {
                    await ProcessMappingConfigGroupAsync(
                        configGroup.ToList(),
                        fileDataRows,
                        connection,
                        transaction);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新业务表失败: FileInfoId={FileInfoId}", fileInfoId);
                throw;
            }
        }

        /// <summary>
        /// 获取启用的映射配置
        /// </summary>
        private async Task<List<DataMappingConfigModel>> GetActiveMappingConfigsAsync(
            string fileType,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            var configs = new List<DataMappingConfigModel>();

            var sql = @"
                SELECT Id, ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField,
                       TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description
                FROM DataMappingConfig
                WHERE IsActive = 1
                  AND SourceTable = 'FileData'
                  AND (SourceFileType IS NULL OR SourceFileType = @FileType)
                ORDER BY TargetTable, TargetMatchField";

            try
            {
                using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@FileType", fileType ?? (object)DBNull.Value);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    configs.Add(new DataMappingConfigModel
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("Id")),
                        ConfigName = reader.GetString(reader.GetOrdinal("ConfigName")),
                        SourceTable = reader.GetString(reader.GetOrdinal("SourceTable")),
                        SourceFileType = reader.IsDBNull(reader.GetOrdinal("SourceFileType")) ? null : reader.GetString(reader.GetOrdinal("SourceFileType")),
                        SourceMatchField = reader.GetString(reader.GetOrdinal("SourceMatchField")),
                        SourceDataField = reader.GetString(reader.GetOrdinal("SourceDataField")),
                        TargetTable = reader.GetString(reader.GetOrdinal("TargetTable")),
                        TargetMatchField = reader.GetString(reader.GetOrdinal("TargetMatchField")),
                        TargetUpdateField = reader.GetString(reader.GetOrdinal("TargetUpdateField")),
                        MatchCondition = reader.IsDBNull(reader.GetOrdinal("MatchCondition")) ? null : reader.GetString(reader.GetOrdinal("MatchCondition")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取映射配置失败");
                throw;
            }

            return configs;
        }

        /// <summary>
        /// 获取文件数据行
        /// </summary>
        private async Task<List<FileDataRowModel>> GetFileDataRowsAsync(
            int fileInfoId,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            var rows = new List<FileDataRowModel>();

            var sql = @"
                SELECT RowNumber, ColumnName, ColumnValue
                FROM FileData
                WHERE FileInfoId = @FileInfoId
                ORDER BY RowNumber, ColumnName";

            try
            {
                using var command = new SqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@FileInfoId", fileInfoId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add(new FileDataRowModel
                    {
                        RowNumber = reader.GetInt32(reader.GetOrdinal("RowNumber")),
                        ColumnName = reader.GetString(reader.GetOrdinal("ColumnName")),
                        ColumnValue = reader.IsDBNull(reader.GetOrdinal("ColumnValue")) ? null : reader.GetString(reader.GetOrdinal("ColumnValue"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取文件数据行失败: FileInfoId={FileInfoId}", fileInfoId);
                throw;
            }

            return rows;
        }

        /// <summary>
        /// 处理映射配置组（同一目标表和匹配字段的配置）
        /// </summary>
        private async Task ProcessMappingConfigGroupAsync(
            List<DataMappingConfigModel> configs,
            List<FileDataRowModel> fileDataRows,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            if (configs.Count == 0) return;

            var firstConfig = configs[0];
            var targetTable = firstConfig.TargetTable;
            var targetMatchField = firstConfig.TargetMatchField;

            _logger.LogDebug("处理映射配置组: 目标表={TargetTable}, 匹配字段={MatchField}", targetTable, targetMatchField);

            // 按行号分组FileData数据
            var groupedByRow = fileDataRows.GroupBy(r => r.RowNumber).ToList();

            foreach (var rowGroup in groupedByRow)
            {
                var rowData = rowGroup.ToDictionary(r => r.ColumnName, r => r.ColumnValue);

                // 获取匹配字段的值
                var matchValue = rowData.GetValueOrDefault(firstConfig.SourceMatchField, null);
                if (string.IsNullOrWhiteSpace(matchValue))
                {
                    _logger.LogDebug("行 {RowNumber} 缺少匹配字段 {MatchField}，跳过", rowGroup.Key, firstConfig.SourceMatchField);
                    continue;
                }

                // 构建更新字段列表
                var updateFields = new List<string>();
                var updateParameters = new Dictionary<string, object?>();

                foreach (var config in configs)
                {
                    // 获取要更新的数据字段值
                    var dataValue = rowData.GetValueOrDefault(config.SourceDataField, (string?)null);
                    if (dataValue != null)
                    {
                        updateFields.Add($"{config.TargetUpdateField} = @Update_{config.TargetUpdateField}");
                        updateParameters.Add($"@Update_{config.TargetUpdateField}", dataValue);
                    }
                }

                if (updateFields.Count == 0)
                {
                    _logger.LogDebug("行 {RowNumber} 没有可更新的字段值，跳过", rowGroup.Key);
                    continue;
                }

                // 构建UPDATE SQL
                var updateSql = $@"
                    UPDATE {targetTable}
                    SET {string.Join(", ", updateFields)}, UpdateTime = @UpdateTime
                    WHERE {targetMatchField} = @MatchValue";

                // 添加匹配条件
                if (!string.IsNullOrWhiteSpace(firstConfig.MatchCondition))
                {
                    updateSql += $" AND {firstConfig.MatchCondition}";
                }

                // 合并参数
                var allParameters = new Dictionary<string, object?>
                {
                    { "@MatchValue", matchValue },
                    { "@UpdateTime", DateTime.Now }
                };
                foreach (var param in updateParameters)
                {
                    allParameters.Add(param.Key, param.Value);
                }

                // 不执行SQL，只生成并存储SQL到数据库
                // 保存SQL到SqlExecutionConfig表
                if (_sqlExecutionService != null && configs.Count > 0)
                {
                    await SaveMappingSqlToConfigAsync(configs, updateSql, allParameters, targetTable, matchValue, connection, transaction);
                    _logger.LogInformation(
                        "已生成SQL并存储: 目标表={TargetTable}，匹配值={MatchValue}，更新字段={UpdateFields}",
                        targetTable, matchValue, string.Join(", ", updateFields.Select(f => f.Split('=')[0].Trim())));
                }
                else
                {
                    _logger.LogWarning(
                        "未配置SQL执行服务，无法存储SQL: 表={TargetTable}，匹配字段={MatchField}，匹配值={MatchValue}",
                        targetTable, targetMatchField, matchValue);
                }
            }
        }

        /// <summary>
        /// 保存映射SQL到配置表（为每行数据生成独立的SQL记录）
        /// </summary>
        private async Task SaveMappingSqlToConfigAsync(
            List<DataMappingConfigModel> configs,
            string sql,
            Dictionary<string, object?> parameters,
            string targetTable,
            string matchValue,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            try
            {
                // 为每行数据生成唯一的配置名称（包含匹配值）
                var configName = $"Mapping_{targetTable}_{configs[0].TargetMatchField}_{matchValue}_{DateTime.Now:yyyyMMddHHmmss}";
                var parametersJson = System.Text.Json.JsonSerializer.Serialize(parameters);
                var description = $"自动生成的映射SQL: {string.Join(", ", configs.Select(c => c.Description))}, 匹配值={matchValue}";

                // 直接插入新的SQL记录（不检查是否存在，因为每行数据都需要独立的SQL）
                // 注意：ValidationEnabled 强制设置为 1（所有SQL都必须验证）
                var sqlInsert = @"
                    INSERT INTO SqlExecutionConfig 
                    (ConfigName, SqlType, SqlStatement, Parameters, Description, 
                     IsActive, ExecutionOrder, ValidationEnabled)
                    VALUES 
                    (@ConfigName, 'Mapping', @SqlStatement, @Parameters, @Description,
                     1, 0, 1)";

                using var insertCommand = new SqlCommand(sqlInsert, connection, transaction);
                insertCommand.Parameters.AddWithValue("@ConfigName", configName);
                insertCommand.Parameters.AddWithValue("@SqlStatement", sql);
                insertCommand.Parameters.AddWithValue("@Parameters", parametersJson);
                insertCommand.Parameters.AddWithValue("@Description", description);

                await insertCommand.ExecuteNonQueryAsync();
                _logger.LogDebug("已保存映射SQL到配置表: {ConfigName}", configName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存映射SQL到配置表失败");
            }
        }
    }

    /// <summary>
    /// 数据映射配置模型
    /// </summary>
    public class DataMappingConfigModel
    {
        public int Id { get; set; }
        public string ConfigName { get; set; } = string.Empty;
        public string SourceTable { get; set; } = string.Empty;
        public string? SourceFileType { get; set; }
        public string SourceMatchField { get; set; } = string.Empty;
        public string SourceDataField { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public string TargetMatchField { get; set; } = string.Empty;
        public string TargetUpdateField { get; set; } = string.Empty;
        public string? MatchCondition { get; set; }
        public string? Description { get; set; }
    }

}

