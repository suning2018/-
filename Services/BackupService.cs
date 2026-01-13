using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    public class BackupService
    {
        private readonly string _backupPath;
        private readonly int _keepDays;
        private readonly ILogger<BackupService> _logger;
        private readonly DatabaseLogService? _databaseLogService;

        public BackupService(IConfiguration configuration, ILogger<BackupService> logger, DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
            _backupPath = configuration["BackupSettings:BackupPath"] ?? "Backup";
            _keepDays = int.Parse(configuration["BackupSettings:KeepDays"] ?? "30");
            
            // 确保备份目录存在
            Directory.CreateDirectory(_backupPath);
        }

        /// <summary>
        /// 备份文件
        /// </summary>
        public async Task<string> BackupFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var error = $"要备份的文件不存在: {filePath}";
                _logger.LogError(error);
                throw new FileNotFoundException(error);
            }

            try
            {
                var fileName = Path.GetFileName(filePath);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{timestamp}{Path.GetExtension(fileName)}";
                var backupFilePath = Path.Combine(_backupPath, backupFileName);

                File.Copy(filePath, backupFilePath, true);

                _logger.LogInformation("文件备份成功: {FileName} -> {BackupPath}", fileName, backupFilePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogInformationAsync($"文件备份成功: {fileName}", "Backup", "BackupFile", fileName: fileName, operation: "Backup");
                }

                return backupFilePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备份文件失败: {FilePath}", filePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync($"备份文件失败: {Path.GetFileName(filePath)}", ex, "Backup", "BackupFile", fileName: Path.GetFileName(filePath), operation: "Backup");
                }
                throw new Exception($"备份文件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 清理旧的备份文件
        /// </summary>
        public async Task CleanOldBackupsAsync()
        {
            try
            {
                if (!Directory.Exists(_backupPath))
                {
                    return;
                }

                var files = Directory.GetFiles(_backupPath);
                var cutoffDate = DateTime.Now.AddDays(-_keepDays);
                var deletedCount = 0;

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTime < cutoffDate)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }

                if (deletedCount > 0)
                {
                    var msg = $"已清理 {deletedCount} 个超过 {_keepDays} 天的备份文件。";
                    Console.WriteLine(msg);
                    _logger.LogInformation(msg);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogInformationAsync(msg, "Backup", "CleanOldBackups");
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMsg = $"清理旧备份文件时出错: {ex.Message}";
                Console.WriteLine(errorMsg);
                _logger.LogError(ex, "清理旧备份文件时出错");
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync("清理旧备份文件时出错", ex, "Backup", "CleanOldBackups");
                }
            }
        }
    }
}

