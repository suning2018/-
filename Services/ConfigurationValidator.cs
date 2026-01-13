using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 配置验证器
    /// </summary>
    public static class ConfigurationValidator
    {
        /// <summary>
        /// 验证所有配置项
        /// </summary>
        public static void Validate(IConfiguration configuration)
        {
            var errors = new List<string>();

            // 验证数据库连接字符串
            var dbConnectionString = configuration.GetConnectionString("SQLServer");
            if (string.IsNullOrWhiteSpace(dbConnectionString))
            {
                errors.Add("数据库连接字符串未配置（ConnectionStrings:SQLServer）");
            }

            // 验证FTP配置
            var ftpServer = configuration["FtpSettings:Server"];
            if (string.IsNullOrWhiteSpace(ftpServer))
            {
                errors.Add("FTP服务器地址未配置（FtpSettings:Server）");
            }

            var ftpUsername = configuration["FtpSettings:Username"];
            if (string.IsNullOrWhiteSpace(ftpUsername) || ftpUsername == "your_username")
            {
                errors.Add("FTP用户名未配置或使用默认值（FtpSettings:Username）");
            }

            var ftpPassword = configuration["FtpSettings:Password"];
            if (string.IsNullOrWhiteSpace(ftpPassword) || ftpPassword == "your_password")
            {
                errors.Add("FTP密码未配置或使用默认值（FtpSettings:Password）");
            }

            var ftpPort = configuration["FtpSettings:Port"];
            if (string.IsNullOrWhiteSpace(ftpPort) || !int.TryParse(ftpPort, out int port) || port <= 0 || port > 65535)
            {
                errors.Add("FTP端口配置无效（FtpSettings:Port），必须是1-65535之间的数字");
            }

            // 验证路径配置
            var downloadPath = configuration["FileSettings:LocalDownloadPath"];
            if (string.IsNullOrWhiteSpace(downloadPath))
            {
                errors.Add("本地下载路径未配置（FileSettings:LocalDownloadPath）");
            }

            var backupPath = configuration["BackupSettings:BackupPath"];
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                errors.Add("备份路径未配置（BackupSettings:BackupPath）");
            }

            // 验证日志配置
            var logRetainDays = configuration["LogSettings:RetainDays"];
            if (!string.IsNullOrWhiteSpace(logRetainDays) && 
                (!int.TryParse(logRetainDays, out int days) || days < 0))
            {
                errors.Add("日志保留天数配置无效（LogSettings:RetainDays），必须是非负整数");
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "配置验证失败：\n" + string.Join("\n", errors));
            }
        }
    }
}

