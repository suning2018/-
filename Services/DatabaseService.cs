using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 数据库操作服务
    /// </summary>
    public class DatabaseService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DatabaseService> _logger;
        private readonly DatabaseLogService? _databaseLogService;

        public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger, DatabaseLogService? databaseLogService = null)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseLogService = databaseLogService;
            // InitializeDatabase(); // 已禁用：表已提前创建，不需要自动创建
        }

        /// <summary>
        /// 获取数据库连接字符串
        /// </summary>
        /// <returns>连接字符串</returns>
        public string GetConnectionString()
        {
            var connectionString = _configuration.GetConnectionString("SQLServer");
            
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("数据库连接字符串未配置。请检查 appsettings.json 中的 ConnectionStrings:SQLServer 配置。");
                throw new InvalidOperationException("数据库连接字符串未配置。请检查 appsettings.json 中的 ConnectionStrings:SQLServer 配置。");
            }
            
            return connectionString;
        }

        /// <summary>
        /// 创建数据库连接
        /// </summary>
        /// <returns>数据库连接对象</returns>
        public async Task<SqlConnection> CreateConnectionAsync()
        {
            var connectionString = GetConnectionString();
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }


        /// <summary>
        /// 保存Excel数据到数据库
        /// </summary>
        public async Task SaveExcelDataAsync(ExcelFileData fileData)
        {
            if (fileData == null || fileData.Rows == null || fileData.Rows.Count == 0)
            {
                _logger.LogWarning("文件数据为空，跳过保存: {FileName}", fileData?.SourceFileName ?? "Unknown");
                return;
            }

            try
            {
                using var connection = await CreateConnectionAsync();
                using var transaction = connection.BeginTransaction();
                
                try
                    {
                    var importTime = fileData.ImportTime;

                    // 1. 保存文件信息（允许同名文件多次导入，每次都插入新记录）
                    var fileInfoSql = @"
                        INSERT INTO FileInfo (SourceFileName, FileType, PartNumber, PartName, SerialNumber, ImportTime)
                        VALUES (@SourceFileName, @FileType, @PartNumber, @PartName, @SerialNumber, @ImportTime);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);
                    ";

                    var fileInfoParams = new Dictionary<string, object>
                    {
                        { "@SourceFileName", fileData.SourceFileName },
                        { "@FileType", "Excel" },
                        { "@PartNumber", fileData.HeaderInfo.GetValueOrDefault("PartNumber", string.Empty) },
                        { "@PartName", fileData.HeaderInfo.GetValueOrDefault("PartName", string.Empty) },
                        { "@SerialNumber", fileData.HeaderInfo.GetValueOrDefault("SerialNumber", string.Empty) },
                        { "@ImportTime", importTime }
                    };

                    // 获取插入的FileInfo Id
                    var fileInfoId = await ExecuteScalarAsync(fileInfoSql, fileInfoParams, connection, transaction);
                    if (fileInfoId == null || !int.TryParse(fileInfoId.ToString(), out int fileInfoIdValue))
                    {
                        throw new Exception("无法获取FileInfo Id");
                    }

                    // 2. 保存数据行（跳过 ColumnValue 为空的数据）
                    foreach (var rowData in fileData.Rows)
                    {
                        foreach (var column in rowData.Columns)
                        {
                            // 跳过 ColumnValue 为空或只包含空白字符的数据
                            if (string.IsNullOrWhiteSpace(column.Value))
                            {
                                continue;
                            }

                            // 允许同名文件多次导入，每次都插入新记录
                            var insertSql = @"
                                INSERT INTO FileData (FileInfoId, SourceFileName, FileType, RowNumber, ColumnName, ColumnValue, ImportTime)
                                VALUES (@FileInfoId, @SourceFileName, @FileType, @RowNumber, @ColumnName, @ColumnValue, @ImportTime);
                            ";

                            // 截断过长的列名和列值（防止数据库字段长度限制）
                            var columnName = column.Key.Length > 200 ? column.Key.Substring(0, 200) : column.Key;
                            var columnValue = column.Value ?? string.Empty;
                            // ColumnValue是NVARCHAR(MAX)，理论上无限制，但为了安全起见，限制为10000字符
                            if (columnValue.Length > 10000)
                            {
                                columnValue = columnValue.Substring(0, 10000);
                                _logger.LogWarning("Excel数据列值过长，已截断: {ColumnName}, 原始长度: {Length}", 
                                    columnName, column.Value?.Length ?? 0);
                            }

                            var parameters = new Dictionary<string, object>
                            {
                                { "@FileInfoId", fileInfoIdValue },
                                { "@SourceFileName", fileData.SourceFileName },
                                { "@FileType", "Excel" },
                                { "@RowNumber", rowData.RowNumber },
                                { "@ColumnName", columnName },
                                { "@ColumnValue", columnValue },
                                { "@ImportTime", importTime }
                            };

                            await ExecuteNonQueryAsync(insertSql, parameters, connection, transaction);
                        }
                    }

                    transaction.Commit();
                    _logger.LogInformation("成功保存文件信息和数据 {RowCount} 行到数据库，源文件: {FileName}", 
                        fileData.Rows.Count, fileData.SourceFileName);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "保存数据到数据库失败，源文件: {FileName}", fileData.SourceFileName);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存数据到数据库时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 保存PDF数据到数据库
        /// </summary>
        public async Task SavePdfDataAsync(PdfFileData fileData)
        {
            if (fileData == null || (fileData.DiagnosticData == null || fileData.DiagnosticData.Count == 0))
            {
                _logger.LogWarning("PDF文件数据为空，跳过保存: {FileName}", fileData?.SourceFileName ?? "Unknown");
                return;
            }

            try
            {
                using var connection = await CreateConnectionAsync();
                using var transaction = connection.BeginTransaction();
                
                try
                {
                    var importTime = fileData.ImportTime;

                    // 1. 保存文件信息（允许同名文件多次导入，每次都插入新记录）
                    var fileInfoSql = @"
                        INSERT INTO FileInfo (SourceFileName, FileType, PartName, TestId, Operator, TestDate, Machine, QC20W, LastCalibration, ImportTime)
                        VALUES (@SourceFileName, @FileType, @PartName, @TestId, @Operator, @TestDate, @Machine, @QC20W, @LastCalibration, @ImportTime);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);
                    ";

                    var machineValue = fileData.HeaderInfo.GetValueOrDefault("Machine", string.Empty);
                    var partName = fileData.HeaderInfo.GetValueOrDefault("PartName", string.Empty);
                    
                    // 记录Machine字段的提取和保存情况
                    if (!string.IsNullOrWhiteSpace(machineValue))
                    {
                        _logger.LogInformation("保存PDF文件信息，Machine字段: {Machine}", machineValue);
                    }
                    else
                    {
                        _logger.LogWarning("保存PDF文件信息，Machine字段为空。HeaderInfo包含的键: {Keys}", 
                            string.Join(", ", fileData.HeaderInfo.Keys));
                    }
                    
                    // 记录PartName字段的提取和保存情况（序列号存入PartName）
                    if (!string.IsNullOrWhiteSpace(partName))
                    {
                        _logger.LogInformation("保存PDF文件信息，PartName字段（序列号）: {PartName}", partName);
                    }
                    
                    var fileInfoParams = new Dictionary<string, object>
                    {
                        { "@SourceFileName", fileData.SourceFileName },
                        { "@FileType", fileData.FileType },
                        { "@PartName", partName },
                        { "@TestId", fileData.HeaderInfo.GetValueOrDefault("TestId", string.Empty) },
                        { "@Operator", fileData.HeaderInfo.GetValueOrDefault("Operator", string.Empty) },
                        { "@TestDate", fileData.HeaderInfo.GetValueOrDefault("TestDate", string.Empty) },
                        { "@Machine", machineValue },
                        { "@QC20W", fileData.HeaderInfo.GetValueOrDefault("QC20W", string.Empty) },
                        { "@LastCalibration", fileData.HeaderInfo.GetValueOrDefault("LastCalibration", string.Empty) },
                        { "@ImportTime", importTime }
                    };

                    // 获取插入的FileInfo Id
                    var fileInfoId = await ExecuteScalarAsync(fileInfoSql, fileInfoParams, connection, transaction);
                    if (fileInfoId == null || !int.TryParse(fileInfoId.ToString(), out int fileInfoIdValue))
                    {
                        throw new Exception("无法获取FileInfo Id");
                    }

                    // 2. 保存诊断数据（跳过 ColumnValue 为空的数据）
                    if (fileData.DiagnosticData != null)
                    {
                        _logger.LogInformation("准备保存 {Count} 条诊断数据", fileData.DiagnosticData.Count);
                        int rowNum = 1;
                        foreach (var diagnosticItem in fileData.DiagnosticData)
                        {
                            // 跳过 ColumnValue 为空或只包含空白字符的数据
                            if (string.IsNullOrWhiteSpace(diagnosticItem.Value))
                            {
                                _logger.LogDebug("跳过空值诊断数据: {Key}", diagnosticItem.Key);
                                continue;
                            }
                            
                            _logger.LogDebug("保存诊断数据: RowNumber={RowNumber}, ColumnName={ColumnName}, ColumnValue={ColumnValue}", 
                                rowNum, diagnosticItem.Key, diagnosticItem.Value);

                            // 允许同名文件多次导入，每次都插入新记录
                            var insertSql = @"
                                INSERT INTO FileData (FileInfoId, SourceFileName, FileType, RowNumber, ColumnName, ColumnValue, ImportTime)
                                VALUES (@FileInfoId, @SourceFileName, @FileType, @RowNumber, @ColumnName, @ColumnValue, @ImportTime);
                            ";

                            // 截断过长的列名和列值（防止数据库字段长度限制）
                            var columnName = diagnosticItem.Key.Length > 200 ? diagnosticItem.Key.Substring(0, 200) : diagnosticItem.Key;
                            var columnValue = diagnosticItem.Value ?? string.Empty;
                            // ColumnValue是NVARCHAR(MAX)，理论上无限制，但为了安全起见，限制为10000字符
                            if (columnValue.Length > 10000)
                            {
                                columnValue = columnValue.Substring(0, 10000);
                                _logger.LogWarning("PDF诊断数据列值过长，已截断: {ColumnName}, 原始长度: {Length}", 
                                    columnName, diagnosticItem.Value?.Length ?? 0);
                            }

                            var parameters = new Dictionary<string, object>
                            {
                                { "@FileInfoId", fileInfoIdValue },
                                { "@SourceFileName", fileData.SourceFileName },
                                { "@FileType", fileData.FileType },
                                { "@RowNumber", rowNum++ },
                                { "@ColumnName", columnName },
                                { "@ColumnValue", columnValue },
                                { "@ImportTime", importTime }
                            };

                            await ExecuteNonQueryAsync(insertSql, parameters, connection, transaction);
                        }
                    }


                    transaction.Commit();
                    _logger.LogInformation("成功保存PDF文件信息和数据到数据库，源文件: {FileName}", fileData.SourceFileName);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "保存PDF数据到数据库失败，源文件: {FileName}", fileData.SourceFileName);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存PDF数据到数据库时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 保存B5R数据到数据库
        /// </summary>
        public async Task SaveB5rDataAsync(B5rFileData fileData)
        {
            if (fileData == null || (fileData.DiagnosticData == null || fileData.DiagnosticData.Count == 0))
            {
                _logger.LogWarning("B5R文件数据为空，跳过保存: {FileName}", fileData?.SourceFileName ?? "Unknown");
                return;
            }

            try
            {
                using var connection = await CreateConnectionAsync();
                using var transaction = connection.BeginTransaction();
                
                try
                {
                    var importTime = fileData.ImportTime;

                    // 1. 保存文件信息
                    var fileInfoSql = @"
                        INSERT INTO FileInfo (SourceFileName, FileType, PartName, TestId, Operator, TestDate, Machine, QC20W, LastCalibration, ImportTime)
                        VALUES (@SourceFileName, @FileType, @PartName, @TestId, @Operator, @TestDate, @Machine, @QC20W, @LastCalibration, @ImportTime);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);
                    ";

                    var machineValue = fileData.HeaderInfo.GetValueOrDefault("Machine", string.Empty);
                    var partName = fileData.HeaderInfo.GetValueOrDefault("PartName", string.Empty);
                    
                    var fileInfoParams = new Dictionary<string, object>
                    {
                        { "@SourceFileName", fileData.SourceFileName },
                        { "@FileType", fileData.FileType },
                        { "@PartName", partName },
                        { "@TestId", fileData.HeaderInfo.GetValueOrDefault("TestId", string.Empty) },
                        { "@Operator", fileData.HeaderInfo.GetValueOrDefault("Operator", string.Empty) },
                        { "@TestDate", fileData.HeaderInfo.GetValueOrDefault("TestDate", string.Empty) },
                        { "@Machine", machineValue },
                        { "@QC20W", fileData.HeaderInfo.GetValueOrDefault("QC20W", string.Empty) },
                        { "@LastCalibration", fileData.HeaderInfo.GetValueOrDefault("LastCalibration", string.Empty) },
                        { "@ImportTime", importTime }
                    };

                    var fileInfoId = await ExecuteScalarAsync(fileInfoSql, fileInfoParams, connection, transaction);
                    if (fileInfoId == null || !int.TryParse(fileInfoId.ToString(), out int fileInfoIdValue))
                    {
                        throw new Exception("无法获取FileInfo Id");
                    }

                    // 2. 保存诊断数据
                    if (fileData.DiagnosticData != null)
                    {
                        int rowNum = 1;
                        foreach (var diagnosticItem in fileData.DiagnosticData)
                        {
                            if (string.IsNullOrWhiteSpace(diagnosticItem.Value))
                            {
                                continue;
                            }
                            
                            var insertSql = @"
                                INSERT INTO FileData (FileInfoId, SourceFileName, FileType, RowNumber, ColumnName, ColumnValue, ImportTime)
                                VALUES (@FileInfoId, @SourceFileName, @FileType, @RowNumber, @ColumnName, @ColumnValue, @ImportTime);
                            ";

                            var columnName = diagnosticItem.Key.Length > 200 ? diagnosticItem.Key.Substring(0, 200) : diagnosticItem.Key;
                            var columnValue = diagnosticItem.Value ?? string.Empty;
                            if (columnValue.Length > 10000)
                            {
                                columnValue = columnValue.Substring(0, 10000);
                            }

                            var parameters = new Dictionary<string, object>
                            {
                                { "@FileInfoId", fileInfoIdValue },
                                { "@SourceFileName", fileData.SourceFileName },
                                { "@FileType", fileData.FileType },
                                { "@RowNumber", rowNum++ },
                                { "@ColumnName", columnName },
                                { "@ColumnValue", columnValue },
                                { "@ImportTime", importTime }
                            };

                            await ExecuteNonQueryAsync(insertSql, parameters, connection, transaction);
                        }
                    }

                    transaction.Commit();
                    _logger.LogInformation("成功保存B5R文件信息和数据到数据库，源文件: {FileName}", fileData.SourceFileName);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, "保存B5R数据到数据库失败，源文件: {FileName}", fileData.SourceFileName);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存B5R数据到数据库时发生错误");
                throw;
            }
        }

        /// <summary>
        /// 执行非查询命令（INSERT, UPDATE, DELETE）
        /// </summary>
        /// <param name="sql">SQL命令语句</param>
        /// <param name="parameters">命令参数</param>
        /// <param name="connection">数据库连接（可选）</param>
        /// <param name="transaction">事务（可选）</param>
        /// <returns>受影响的行数</returns>
        public async Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object>? parameters = null, SqlConnection? connection = null, SqlTransaction? transaction = null)
        {
            var shouldDisposeConnection = connection == null;
            SqlConnection? localConnection = connection;
            
            try
            {
                if (localConnection == null)
                {
                    localConnection = await CreateConnectionAsync();
                }

                using var command = new SqlCommand(sql, localConnection, transaction);

                // 添加参数
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
                _logger.LogError(ex, "执行命令时发生错误: {Sql}", sql);
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

        /// <summary>
        /// 执行标量查询（支持事务）
        /// </summary>
        /// <param name="sql">SQL查询语句</param>
        /// <param name="parameters">查询参数</param>
        /// <param name="connection">数据库连接（可选）</param>
        /// <param name="transaction">事务（可选）</param>
        /// <returns>标量结果</returns>
        public async Task<object?> ExecuteScalarAsync(string sql, Dictionary<string, object>? parameters = null, SqlConnection? connection = null, SqlTransaction? transaction = null)
        {
            var shouldDisposeConnection = connection == null;
            SqlConnection? localConnection = connection;
            
            try
            {
                if (localConnection == null)
                {
                    localConnection = await CreateConnectionAsync();
                }
                else if (localConnection.State != System.Data.ConnectionState.Open)
                {
                    await localConnection.OpenAsync();
                }
                
                using var command = new SqlCommand(sql, localConnection, transaction);

                // 添加参数
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }

                var result = await command.ExecuteScalarAsync();
                return result == DBNull.Value ? null : result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行标量查询时发生错误: {Sql}", sql);
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

        /// <summary>
        /// 执行查询并返回多个结果
        /// </summary>
        /// <param name="sql">SQL查询语句</param>
        /// <param name="parameters">查询参数</param>
        /// <returns>查询结果列表</returns>
        public async Task<List<Dictionary<string, object>>> ExecuteQueryListAsync(string sql, Dictionary<string, object>? parameters = null)
        {
            try
            {
                using var connection = await CreateConnectionAsync();
                using var command = new SqlCommand(sql, connection);

                // 添加参数
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        command.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
                    }
                }

                using var reader = await command.ExecuteReaderAsync();
                
                var results = new List<Dictionary<string, object>>();
                while (await reader.ReadAsync())
                {
                    var result = new Dictionary<string, object>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var fieldName = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        result[fieldName] = value ?? DBNull.Value;
                    }
                    results.Add(result);
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行查询时发生错误: {Sql}", sql);
                throw;
            }
        }

        /// <summary>
        /// 查询数据
        /// </summary>
        public async Task<List<Dictionary<string, object>>> QueryDataAsync(string? sourceFileName = null)
        {
            var sql = "SELECT * FROM ExcelData";
            var parameters = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(sourceFileName))
            {
                sql += " WHERE SourceFileName = @SourceFileName";
                parameters["@SourceFileName"] = sourceFileName;
            }
            sql += " ORDER BY ImportTime DESC, RowNumber";

            return await ExecuteQueryListAsync(sql, parameters.Count > 0 ? parameters : null);
        }

        /// <summary>
        /// 测试数据库连接
        /// </summary>
        /// <returns>连接测试结果</returns>
        public async Task<(bool IsConnected, string Message)> TestConnectionAsync()
        {
            try
            {
                using var connection = await CreateConnectionAsync();
                using var command = new SqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                
                return (true, "数据库连接成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库连接测试失败");
                return (false, $"数据库连接失败: {ex.Message}");
            }
        }
    }
}

