namespace FtpExcelProcessor.Models
{
    /// <summary>
    /// FTP配置
    /// </summary>
    public class FtpSettings
    {
        public string Server { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string RemotePath { get; set; } = "/";
        public int Port { get; set; } = 21;
    }

    /// <summary>
    /// 备份配置
    /// </summary>
    public class BackupSettings
    {
        public string BackupPath { get; set; } = "Backup";
        public int KeepDays { get; set; } = 30;
    }

    /// <summary>
    /// 文件配置
    /// </summary>
    public class FileSettings
    {
        public string LocalDownloadPath { get; set; } = "Downloads";
        public string FilePattern { get; set; } = "*.xlsx,*.pdf";
    }

    /// <summary>
    /// 日志配置
    /// </summary>
    public class LogSettings
    {
        public string LogPath { get; set; } = "Logs";
        public string LogFileName { get; set; } = "app-{Date}.log";
        public int RetainDays { get; set; } = 30;
        public bool EnableDatabaseLog { get; set; } = true;
    }

}

