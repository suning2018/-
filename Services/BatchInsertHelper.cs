using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 批量插入辅助类
    /// </summary>
    public class BatchInsertHelper
    {
        private readonly ILogger<BatchInsertHelper>? _logger;

        public BatchInsertHelper(ILogger<BatchInsertHelper>? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 批量插入数据到FileData表
        /// </summary>
        public async Task<int> BulkInsertFileDataAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string sourceFileName,
            string fileType,
            List<Dictionary<string, object>> dataRows,
            DateTime importTime)
        {
            if (dataRows == null || dataRows.Count == 0)
            {
                return 0;
            }

            try
            {
                // 创建DataTable
                var dataTable = new DataTable();
                dataTable.Columns.Add("SourceFileName", typeof(string));
                dataTable.Columns.Add("FileType", typeof(string));
                dataTable.Columns.Add("RowNumber", typeof(int));
                dataTable.Columns.Add("ColumnName", typeof(string));
                dataTable.Columns.Add("ColumnValue", typeof(string));
                dataTable.Columns.Add("ImportTime", typeof(DateTime));

                // 填充数据
                foreach (var rowData in dataRows)
                {
                    foreach (var column in rowData)
                    {
                        var row = dataTable.NewRow();
                        row["SourceFileName"] = sourceFileName;
                        row["FileType"] = fileType;
                        row["RowNumber"] = rowData.ContainsKey("RowNumber") ? rowData["RowNumber"] : 0;
                        row["ColumnName"] = column.Key;
                        row["ColumnValue"] = column.Value?.ToString() ?? string.Empty;
                        row["ImportTime"] = importTime;
                        dataTable.Rows.Add(row);
                    }
                }

                // 使用SqlBulkCopy批量插入
                using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
                bulkCopy.DestinationTableName = "FileData";
                bulkCopy.BatchSize = 1000; // 每批1000条
                bulkCopy.BulkCopyTimeout = 300; // 5分钟超时

                // 映射列
                bulkCopy.ColumnMappings.Add("SourceFileName", "SourceFileName");
                bulkCopy.ColumnMappings.Add("FileType", "FileType");
                bulkCopy.ColumnMappings.Add("RowNumber", "RowNumber");
                bulkCopy.ColumnMappings.Add("ColumnName", "ColumnName");
                bulkCopy.ColumnMappings.Add("ColumnValue", "ColumnValue");
                bulkCopy.ColumnMappings.Add("ImportTime", "ImportTime");

                await bulkCopy.WriteToServerAsync(dataTable);

                _logger?.LogInformation("批量插入 {Count} 条数据到FileData表", dataTable.Rows.Count);
                return dataTable.Rows.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "批量插入数据失败");
                throw;
            }
        }

        /// <summary>
        /// 批量插入Excel数据（使用MERGE处理重复）
        /// </summary>
        public async Task BulkInsertExcelDataAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string sourceFileName,
            List<ExcelRowData> rows,
            DateTime importTime)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            // 准备批量数据
            var dataRows = new List<Dictionary<string, object>>();
            foreach (var rowData in rows)
            {
                var rowDict = new Dictionary<string, object>();
                foreach (var col in rowData.Columns)
                {
                    rowDict[col.Key] = col.Value;
                }
                rowDict["RowNumber"] = rowData.RowNumber;
                dataRows.Add(rowDict);
            }

            // 先批量插入到临时表
            var tempTableName = $"#TempFileData_{Guid.NewGuid():N}";
            await CreateTempTableAsync(connection, transaction, tempTableName);
            
            try
            {
                // 插入到临时表
                await BulkInsertToTempTableAsync(connection, transaction, tempTableName, sourceFileName, "Excel", dataRows, importTime);
                
                // 使用MERGE从临时表合并到主表
                var mergeSql = $@"
                    MERGE FileData AS target
                    USING {tempTableName} AS source
                    ON target.SourceFileName = source.SourceFileName 
                       AND target.RowNumber = source.RowNumber 
                       AND target.ColumnName = source.ColumnName
                    WHEN MATCHED THEN
                        UPDATE SET ColumnValue = source.ColumnValue, ImportTime = source.ImportTime
                    WHEN NOT MATCHED THEN
                        INSERT (SourceFileName, FileType, RowNumber, ColumnName, ColumnValue, ImportTime)
                        VALUES (source.SourceFileName, source.FileType, source.RowNumber, source.ColumnName, source.ColumnValue, source.ImportTime);
                ";

                using var command = new SqlCommand(mergeSql, connection, transaction);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                // 删除临时表
                var dropSql = $"DROP TABLE IF EXISTS {tempTableName}";
                using var dropCommand = new SqlCommand(dropSql, connection, transaction);
                await dropCommand.ExecuteNonQueryAsync();
            }
        }

        private async Task CreateTempTableAsync(SqlConnection connection, SqlTransaction transaction, string tableName)
        {
            var createSql = $@"
                CREATE TABLE {tableName} (
                    SourceFileName NVARCHAR(500) NOT NULL,
                    FileType NVARCHAR(50) NOT NULL,
                    RowNumber INT NOT NULL,
                    ColumnName NVARCHAR(200) NOT NULL,
                    ColumnValue NVARCHAR(MAX),
                    ImportTime DATETIME NOT NULL
                );
            ";

            using var command = new SqlCommand(createSql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        private async Task BulkInsertToTempTableAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string tableName,
            string sourceFileName,
            string fileType,
            List<Dictionary<string, object>> dataRows,
            DateTime importTime)
        {
            var dataTable = new DataTable();
            dataTable.Columns.Add("SourceFileName", typeof(string));
            dataTable.Columns.Add("FileType", typeof(string));
            dataTable.Columns.Add("RowNumber", typeof(int));
            dataTable.Columns.Add("ColumnName", typeof(string));
            dataTable.Columns.Add("ColumnValue", typeof(string));
            dataTable.Columns.Add("ImportTime", typeof(DateTime));

            foreach (var rowData in dataRows)
            {
                foreach (var column in rowData)
                {
                    if (column.Key == "RowNumber") continue;
                    
                    var row = dataTable.NewRow();
                    row["SourceFileName"] = sourceFileName;
                    row["FileType"] = fileType;
                    row["RowNumber"] = rowData.GetValueOrDefault("RowNumber", 0);
                    row["ColumnName"] = column.Key;
                    row["ColumnValue"] = column.Value?.ToString() ?? string.Empty;
                    row["ImportTime"] = importTime;
                    dataTable.Rows.Add(row);
                }
            }

            using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
            bulkCopy.DestinationTableName = tableName;
            bulkCopy.BatchSize = 1000;
            
            bulkCopy.ColumnMappings.Add("SourceFileName", "SourceFileName");
            bulkCopy.ColumnMappings.Add("FileType", "FileType");
            bulkCopy.ColumnMappings.Add("RowNumber", "RowNumber");
            bulkCopy.ColumnMappings.Add("ColumnName", "ColumnName");
            bulkCopy.ColumnMappings.Add("ColumnValue", "ColumnValue");
            bulkCopy.ColumnMappings.Add("ImportTime", "ImportTime");

            await bulkCopy.WriteToServerAsync(dataTable);
        }
    }
}

