using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using FtpExcelProcessor.Services;
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
            // 注册全局异常处理
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Console.WriteLine($"未处理的异常: {e.ExceptionObject}");
                Serilog.Log.Fatal((Exception)e.ExceptionObject, "未处理的异常导致程序退出");
                Serilog.Log.CloseAndFlush();
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine($"未观察到的任务异常: {e.Exception}");
                Serilog.Log.Error(e.Exception, "未观察到的任务异常");
                e.SetObserved();
            };

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
                Console.WriteLine($"异常详情: {ex}");
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
                catch (Exception logEx)
                {
                    Console.WriteLine($"记录日志失败: {logEx.Message}");
                }
            }
            finally
            {
                Console.WriteLine("\n程序即将退出，按任意键关闭...");
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
            int loopCount = 0;
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 持续监控已启动，将每 {checkIntervalSeconds} 秒检查一次...\n");
            
            while (_isRunning)
            {
                try
                {
                    // 检查 _isRunning 状态
                    if (!_isRunning)
                    {
                        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 检测到停止信号，退出循环");
                        break;
                    }
                    
                    loopCount++;
                    var startTime = DateTime.Now;
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 开始第 {loopCount} 次检查...");
                    
                    // 1. 检查并处理FTP文件
                    await ProcessFtpFilesAsync();

                    // 2. 检查并处理数据映射
                    await ProcessDataMappingAsync();

                    // 3. 检查并执行SQL
                    await ProcessSqlExecutionAsync();

                    var elapsed = (DateTime.Now - startTime).TotalSeconds;
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 第 {loopCount} 次检查完成（耗时 {elapsed:F2} 秒），等待 {checkIntervalSeconds} 秒后继续...\n");

                    // 等待指定间隔后再次检查
                    if (_isRunning)
                    {
                        try
                        {
                            // 直接使用 Task.Delay，不依赖控制台输入
                            await Task.Delay(checkIntervalSeconds * 1000);
                            
                            if (!_isRunning)
                            {
                                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 检测到停止信号，退出循环");
                                break;
                            }
                            
                            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 等待完成，准备开始第 {loopCount + 1} 次检查...\n");
                        }
                        catch (Exception delayEx)
                        {
                            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 等待期间出错: {delayEx.Message}");
                            Serilog.Log.Error(delayEx, "等待期间出错");
                            // 即使等待出错，也继续循环
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 循环处理出错: {ex.Message}");
                    Console.WriteLine($"异常堆栈: {ex}");
                    Serilog.Log.Error(ex, "循环处理出错");
                    try
                    {
                        await LogErrorAsync("循环处理出错", ex, "Program", "ContinuousProcessLoop");
                    }
                    catch (Exception logEx)
                    {
                        Console.WriteLine($"记录日志失败: {logEx.Message}");
                    }
                    // 出错后等待一段时间再继续，避免快速循环导致资源耗尽
                    if (_isRunning)
                    {
                        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 等待 {checkIntervalSeconds} 秒后继续监控...\n");
                        await Task.Delay(checkIntervalSeconds * 1000);
                    }
                }
            }
            
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 持续监控已停止（共执行 {loopCount} 次检查）");
        }

        /// <summary>
        /// 处理FTP文件下载和处理
        /// </summary>
        static async Task ProcessFtpFilesAsync()
        {
            if (!await _processingLock.WaitAsync(0) || _configuration == null || _loggerFactory == null)
            {
                return;
            }

            try
            {
                var ftpService = new FtpService(_configuration, _loggerFactory.CreateLogger<FtpService>(), _databaseLogService);
                var excelService = new ExcelService(_loggerFactory.CreateLogger<ExcelService>(), _databaseLogService);
                var pdfService = new PdfService(_loggerFactory.CreateLogger<PdfService>(), _databaseLogService);
                var b5rService = new B5rService(_loggerFactory.CreateLogger<B5rService>(), pdfService, _databaseLogService);
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
                    try
                    {
                        await ProcessSingleFileAsync(filePath, excelService, pdfService, b5rService, databaseService, fileClassificationService);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 处理文件 {filePath} 时出错: {ex.Message}");
                        Serilog.Log.Error(ex, "处理文件时出错: {FilePath}", filePath);
                        try
                        {
                            await LogErrorAsync($"处理文件时出错: {filePath}", ex, "FTP", "ProcessFile", Path.GetFileName(filePath));
                        }
                        catch (Exception logEx)
                        {
                            Console.WriteLine($"记录日志失败: {logEx.Message}");
                        }
                    }
                }
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 文件处理完成\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FTP文件处理出错: {ex.Message}");
                Console.WriteLine($"异常堆栈: {ex}");
                Serilog.Log.Error(ex, "FTP文件处理出错");
                try
                {
                    await LogErrorAsync("FTP文件处理出错", ex, "FTP", "ProcessFtpFiles");
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"记录日志失败: {logEx.Message}");
                }
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
            if (!await _processingLock.WaitAsync(0) || _configuration == null || _loggerFactory == null)
            {
                return;
            }

            try
            {
                var dataProcessingService = new DataProcessingService(
                    _configuration,
                    _loggerFactory.CreateLogger<DataProcessingService>(),
                    _databaseLogService);

                var (excelCount, pdfCount, b5rCount) = await dataProcessingService.GetUnprocessedFileCountAsync();
                if (excelCount == 0 && pdfCount == 0 && b5rCount == 0)
                {
                    return; // 没有需要处理的数据
                }

                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 发现未处理数据: Excel={excelCount}, PDF={pdfCount}, B5R={b5rCount}，开始映射...");
                await LogInfoAsync($"开始数据处理，未处理文件: Excel={excelCount}, PDF={pdfCount}, B5R={b5rCount}", "DataProcessing", "ProcessData");

                await dataProcessingService.ProcessUnprocessedDataAsync();
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 数据映射完成（SQL已生成并存储）\n");
                await LogInfoAsync("数据处理完成，SQL已生成并存储", "DataProcessing", "ProcessData");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 数据映射处理出错: {ex.Message}");
                Console.WriteLine($"异常堆栈: {ex}");
                Serilog.Log.Error(ex, "数据映射处理出错");
                try
                {
                    await LogErrorAsync("数据映射处理出错", ex, "DataProcessing", "ProcessData");
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"记录日志失败: {logEx.Message}");
                }
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
            if (!await _processingLock.WaitAsync(0) || _configuration == null || _loggerFactory == null)
            {
                return;
            }

            try
            {
                var sqlExecutionService = new SqlExecutionService(
                    _configuration,
                    _loggerFactory.CreateLogger<SqlExecutionService>(),
                    _databaseLogService);

                var sqlConfigs = await sqlExecutionService.GetPendingSqlConfigsAsync();
                if (sqlConfigs.Count == 0)
                {
                    return;
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

                        var (isValid, errorMessage) = await sqlExecutionService.ValidateSqlAsync(sqlStatement, parameters);
                        if (!isValid)
                        {
                            failCount++;
                            Console.WriteLine($"    校验失败: {errorMessage}");
                            continue;
                        }

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
        /// 处理单个文件
        /// </summary>
        static async Task ProcessSingleFileAsync(
            string filePath,
            ExcelService excelService,
            PdfService pdfService,
            B5rService b5rService,
            DatabaseService databaseService,
            FileClassificationService fileClassificationService)
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
                else if (extension == ".b5r")
                {
                    fileType = "B5R";
                    var b5rFileData = await b5rService.ReadB5rFileAsync(filePath);
                    Console.WriteLine($"    读取到 {b5rFileData.DiagnosticData.Count} 条诊断数据");
                    await databaseService.SaveB5rDataAsync(b5rFileData);
                    Console.WriteLine("    B5R数据已保存到数据库");
                    await LogInfoAsync("B5R数据已保存到数据库", "Database", "SaveB5rData", fileName);
                    isSuccess = true;
                }
                else
                {
                    Console.WriteLine($"    不支持的文件格式: {extension}");
                    await LogWarningAsync($"不支持的文件格式: {extension}", "FileProcessing", "ProcessFile", fileName);
                    return;
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
                if (!string.IsNullOrEmpty(fileType))
                {
                    try
                    {
                        var targetPath = isSuccess
                            ? await fileClassificationService.MoveToSuccessAsync(filePath, fileType)
                            : await fileClassificationService.MoveToFailedAsync(filePath, fileType);
                        Console.WriteLine($"    文件已移动到{(isSuccess ? "成功" : "失败")}目录: {targetPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    移动文件失败: {ex.Message}");
                        await LogErrorAsync($"移动文件失败: {fileName}", ex, "FileClassification", "MoveFile", fileName);
                    }
                }
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

    }
}
