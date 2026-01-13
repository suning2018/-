using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 数据处理服务 - 从FileInfo和FileData读取数据并更新到业务表
    /// </summary>
    public class DataProcessingService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DataProcessingService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly string _connectionString;
        private readonly BusinessTableUpdateService? _businessTableUpdateService;
        private readonly SqlExecutionService? _sqlExecutionService;

        public DataProcessingService(IConfiguration configuration, ILogger<DataProcessingService> logger, DatabaseLogService? databaseLogService = null)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseLogService = databaseLogService;
            _connectionString = configuration.GetConnectionString("SQLServer") 
                ?? throw new InvalidOperationException("数据库连接字符串未配置");
            
            // 初始化业务表更新服务
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _businessTableUpdateService = new BusinessTableUpdateService(
                loggerFactory.CreateLogger<BusinessTableUpdateService>());
        }

        /// <summary>
        /// 处理所有未处理的数据
        /// </summary>
        public async Task ProcessAllDataAsync(DateTime? sinceDate = null)
        {
            try
            {
                _logger.LogInformation("开始处理数据，时间范围: {SinceDate}", sinceDate?.ToString() ?? "全部");

                // 1. 处理Excel测量数据
                await ProcessExcelDataAsync(sinceDate);

                // 2. 处理PDF诊断数据
                await ProcessPdfDataAsync(sinceDate);

                _logger.LogInformation("数据处理完成");
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogInformationAsync("数据处理完成", "DataProcessing", "ProcessAllData");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理数据时发生错误");
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync("处理数据时发生错误", ex, "DataProcessing", "ProcessAllData");
                }
                throw;
            }
        }

        /// <summary>
        /// 处理Excel数据（更新到MeasurementData表）
        /// </summary>
        public async Task ProcessExcelDataAsync(DateTime? sinceDate = null)
        {
            try
            {
                _logger.LogInformation("开始处理Excel数据");

                // 1. 获取需要处理的Excel文件列表
                var excelFiles = await GetExcelFilesToProcessAsync(sinceDate);
                _logger.LogInformation("找到 {Count} 个Excel文件需要处理", excelFiles.Count);

                if (excelFiles.Count == 0)
                {
                    return;
                }

                // 2. 处理每个文件的数据
                foreach (var fileInfo in excelFiles)
                {
                    await ProcessExcelFileAsync(fileInfo);
                }

                _logger.LogInformation("Excel数据处理完成，共处理 {Count} 个文件", excelFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理Excel数据时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 处理PDF数据（更新到DiagnosticData表）
        /// </summary>
        public async Task ProcessPdfDataAsync(DateTime? sinceDate = null)
        {
            try
            {
                _logger.LogInformation("开始处理PDF数据");

                // 1. 获取需要处理的PDF文件列表
                var pdfFiles = await GetPdfFilesToProcessAsync(sinceDate);
                _logger.LogInformation("找到 {Count} 个PDF文件需要处理", pdfFiles.Count);

                if (pdfFiles.Count == 0)
                {
                    return;
                }

                // 2. 处理每个文件的数据
                foreach (var fileInfo in pdfFiles)
                {
                    await ProcessPdfFileAsync(fileInfo);
                }

                _logger.LogInformation("PDF数据处理完成，共处理 {Count} 个文件", pdfFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理PDF数据时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 获取需要处理的Excel文件列表（只获取未处理的数据）
        /// </summary>
        private async Task<List<FileInfoModel>> GetExcelFilesToProcessAsync(DateTime? sinceDate = null, bool onlyUnprocessed = true)
        {
            var files = new List<FileInfoModel>();

            var sql = @"
                SELECT Id, SourceFileName, PartNumber, PartName, SerialNumber, ImportTime
                FROM FileInfo
                WHERE FileType = 'Excel'";

            // 只处理未处理的数据（读写分离）
            if (onlyUnprocessed)
            {
                sql += " AND (Processed = 0 OR Processed IS NULL)";
            }

            if (sinceDate.HasValue)
            {
                sql += " AND ImportTime >= @SinceDate";
            }

            sql += " ORDER BY ImportTime ASC";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
                if (sinceDate.HasValue)
                {
                    command.Parameters.AddWithValue("@SinceDate", sinceDate.Value);
                }

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    files.Add(new FileInfoModel
                    {
                        Id = reader.GetInt32("Id"),
                        SourceFileName = reader.GetString("SourceFileName"),
                        PartNumber = reader.IsDBNull("PartNumber") ? null : reader.GetString("PartNumber"),
                        PartName = reader.IsDBNull("PartName") ? null : reader.GetString("PartName"),
                        SerialNumber = reader.IsDBNull("SerialNumber") ? null : reader.GetString("SerialNumber"),
                        ImportTime = reader.GetDateTime("ImportTime")
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取Excel文件列表失败");
                throw;
            }

            return files;
        }

        /// <summary>
        /// 获取需要处理的PDF文件列表（只获取未处理的数据）
        /// </summary>
        private async Task<List<PdfFileInfoModel>> GetPdfFilesToProcessAsync(DateTime? sinceDate = null, bool onlyUnprocessed = true)
        {
            var files = new List<PdfFileInfoModel>();

            var sql = @"
                SELECT Id, SourceFileName, TestId, Operator, TestDate, Machine, QC20W, LastCalibration, ImportTime
                FROM FileInfo
                WHERE FileType = 'PDF'";

            // 只处理未处理的数据（读写分离）
            if (onlyUnprocessed)
            {
                sql += " AND (Processed = 0 OR Processed IS NULL)";
            }

            if (sinceDate.HasValue)
            {
                sql += " AND ImportTime >= @SinceDate";
            }

            sql += " ORDER BY ImportTime ASC";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
                if (sinceDate.HasValue)
                {
                    command.Parameters.AddWithValue("@SinceDate", sinceDate.Value);
                }

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    files.Add(new PdfFileInfoModel
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("Id")),
                        SourceFileName = reader.GetString(reader.GetOrdinal("SourceFileName")),
                        TestId = reader.IsDBNull(reader.GetOrdinal("TestId")) ? null : reader.GetString(reader.GetOrdinal("TestId")),
                        Operator = reader.IsDBNull(reader.GetOrdinal("Operator")) ? null : reader.GetString(reader.GetOrdinal("Operator")),
                        TestDate = reader.IsDBNull(reader.GetOrdinal("TestDate")) ? null : reader.GetString(reader.GetOrdinal("TestDate")),
                        Machine = reader.IsDBNull(reader.GetOrdinal("Machine")) ? null : reader.GetString(reader.GetOrdinal("Machine")),
                        QC20W = reader.IsDBNull(reader.GetOrdinal("QC20W")) ? null : reader.GetString(reader.GetOrdinal("QC20W")),
                        LastCalibration = reader.IsDBNull(reader.GetOrdinal("LastCalibration")) ? null : reader.GetString(reader.GetOrdinal("LastCalibration")),
                        ImportTime = reader.GetDateTime(reader.GetOrdinal("ImportTime"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取PDF文件列表失败");
                throw;
            }

            return files;
        }

        /// <summary>
        /// 处理单个Excel文件的数据
        /// </summary>
        private async Task ProcessExcelFileAsync(FileInfoModel fileInfo)
        {
            try
            {
                _logger.LogDebug("处理Excel文件: {FileName}", fileInfo.SourceFileName);

                // 1. 从FileData表读取该文件的所有数据行（使用FileInfoId关联）
                var fileDataRows = await GetFileDataRowsAsync(fileInfo.Id);

                if (fileDataRows.Count == 0)
                {
                    _logger.LogWarning("文件 {FileName} 没有数据行", fileInfo.SourceFileName);
                    return;
                }

                // 2. 按行号分组数据
                var groupedRows = fileDataRows
                    .GroupBy(r => r.RowNumber)
                    .OrderBy(g => g.Key)
                    .ToList();

                // 3. 根据配置规则更新业务表（生成SQL并存储，不执行）
                // 数据直接通过DataMappingConfig映射到实际业务表，不再保存到中间表
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();

                try
                {
                    // 根据配置规则更新业务表（生成SQL并存储，不执行）
                    if (_businessTableUpdateService != null)
                    {
                        await _businessTableUpdateService.UpdateBusinessTablesFromFileDataAsync(
                            fileInfo.Id,
                            "Excel",
                            connection,
                            transaction);
                    }

                    // 标记文件为已处理
                    await MarkFileAsProcessedAsync(fileInfo.SourceFileName, connection, transaction);

                    transaction.Commit();
                    _logger.LogInformation("成功处理Excel文件: {FileName}, 共 {Count} 行数据", 
                        fileInfo.SourceFileName, groupedRows.Count);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogInformationAsync($"成功处理Excel文件: {fileInfo.SourceFileName}, 共 {groupedRows.Count} 行数据", "DataProcessing", "ProcessExcelFile", fileName: fileInfo.SourceFileName);
                    }
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "处理Excel文件 {FileName} 时发生错误", fileInfo.SourceFileName);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理Excel文件 {FileName} 失败", fileInfo.SourceFileName);
                throw;
            }
        }

        /// <summary>
        /// 处理单个PDF文件的数据
        /// </summary>
        private async Task ProcessPdfFileAsync(PdfFileInfoModel fileInfo)
        {
            try
            {
                _logger.LogDebug("处理PDF文件: {FileName}", fileInfo.SourceFileName);

                // 1. 根据配置规则更新业务表（生成SQL并存储，不执行）
                // 数据直接通过DataMappingConfig映射到实际业务表，不再保存到中间表
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();

                try
                {
                    // 根据配置规则更新业务表（生成SQL并存储，不执行）
                    if (_businessTableUpdateService != null)
                    {
                        await _businessTableUpdateService.UpdateBusinessTablesFromFileDataAsync(
                            fileInfo.Id,
                            "PDF",
                            connection,
                            transaction);
                    }


                    // 2. 标记文件为已处理
                    await MarkFileAsProcessedAsync(fileInfo.SourceFileName, connection, transaction);

                    transaction.Commit();
                    _logger.LogInformation("成功处理PDF文件: {FileName}", fileInfo.SourceFileName);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogInformationAsync($"成功处理PDF文件: {fileInfo.SourceFileName}", "DataProcessing", "ProcessPdfFile", fileName: fileInfo.SourceFileName);
                    }
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "处理PDF文件 {FileName} 时发生错误", fileInfo.SourceFileName);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理PDF文件 {FileName} 失败", fileInfo.SourceFileName);
                throw;
            }
        }

        /// <summary>
        /// 获取文件数据行
        /// </summary>
        private async Task<List<FileDataRowModel>> GetFileDataRowsAsync(int fileInfoId)
        {
            var rows = new List<FileDataRowModel>();

            var sql = @"
                SELECT RowNumber, ColumnName, ColumnValue
                FROM FileData
                WHERE FileInfoId = @FileInfoId
                ORDER BY RowNumber, ColumnName";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
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
        /// 获取诊断数据字典
        /// </summary>
        private async Task<Dictionary<string, string?>> GetDiagnosticDataAsync(string sourceFileName)
        {
            var diagnosticData = new Dictionary<string, string?>();

            var sql = @"
                SELECT ColumnName, ColumnValue
                FROM FileData
                WHERE SourceFileName = @SourceFileName
                  AND ColumnName LIKE '%Percentage' OR ColumnName LIKE '%Value' OR ColumnName = 'Roundness'";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@SourceFileName", sourceFileName);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var columnName = reader.GetString(reader.GetOrdinal("ColumnName"));
                    var columnValue = reader.IsDBNull(reader.GetOrdinal("ColumnValue")) ? null : reader.GetString(reader.GetOrdinal("ColumnValue"));
                    diagnosticData[columnName] = columnValue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取诊断数据失败: {FileName}", sourceFileName);
                throw;
            }

            return diagnosticData;
        }

        /// <summary>
        /// 标记文件为已处理
        /// </summary>
        private async Task MarkFileAsProcessedAsync(string sourceFileName, SqlConnection connection, SqlTransaction transaction)
        {
            var sql = @"
                UPDATE FileInfo
                SET Processed = 1, ProcessedTime = @ProcessedTime
                WHERE SourceFileName = @SourceFileName";

            var parameters = new Dictionary<string, object?>
            {
                { "@SourceFileName", sourceFileName },
                { "@ProcessedTime", DateTime.Now }
            };

            await ExecuteNonQueryAsync(sql, parameters, connection, transaction);
            _logger.LogDebug("标记文件为已处理: {FileName}", sourceFileName);
        }


        /// <summary>
        /// 处理所有未处理的数据（读写分离专用方法）
        /// </summary>
        public async Task ProcessUnprocessedDataAsync(DateTime? sinceDate = null)
        {
            await ProcessAllDataAsync(sinceDate);
        }

        /// <summary>
        /// 获取未处理文件统计
        /// </summary>
        public async Task<(int ExcelCount, int PdfCount)> GetUnprocessedFileCountAsync()
        {
            var sql = @"
                SELECT 
                    SUM(CASE WHEN FileType = 'Excel' AND (Processed = 0 OR Processed IS NULL) THEN 1 ELSE 0 END) AS ExcelCount,
                    SUM(CASE WHEN FileType = 'PDF' AND (Processed = 0 OR Processed IS NULL) THEN 1 ELSE 0 END) AS PdfCount
                FROM FileInfo";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
                using var reader = await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var excelCount = reader.IsDBNull(reader.GetOrdinal("ExcelCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("ExcelCount"));
                    var pdfCount = reader.IsDBNull(reader.GetOrdinal("PdfCount")) ? 0 : reader.GetInt32(reader.GetOrdinal("PdfCount"));
                    return (excelCount, pdfCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取未处理文件统计失败");
            }

            return (0, 0);
        }

        /// <summary>
        /// 执行非查询SQL
        /// </summary>
        private async Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object?>? parameters = null, SqlConnection? connection = null, SqlTransaction? transaction = null)
        {
            var shouldDisposeConnection = connection == null;
            SqlConnection? localConnection = connection;

            try
            {
                if (localConnection == null)
                {
                    localConnection = new SqlConnection(_connectionString);
                    await localConnection.OpenAsync();
                }

                using var command = new SqlCommand(sql, localConnection, transaction);

                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }

                return await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行SQL失败: {Sql}", sql);
                throw;
            }
            finally
            {
                if (shouldDisposeConnection && localConnection != null)
                {
                    localConnection.Dispose();
                }
            }
        }
    }

    #region 数据模型

    /// <summary>
    /// 文件信息模型
    /// </summary>
    public class FileInfoModel
    {
        public int Id { get; set; }
        public string SourceFileName { get; set; } = string.Empty;
        public string? PartNumber { get; set; }
        public string? PartName { get; set; }
        public string? SerialNumber { get; set; }
        public DateTime ImportTime { get; set; }
    }

    /// <summary>
    /// PDF文件信息模型
    /// </summary>
    public class PdfFileInfoModel
    {
        public int Id { get; set; }
        public string SourceFileName { get; set; } = string.Empty;
        public string? TestId { get; set; }
        public string? Operator { get; set; }
        public string? TestDate { get; set; }
        public string? Machine { get; set; }
        public string? QC20W { get; set; }
        public string? LastCalibration { get; set; }
        public DateTime ImportTime { get; set; }
    }

    /// <summary>
    /// 文件数据行模型
    /// </summary>
    public class FileDataRowModel
    {
        public int RowNumber { get; set; }
        public string ColumnName { get; set; } = string.Empty;
        public string? ColumnValue { get; set; }
    }


    #endregion
}

