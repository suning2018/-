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
        private readonly ILogger<DataProcessingService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly string _connectionString;
        private readonly BusinessTableUpdateService? _businessTableUpdateService;

        public DataProcessingService(IConfiguration configuration, ILogger<DataProcessingService> logger, DatabaseLogService? databaseLogService = null)
        {
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

                // 2. 根据配置规则更新业务表（生成SQL并存储，不执行）
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();

                try
                {
                    if (_businessTableUpdateService != null)
                    {
                        await _businessTableUpdateService.UpdateBusinessTablesFromFileDataAsync(
                            fileInfo.Id,
                            "Excel",
                            connection,
                            transaction);
                    }

                    await MarkFileAsProcessedAsync(fileInfo.Id, connection, transaction);
                    transaction.Commit();
                    
                    var rowCount = fileDataRows.GroupBy(r => r.RowNumber).Count();
                    _logger.LogInformation("成功处理Excel文件: {FileName}, 共 {Count} 行数据", 
                        fileInfo.SourceFileName, rowCount);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogInformationAsync($"成功处理Excel文件: {fileInfo.SourceFileName}, 共 {rowCount} 行数据", "DataProcessing", "ProcessExcelFile", fileName: fileInfo.SourceFileName);
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
                    if (_businessTableUpdateService != null)
                    {
                        await _businessTableUpdateService.UpdateBusinessTablesFromFileDataAsync(
                            fileInfo.Id,
                            "PDF",
                            connection,
                            transaction);
                    }

                    await MarkFileAsProcessedAsync(fileInfo.Id, connection, transaction);

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
        /// 标记文件为已处理
        /// </summary>
        private async Task MarkFileAsProcessedAsync(int fileInfoId, SqlConnection connection, SqlTransaction transaction)
        {
            var sql = @"
                UPDATE FileInfo
                SET Processed = 1, ProcessedTime = @ProcessedTime
                WHERE Id = @FileInfoId";

            var parameters = new Dictionary<string, object?>
            {
                { "@FileInfoId", fileInfoId },
                { "@ProcessedTime", DateTime.Now }
            };

            await ExecuteNonQueryAsync(sql, parameters, connection, transaction);
            _logger.LogDebug("标记文件为已处理: FileInfoId={FileInfoId}", fileInfoId);
        }

        /// <summary>
        /// 处理所有未处理的数据
        /// </summary>
        public async Task ProcessUnprocessedDataAsync()
        {
            try
            {
                // 获取未处理的Excel文件
                var excelFiles = await GetUnprocessedFilesAsync<FileInfoModel>("Excel");
                foreach (var fileInfo in excelFiles)
                {
                    await ProcessExcelFileAsync(fileInfo);
                }

                // 获取未处理的PDF文件
                var pdfFiles = await GetUnprocessedFilesAsync<PdfFileInfoModel>("PDF");
                foreach (var fileInfo in pdfFiles)
                {
                    await ProcessPdfFileAsync(fileInfo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理未处理数据时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 获取未处理的文件列表
        /// </summary>
        private async Task<List<T>> GetUnprocessedFilesAsync<T>(string fileType) where T : class
        {
            var files = new List<T>();
            var sql = $@"
                SELECT Id, SourceFileName, PartNumber, PartName, SerialNumber, 
                       TestId, Operator, TestDate, Machine, QC20W, LastCalibration, ImportTime
                FROM FileInfo
                WHERE FileType = @FileType
                  AND (Processed = 0 OR Processed IS NULL)
                ORDER BY ImportTime ASC";

            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@FileType", fileType);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (fileType == "Excel" && typeof(T) == typeof(FileInfoModel))
                    {
                        var fileInfo = new FileInfoModel
                        {
                            Id = reader.GetInt32(reader.GetOrdinal("Id")),
                            SourceFileName = reader.GetString(reader.GetOrdinal("SourceFileName")),
                            PartNumber = reader.IsDBNull(reader.GetOrdinal("PartNumber")) ? null : reader.GetString(reader.GetOrdinal("PartNumber")),
                            PartName = reader.IsDBNull(reader.GetOrdinal("PartName")) ? null : reader.GetString(reader.GetOrdinal("PartName")),
                            SerialNumber = reader.IsDBNull(reader.GetOrdinal("SerialNumber")) ? null : reader.GetString(reader.GetOrdinal("SerialNumber")),
                            ImportTime = reader.GetDateTime(reader.GetOrdinal("ImportTime"))
                        };
                        files.Add((T)(object)fileInfo);
                    }
                    else if (fileType == "PDF" && typeof(T) == typeof(PdfFileInfoModel))
                    {
                        var fileInfo = new PdfFileInfoModel
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
                        };
                        files.Add((T)(object)fileInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取未处理文件列表失败: FileType={FileType}", fileType);
                throw;
            }

            return files;
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

