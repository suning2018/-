using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using FtpExcelProcessor.Services;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace FtpExcelProcessor
{
    class Program
    {
        private static DatabaseLogService? _databaseLogService;
        private static IConfiguration? _configuration;
        private static ILoggerFactory? _loggerFactory;
        private static bool _isRunning = true;
        private static readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);

        static async Task Main(string[] args)
        {
            try
            {
                // 加载配置
                _configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                // 初始化日志
                InitializeLogging(_configuration);
                
                // 创建日志工厂
                _loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddConfiguration(_configuration.GetSection("Logging")).AddSerilog();
                });

                // 初始化数据库日志服务
                var enableDatabaseLog = bool.Parse(_configuration["LogSettings:EnableDatabaseLog"] ?? "true");
                if (enableDatabaseLog)
                {
                    _databaseLogService = new DatabaseLogService(_configuration, _loggerFactory.CreateLogger<DatabaseLogService>());
                    await LogInfoAsync("程序启动", "Program", "Main");
                }

                Console.WriteLine("=== FTP文件处理程序（持续监控模式）===\n");

                // 测试数据库连接
                var databaseService = new DatabaseService(_configuration, _loggerFactory.CreateLogger<DatabaseService>(), _databaseLogService);
                var (isConnected, message) = await databaseService.TestConnectionAsync();
                if (!isConnected)
                {
                    Console.WriteLine($"警告: {message}\n请检查数据库连接配置后重试。");
                    await LogWarningAsync($"数据库连接失败: {message}", "Database", "TestConnection");
                    return;
                }
                Console.WriteLine($"数据库连接: {message}\n");
                await LogInfoAsync($"数据库连接成功: {message}", "Database", "TestConnection");

                // 确保目录存在
                var downloadPath = _configuration["FileSettings:LocalDownloadPath"] ?? "Downloads";
                Directory.CreateDirectory(downloadPath);

                // 获取检查间隔配置
                var checkIntervalSeconds = int.Parse(_configuration["ScheduleSettings:CheckIntervalSeconds"] ?? "30");
                Console.WriteLine($"检查间隔: {checkIntervalSeconds} 秒");
                Console.WriteLine("程序将持续监控：");
                Console.WriteLine("  1. FTP服务器是否有新文件");
                Console.WriteLine("  2. 是否有需要映射的数据");
                Console.WriteLine("  3. 是否有待执行的SQL");
                Console.WriteLine("按 Ctrl+C 或关闭窗口以停止程序。\n");
                await LogInfoAsync($"持续监控模式已启动，检查间隔: {checkIntervalSeconds} 秒", "Program", "Main");

                // 注册Ctrl+C处理
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    _isRunning = false;
                    Console.WriteLine("\n正在停止程序...");
                };

                // 持续循环检查和处理
                await ContinuousProcessLoopAsync(checkIntervalSeconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"程序执行出错: {ex.Message}");
                Serilog.Log.Fatal(ex, "程序执行出错");
                try
                {
                    if (_databaseLogService == null)
                    {
                        var config = new ConfigurationBuilder()
                            .SetBasePath(Directory.GetCurrentDirectory())
                            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                            .Build();
                        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
                        _databaseLogService = new DatabaseLogService(config, loggerFactory.CreateLogger<DatabaseLogService>());
                    }
                    await _databaseLogService.LogErrorAsync("程序执行出错", ex, "Program", "Main");
                }
                catch { }
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
                if (Environment.UserInteractive)
                {
                    Console.WriteLine("\n程序已停止。");
                    WaitForExit();
                }
            }
        }

        /// <summary>
        /// 持续循环处理
        /// </summary>
        static async Task ContinuousProcessLoopAsync(int checkIntervalSeconds)
        {
            while (_isRunning)
            {
                try
                {
                    // 1. 检查并处理FTP文件
                    await ProcessFtpFilesAsync();

                    // 2. 检查并处理数据映射
                    await ProcessDataMappingAsync();

                    // 3. 检查并执行SQL
                    await ProcessSqlExecutionAsync();

                    // 等待指定间隔后再次检查
                    if (_isRunning)
                    {
                        await Task.Delay(checkIntervalSeconds * 1000);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 循环处理出错: {ex.Message}");
                    await LogErrorAsync("循环处理出错", ex, "Program", "ContinuousProcessLoop");
                    // 出错后等待一段时间再继续
                    if (_isRunning)
                    {
                        await Task.Delay(checkIntervalSeconds * 1000);
                    }
                }
            }
        }

        /// <summary>
        /// 处理FTP文件下载和处理
        /// </summary>
        static async Task ProcessFtpFilesAsync()
        {
            if (!await _processingLock.WaitAsync(0))
            {
                return; // 有其他任务在执行，跳过
            }

            try
            {
                if (_configuration == null || _loggerFactory == null)
                {
                    return;
                }

                var ftpService = new FtpService(_configuration, _loggerFactory.CreateLogger<FtpService>(), _databaseLogService);
                var excelService = new ExcelService(_loggerFactory.CreateLogger<ExcelService>(), _databaseLogService);
                var pdfService = new PdfService(_loggerFactory.CreateLogger<PdfService>(), _databaseLogService);
                var databaseService = new DatabaseService(_configuration, _loggerFactory.CreateLogger<DatabaseService>(), _databaseLogService);
                var fileClassificationService = new FileClassificationService(_configuration, _loggerFactory.CreateLogger<FileClassificationService>(), _databaseLogService);

                // 从FTP下载文件
                var downloadedFiles = await ftpService.DownloadFilesAsync();
                
                if (downloadedFiles.Count == 0)
                {
                    return; // 没有新文件
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 发现 {downloadedFiles.Count} 个新文件，开始处理...");
                await LogInfoAsync($"发现 {downloadedFiles.Count} 个新文件", "FTP", "DownloadFiles");

                // 处理每个文件
                foreach (var filePath in downloadedFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var extension = Path.GetExtension(filePath).ToLower();
                    string fileType = "";
                    bool isSuccess = false;
                    
                    try
                    {
                        Console.WriteLine($"  处理文件: {fileName}");
                        await LogInfoAsync($"正在处理文件: {fileName}", "FileProcessing", "ProcessFile", fileName);
                        
                        if (extension == ".xlsx" || extension == ".xls")
                        {
                            fileType = "Excel";
                            var excelFileData = await excelService.ReadExcelFileAsync(filePath);
                            Console.WriteLine($"    读取到 {excelFileData.Rows.Count} 条数据行");
                            await databaseService.SaveExcelDataAsync(excelFileData);
                            Console.WriteLine("    Excel数据已保存到数据库");
                            await LogInfoAsync("Excel数据已保存到数据库", "Database", "SaveExcelData", fileName);
                            isSuccess = true;
                        }
                        else if (extension == ".pdf")
                        {
                            fileType = "PDF";
                            var pdfFileData = await pdfService.ReadPdfFileAsync(filePath);
                            Console.WriteLine($"    读取到 {pdfFileData.DiagnosticData.Count} 条诊断数据");
                            await databaseService.SavePdfDataAsync(pdfFileData);
                            Console.WriteLine("    PDF数据已保存到数据库");
                            await LogInfoAsync("PDF数据已保存到数据库", "Database", "SavePdfData", fileName);
                            isSuccess = true;
                        }
                        else
                        {
                            Console.WriteLine($"    不支持的文件格式: {extension}");
                            await LogWarningAsync($"不支持的文件格式: {extension}", "FileProcessing", "ProcessFile", fileName);
                            // 不支持的文件格式，不移动文件，保留在下载目录
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    处理文件失败: {fileName}, 错误: {ex.Message}");
                        await LogErrorAsync($"处理文件失败: {fileName}", ex, "FileProcessing", "ProcessFile", fileName);
                        isSuccess = false;
                    }
                    finally
                    {
                        // 根据处理结果移动文件到相应目录
                        if (!string.IsNullOrEmpty(fileType))
                        {
                            try
                            {
                                if (isSuccess)
                                {
                                    var targetPath = await fileClassificationService.MoveToSuccessAsync(filePath, fileType);
                                    Console.WriteLine($"    文件已移动到成功目录: {targetPath}");
                                }
                                else
                                {
                                    var targetPath = await fileClassificationService.MoveToFailedAsync(filePath, fileType);
                                    Console.WriteLine($"    文件已移动到失败目录: {targetPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"    移动文件失败: {ex.Message}");
                                await LogErrorAsync($"移动文件失败: {fileName}", ex, "FileClassification", "MoveFile", fileName);
                            }
                        }
                    }
                }
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 文件处理完成\n");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// 处理数据映射（生成SQL）
        /// </summary>
        static async Task ProcessDataMappingAsync()
        {
            if (!await _processingLock.WaitAsync(0))
            {
                return; // 有其他任务在执行，跳过
            }

            try
            {
                if (_configuration == null || _loggerFactory == null)
                {
                    return;
                }

                var dataProcessingService = new DataProcessingService(
                    _configuration,
                    _loggerFactory.CreateLogger<DataProcessingService>(),
                    _databaseLogService);

                var (excelCount, pdfCount) = await dataProcessingService.GetUnprocessedFileCountAsync();
                if (excelCount == 0 && pdfCount == 0)
                {
                    return; // 没有需要处理的数据
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 发现未处理数据: Excel={excelCount}, PDF={pdfCount}，开始映射...");
                await LogInfoAsync($"开始数据处理，未处理文件: Excel={excelCount}, PDF={pdfCount}", "DataProcessing", "ProcessData");

                await dataProcessingService.ProcessUnprocessedDataAsync();
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 数据映射完成（SQL已生成并存储）\n");
                await LogInfoAsync("数据处理完成，SQL已生成并存储", "DataProcessing", "ProcessData");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// 处理SQL执行
        /// </summary>
        static async Task ProcessSqlExecutionAsync()
        {
            if (!await _processingLock.WaitAsync(0))
            {
                return; // 有其他任务在执行，跳过
            }

            try
            {
                if (_configuration == null || _loggerFactory == null)
                {
                    return;
                }

                var sqlExecutionService = new SqlExecutionService(
                    _configuration,
                    _loggerFactory.CreateLogger<SqlExecutionService>(),
                    _databaseLogService);

                var connectionString = _configuration.GetConnectionString("SQLServer");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return;
                }

                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                // 获取待执行的SQL配置（支持所有类型的SQL，包括手动插入的）
                var sql = @"
                    SELECT Id, ConfigName, SqlStatement, Parameters
                    FROM SqlExecutionConfig 
                    WHERE IsActive = 1
                      AND (LastExecuteTime IS NULL OR ExecuteCount = 0)
                    ORDER BY ExecutionOrder, Id";

                var sqlConfigs = new List<(int Id, string ConfigName, string SqlStatement, string? Parameters)>();

                using (var command = new SqlCommand(sql, connection))
                {
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
                }

                if (sqlConfigs.Count == 0)
                {
                    return; // 没有待执行的SQL
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 发现 {sqlConfigs.Count} 条待执行的SQL，开始执行...");
                await LogInfoAsync($"开始执行存储的SQL，共 {sqlConfigs.Count} 条", "SqlExecution", "ExecuteStoredSql");

                int successCount = 0;
                int failCount = 0;

                foreach (var (id, configName, sqlStatement, parametersJson) in sqlConfigs)
                {
                    try
                    {
                        Console.WriteLine($"  执行SQL: {configName}");

                        // 解析参数
                        Dictionary<string, object?>? parameters = null;
                        if (!string.IsNullOrWhiteSpace(parametersJson))
                        {
                            try
                            {
                                parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"    参数解析失败: {ex.Message}");
                            }
                        }

                        // 校验SQL
                        var (isValid, errorMessage) = await sqlExecutionService.ValidateSqlAsync(sqlStatement, parameters);
                        if (!isValid)
                        {
                            failCount++;
                            Console.WriteLine($"    校验失败: {errorMessage}");
                            continue;
                        }

                        // 执行SQL
                        var (success, message, rowsAffected) = await sqlExecutionService.ExecuteSqlConfigByIdAsync(id, parameters);
                        if (success)
                        {
                            successCount++;
                            Console.WriteLine($"    执行成功，影响行数: {rowsAffected}");
                        }
                        else
                        {
                            failCount++;
                            Console.WriteLine($"    执行失败: {message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        Console.WriteLine($"    处理异常: {ex.Message}");
                    }
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SQL执行完成: 成功 {successCount} 条, 失败 {failCount} 条\n");
                await LogInfoAsync($"SQL执行完成: 成功 {successCount} 条, 失败 {failCount} 条", "SqlExecution", "ExecuteStoredSql");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        static void InitializeLogging(IConfiguration configuration)
        {
            var logPath = configuration["LogSettings:LogPath"] ?? "Logs";
            var logFileName = configuration["LogSettings:LogFileName"] ?? "app-{Date}.log";
            Directory.CreateDirectory(logPath);

            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(logPath, logFileName),
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: int.Parse(configuration["LogSettings:RetainDays"] ?? "30"),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        /// <summary>
        /// 等待退出
        /// </summary>
        static void WaitForExit()
        {
            if (Console.IsInputRedirected || !Environment.UserInteractive)
            {
                Console.WriteLine("程序将在3秒后自动退出...");
                Task.Delay(3000).Wait();
            }
            else
            {
                Console.WriteLine("按任意键退出...");
                try { Console.ReadKey(); }
                catch { Task.Delay(3000).Wait(); }
            }
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        static async Task LogInfoAsync(string message, string category, string operation, string? fileName = null)
        {
            Serilog.Log.Information(message);
            if (_databaseLogService != null)
            {
                await _databaseLogService.LogInformationAsync(message, category, operation, fileName);
            }
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        static async Task LogWarningAsync(string message, string category, string operation, string? fileName = null)
        {
            Serilog.Log.Warning(message);
            if (_databaseLogService != null)
            {
                await _databaseLogService.LogWarningAsync(message, category, operation, fileName);
            }
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        static async Task LogErrorAsync(string message, Exception ex, string category, string operation, string? fileName = null)
        {
            Serilog.Log.Error(ex, message);
            if (_databaseLogService != null)
            {
                await _databaseLogService.LogErrorAsync(message, ex, category, operation, fileName);
            }
        }

        /// <summary>
        /// 处理数据：生成SQL并存储
        /// </summary>
        static async Task ProcessDataAsync(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            Console.WriteLine("\n=== 开始数据处理（生成SQL）===");
            await LogInfoAsync("开始数据处理", "DataProcessing", "ProcessData");

            var dataProcessingService = new DataProcessingService(
                configuration,
                loggerFactory.CreateLogger<DataProcessingService>(),
                _databaseLogService);

            var (excelCount, pdfCount) = await dataProcessingService.GetUnprocessedFileCountAsync();
            if (excelCount > 0 || pdfCount > 0)
            {
                Console.WriteLine($"未处理的文件: Excel={excelCount}, PDF={pdfCount}");
            }

            await dataProcessingService.ProcessUnprocessedDataAsync();
            Console.WriteLine("=== 数据处理完成（SQL已生成并存储）===");
            await LogInfoAsync("数据处理完成，SQL已生成并存储", "DataProcessing", "ProcessData");
        }

        /// <summary>
        /// 执行存储的SQL
        /// </summary>
        static async Task ExecuteStoredSqlAsync(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            Console.WriteLine("\n=== 开始执行存储的SQL ===");
            await LogInfoAsync("开始执行存储的SQL", "SqlExecution", "ExecuteStoredSql");

            try
            {
                var sqlExecutionService = new SqlExecutionService(
                    configuration,
                    loggerFactory.CreateLogger<SqlExecutionService>(),
                    _databaseLogService);

                var connectionString = configuration.GetConnectionString("SQLServer");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    Console.WriteLine("数据库连接字符串未配置");
                    return;
                }

                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                // 获取待执行的SQL配置（支持所有类型的SQL，包括手动插入的）
                var sql = @"
                    SELECT Id, ConfigName, SqlStatement, Parameters
                    FROM SqlExecutionConfig 
                    WHERE IsActive = 1
                      AND (LastExecuteTime IS NULL OR ExecuteCount = 0)
                    ORDER BY ExecutionOrder, Id";

                var sqlConfigs = new List<(int Id, string ConfigName, string SqlStatement, string? Parameters)>();

                using (var command = new SqlCommand(sql, connection))
                {
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
                }

                if (sqlConfigs.Count == 0)
                {
                    Console.WriteLine("没有待执行的SQL");
                    return;
                }

                Console.WriteLine($"找到 {sqlConfigs.Count} 条待执行的SQL\n");

                int successCount = 0;
                int failCount = 0;

                foreach (var (id, configName, sqlStatement, parametersJson) in sqlConfigs)
                {
                    try
                    {
                        Console.WriteLine($"处理SQL: {configName}");

                        // 解析参数
                        Dictionary<string, object?>? parameters = null;
                        if (!string.IsNullOrWhiteSpace(parametersJson))
                        {
                            try
                            {
                                parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  参数解析失败: {ex.Message}");
                            }
                        }

                        // 校验SQL
                        var (isValid, errorMessage) = await sqlExecutionService.ValidateSqlAsync(sqlStatement, parameters);
                        if (!isValid)
                        {
                            failCount++;
                            Console.WriteLine($"  校验失败: {errorMessage}\n");
                            continue;
                        }

                        // 执行SQL
                        var (success, message, rowsAffected) = await sqlExecutionService.ExecuteSqlConfigByIdAsync(id, parameters);
                        if (success)
                        {
                            successCount++;
                            Console.WriteLine($"  执行成功，影响行数: {rowsAffected}\n");
                        }
                        else
                        {
                            failCount++;
                            Console.WriteLine($"  执行失败: {message}\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        Console.WriteLine($"  处理异常: {ex.Message}\n");
                    }
                }

                Console.WriteLine($"=== SQL执行完成: 成功 {successCount} 条, 失败 {failCount} 条 ===");
                await LogInfoAsync($"SQL执行完成: 成功 {successCount} 条, 失败 {failCount} 条", "SqlExecution", "ExecuteStoredSql");
            }
            catch (Exception ex)
            {
                await LogErrorAsync("执行存储的SQL时发生错误", ex, "SqlExecution", "ExecuteStoredSql");
                throw;
            }
        }
    }
}
