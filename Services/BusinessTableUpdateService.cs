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
        private readonly ILogger<BusinessTableUpdateService> _logger;

        public BusinessTableUpdateService(ILogger<BusinessTableUpdateService> logger)
        {
            _logger = logger;
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

                // 3. 按配置规则分组处理（区分自定义SQL模板和标准映射）
                var standardConfigs = mappingConfigs.Where(c => !c.UseCustomSqlTemplate).ToList();
                var customConfigs = mappingConfigs.Where(c => c.UseCustomSqlTemplate).ToList();

                // 处理标准映射配置
                if (standardConfigs.Count > 0)
                {
                    var standardGroups = standardConfigs.GroupBy(c => new { c.TargetTable, c.TargetMatchField });
                    foreach (var configGroup in standardGroups)
                    {
                        await ProcessMappingConfigGroupAsync(
                            configGroup.ToList(),
                            fileDataRows,
                            connection,
                            transaction);
                    }
                }

                // 处理自定义SQL模板配置
                if (customConfigs.Count > 0)
                {
                    foreach (var config in customConfigs)
                    {
                        await ProcessCustomSqlTemplateAsync(
                            config,
                            fileInfoId,
                            fileDataRows,
                            connection,
                            transaction);
                    }
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
                       TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description,
                       CustomSqlTemplate, UseCustomSqlTemplate, TemplateParameters
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
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                        CustomSqlTemplate = reader.IsDBNull(reader.GetOrdinal("CustomSqlTemplate")) ? null : reader.GetString(reader.GetOrdinal("CustomSqlTemplate")),
                        UseCustomSqlTemplate = !reader.IsDBNull(reader.GetOrdinal("UseCustomSqlTemplate")) && reader.GetBoolean(reader.GetOrdinal("UseCustomSqlTemplate")),
                        TemplateParameters = reader.IsDBNull(reader.GetOrdinal("TemplateParameters")) ? null : reader.GetString(reader.GetOrdinal("TemplateParameters"))
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
                await SaveMappingSqlToConfigAsync(configs, updateSql, allParameters, targetTable, matchValue, connection, transaction);
                _logger.LogInformation(
                    "已生成SQL并存储: 目标表={TargetTable}，匹配值={MatchValue}，更新字段={UpdateFields}",
                    targetTable, matchValue, string.Join(", ", updateFields.Select(f => f.Split('=')[0].Trim())));
            }
        }

        /// <summary>
        /// 保存SQL到配置表
        /// </summary>
        private async Task SaveSqlToConfigAsync(
            string configName,
            string sql,
            string parametersJson,
            string description,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            try
            {
                var sqlInsert = @"
                    INSERT INTO SqlExecutionConfig 
                    (ConfigName, SqlType, SqlStatement, Parameters, Description, 
                     IsActive, ExecutionOrder, ValidationEnabled)
                    VALUES 
                    (@ConfigName, 'Mapping', @SqlStatement, @Parameters, @Description,
                     1, 0, 1)";

                using var command = new SqlCommand(sqlInsert, connection, transaction);
                command.Parameters.AddWithValue("@ConfigName", configName);
                command.Parameters.AddWithValue("@SqlStatement", sql);
                command.Parameters.AddWithValue("@Parameters", parametersJson);
                command.Parameters.AddWithValue("@Description", description);

                await command.ExecuteNonQueryAsync();
                _logger.LogDebug("已保存SQL到配置表: {ConfigName}", configName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "保存SQL到配置表失败: {ConfigName}", configName);
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
            var configName = $"Mapping_{targetTable}_{configs[0].TargetMatchField}_{matchValue}_{DateTime.Now:yyyyMMddHHmmss}";
            var parametersJson = System.Text.Json.JsonSerializer.Serialize(parameters);
            var description = $"自动生成的映射SQL: {string.Join(", ", configs.Select(c => c.Description))}, 匹配值={matchValue}";
            
            await SaveSqlToConfigAsync(configName, sql, parametersJson, description, connection, transaction);
        }

        /// <summary>
        /// 处理自定义SQL模板配置
        /// </summary>
        private async Task ProcessCustomSqlTemplateAsync(
            DataMappingConfigModel config,
            int fileInfoId,
            List<FileDataRowModel> fileDataRows,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(config.CustomSqlTemplate))
            {
                _logger.LogWarning("配置 {ConfigName} 启用了自定义SQL模板，但模板为空", config.ConfigName);
                return;
            }

            try
            {
                // 获取FileInfo信息（用于替换占位符）
                var fileInfo = await GetFileInfoAsync(fileInfoId, connection, transaction);
                if (fileInfo == null)
                {
                    _logger.LogWarning("无法获取FileInfo信息: FileInfoId={FileInfoId}", fileInfoId);
                    return;
                }

                // 替换SQL模板中的占位符
                var sql = ReplacePlaceholders(config.CustomSqlTemplate, fileInfoId, fileInfo);

                // 如果模板中包含 {ColumnName} 或 {RowNumber}，需要为每个匹配的数据行生成SQL
                if (config.CustomSqlTemplate.Contains("{ColumnName}") || config.CustomSqlTemplate.Contains("{RowNumber}"))
                {
                    var templateParams = ParseTemplateParameters(config.TemplateParameters);
                    var matchingRows = GetMatchingRows(fileDataRows, templateParams);

                    if (matchingRows.Count == 0)
                    {
                        _logger.LogDebug("配置 {ConfigName} 没有找到匹配的数据行", config.ConfigName);
                        return;
                    }

                    foreach (var row in matchingRows)
                    {
                        var rowSql = ReplaceRowPlaceholders(sql, row);
                        await SaveCustomSqlToConfigAsync(config, rowSql, fileInfoId, row, connection, transaction);
                    }
                }
                else
                {
                    await SaveCustomSqlToConfigAsync(config, sql, fileInfoId, null, connection, transaction);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理自定义SQL模板失败: ConfigName={ConfigName}", config.ConfigName);
            }
        }

        /// <summary>
        /// 获取FileInfo信息
        /// </summary>
        private async Task<FileInfoForTemplateModel?> GetFileInfoAsync(int fileInfoId, SqlConnection connection, SqlTransaction transaction)
        {
            var sql = @"
                SELECT SourceFileName, FileType, PartNumber, PartName, SerialNumber
                FROM FileInfo
                WHERE Id = @FileInfoId";

            using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@FileInfoId", fileInfoId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return new FileInfoForTemplateModel
            {
                SourceFileName = reader.IsDBNull(reader.GetOrdinal("SourceFileName")) ? null : reader.GetString(reader.GetOrdinal("SourceFileName")),
                FileType = reader.IsDBNull(reader.GetOrdinal("FileType")) ? null : reader.GetString(reader.GetOrdinal("FileType")),
                PartNumber = reader.IsDBNull(reader.GetOrdinal("PartNumber")) ? null : reader.GetString(reader.GetOrdinal("PartNumber")),
                PartName = reader.IsDBNull(reader.GetOrdinal("PartName")) ? null : reader.GetString(reader.GetOrdinal("PartName")),
                SerialNumber = reader.IsDBNull(reader.GetOrdinal("SerialNumber")) ? null : reader.GetString(reader.GetOrdinal("SerialNumber"))
            };
        }

        /// <summary>
        /// 替换SQL模板中的占位符
        /// </summary>
        private string ReplacePlaceholders(string template, int fileInfoId, FileInfoForTemplateModel fileInfo)
        {
            return template
                .Replace("{FileInfoId}", fileInfoId.ToString())
                .Replace("{SourceFileName}", fileInfo.SourceFileName ?? "")
                .Replace("{PartName}", fileInfo.PartName ?? "")
                .Replace("{PartNumber}", fileInfo.PartNumber ?? "")
                .Replace("{SerialNumber}", fileInfo.SerialNumber ?? "")
                .Replace("{FileType}", fileInfo.FileType ?? "");
        }

        /// <summary>
        /// 解析模板参数（JSON格式）
        /// </summary>
        private Dictionary<string, string>? ParseTemplateParameters(string? templateParameters)
        {
            if (string.IsNullOrWhiteSpace(templateParameters))
            {
                return null;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(templateParameters);
            }
            catch
            {
                _logger.LogWarning("解析模板参数失败: {TemplateParameters}", templateParameters);
                return null;
            }
        }

        /// <summary>
        /// 获取匹配的数据行
        /// </summary>
        private List<FileDataRowModel> GetMatchingRows(List<FileDataRowModel> fileDataRows, Dictionary<string, string>? templateParams)
        {
            string? requiredColumnName = null;
            int? requiredRowNumber = null;

            if (templateParams != null)
            {
                templateParams.TryGetValue("ColumnName", out requiredColumnName);
                if (templateParams.TryGetValue("RowNumber", out var rowNumStr) && int.TryParse(rowNumStr, out var rowNum))
                {
                    requiredRowNumber = rowNum;
                }
            }

            return fileDataRows.Where(r =>
                (requiredColumnName == null || r.ColumnName == requiredColumnName) &&
                (requiredRowNumber == null || r.RowNumber == requiredRowNumber)
            ).ToList();
        }

        /// <summary>
        /// 替换行相关的占位符
        /// </summary>
        private string ReplaceRowPlaceholders(string sql, FileDataRowModel row)
        {
            return sql
                .Replace("{ColumnName}", row.ColumnName ?? "")
                .Replace("{RowNumber}", row.RowNumber.ToString())
                .Replace("{ColumnValue}", row.ColumnValue ?? "");
        }

        /// <summary>
        /// 保存自定义SQL到配置表
        /// </summary>
        private async Task SaveCustomSqlToConfigAsync(
            DataMappingConfigModel config,
            string sql,
            int fileInfoId,
            FileDataRowModel? row,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            var configName = $"CustomTemplate_{config.ConfigName}_{fileInfoId}";
            if (row != null)
            {
                configName += $"_{row.RowNumber}_{row.ColumnName}";
            }
            configName += $"_{DateTime.Now:yyyyMMddHHmmss}";

            var description = config.Description ?? $"自定义SQL模板: {config.ConfigName}";
            if (row != null)
            {
                description += $", RowNumber={row.RowNumber}, ColumnName={row.ColumnName}";
            }

            await SaveSqlToConfigAsync(configName, sql, "{}", description, connection, transaction);
        }
    }

    /// <summary>
    /// FileInfo模型（用于自定义SQL模板）
    /// </summary>
    public class FileInfoForTemplateModel
    {
        public string? SourceFileName { get; set; }
        public string? FileType { get; set; }
        public string? PartNumber { get; set; }
        public string? PartName { get; set; }
        public string? SerialNumber { get; set; }
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
        public string? CustomSqlTemplate { get; set; }
        public bool UseCustomSqlTemplate { get; set; }
        public string? TemplateParameters { get; set; }
    }

}

