using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    public class FtpService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<FtpService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly string _server;
        private readonly string _username;
        private readonly string _password;
        private readonly string _excelRemotePath;
        private readonly string _pdfRemotePath;
        private readonly int _port;
        private readonly string _localPath;
        private readonly string _filePattern;

        public FtpService(IConfiguration configuration, ILogger<FtpService> logger, DatabaseLogService? databaseLogService = null)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseLogService = databaseLogService;
            _server = _configuration["FtpSettings:Server"] ?? string.Empty;
            _username = _configuration["FtpSettings:Username"] ?? string.Empty;
            _password = _configuration["FtpSettings:Password"] ?? string.Empty;
            _excelRemotePath = _configuration["FtpSettings:ExcelRemotePath"] ?? "/Excel";
            _pdfRemotePath = _configuration["FtpSettings:PdfRemotePath"] ?? "/PDF";
            _port = int.Parse(_configuration["FtpSettings:Port"] ?? "21");
            _localPath = _configuration["FileSettings:LocalDownloadPath"] ?? "Downloads";
            _filePattern = _configuration["FileSettings:FilePattern"] ?? "*.xlsx";
        }

        /// <summary>
        /// 获取FTP服务器上的文件列表（从指定目录）
        /// </summary>
        /// <param name="remotePath">FTP远程路径</param>
        /// <param name="fileExtensions">文件扩展名过滤（如 [".xlsx", ".xls"] 或 [".pdf"]）</param>
        private async Task<List<string>> GetFileListAsync(string remotePath, string[] fileExtensions)
        {
            var fileList = new List<string>();

            try
            {
                // 规范化远程路径
                var normalizedPath = remotePath?.Trim() ?? "/";
                if (normalizedPath == "" || normalizedPath == "/")
                {
                    normalizedPath = "/";
                }
                else
                {
                    if (!normalizedPath.StartsWith("/"))
                    {
                        normalizedPath = "/" + normalizedPath;
                    }
                    normalizedPath = normalizedPath.TrimEnd('/');
                }
                
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{_server}:{_port}{normalizedPath}");
                request.Method = WebRequestMethods.Ftp.ListDirectory;
                request.Credentials = new NetworkCredential(_username, _password);
                request.UsePassive = true;
                request.UseBinary = true;
                request.KeepAlive = false;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var responseStream = response.GetResponseStream())
                {
                    if (responseStream == null)
                    {
                        return fileList;
                    }
                    using (var reader = new StreamReader(responseStream))
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                // 检查文件扩展名
                                var extension = Path.GetExtension(line).ToLower();
                                if (fileExtensions.Contains(extension))
                                {
                                    fileList.Add(line);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取FTP文件列表失败: {RemotePath}", remotePath);
                // 如果目录不存在或无法访问，记录警告但不抛出异常，继续处理其他目录
            }

            return fileList;
        }

        /// <summary>
        /// 下载文件（从Excel和PDF两个目录分别下载）
        /// </summary>
        public async Task<List<string>> DownloadFilesAsync()
        {
            var downloadedFiles = new List<string>();

            // 1. 从Excel目录下载Excel文件
            var excelFiles = await GetFileListAsync(_excelRemotePath, new[] { ".xlsx", ".xls" });
            foreach (var fileName in excelFiles)
            {
                var localPath = await DownloadFileAsync(fileName, _excelRemotePath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                }
            }

            // 2. 从PDF目录下载PDF文件
            var pdfFiles = await GetFileListAsync(_pdfRemotePath, new[] { ".pdf" });
            foreach (var fileName in pdfFiles)
            {
                var localPath = await DownloadFileAsync(fileName, _pdfRemotePath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                }
            }

            // 3. 从PDF目录下载B5R文件（B5R文件通常也在PDF目录）
            var b5rFiles = await GetFileListAsync(_pdfRemotePath, new[] { ".b5r" });
            foreach (var fileName in b5rFiles)
            {
                var localPath = await DownloadFileAsync(fileName, _pdfRemotePath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                }
            }

            return downloadedFiles;
        }

        /// <summary>
        /// 下载单个文件
        /// </summary>
        /// <param name="fileName">文件名</param>
        /// <param name="remotePath">FTP远程路径</param>
        /// <returns>下载后的本地文件路径，失败返回null</returns>
        private async Task<string?> DownloadFileAsync(string fileName, string remotePath)
        {
            try
            {
                var localFilePath = Path.Combine(_localPath, fileName);
                
                // 如果文件已存在，跳过下载，但仍尝试删除FTP服务器上的文件
                if (File.Exists(localFilePath))
                {
                    _logger.LogDebug("文件 {FileName} 已存在，跳过下载，但会删除FTP服务器上的文件", fileName);
                    await DeleteRemoteFileAsync(fileName, remotePath);
                    return localFilePath;
                }

                // 规范化远程路径和文件名拼接
                var normalizedPath = remotePath?.Trim() ?? "/";
                if (normalizedPath == "" || normalizedPath == "/")
                {
                    normalizedPath = "/";
                }
                else
                {
                    if (!normalizedPath.StartsWith("/"))
                    {
                        normalizedPath = "/" + normalizedPath;
                    }
                    normalizedPath = normalizedPath.TrimEnd('/');
                }
                
                var fileUrl = normalizedPath == "/" 
                    ? $"ftp://{_server}:{_port}/{fileName}"
                    : $"ftp://{_server}:{_port}{normalizedPath}/{fileName}";
                
                // 下载文件
                var request = (FtpWebRequest)WebRequest.Create(fileUrl);
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                request.Credentials = new NetworkCredential(_username, _password);
                request.UsePassive = true;
                request.UseBinary = true;
                request.KeepAlive = false;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var responseStream = response.GetResponseStream())
                {
                    if (responseStream == null)
                    {
                        throw new Exception("无法获取FTP响应流");
                    }
                    using (var fileStream = new FileStream(localFilePath, FileMode.Create))
                    {
                        await responseStream.CopyToAsync(fileStream);
                    }
                }

                _logger.LogInformation("已下载文件: {FileName} 从 {RemotePath}", fileName, remotePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogInformationAsync(
                        $"已下载文件: {fileName} 从 {remotePath}", 
                        "FTP", 
                        "DownloadFile", 
                        fileName: fileName);
                }

                // 下载成功后删除FTP服务器上的文件
                await DeleteRemoteFileAsync(fileName, remotePath);

                return localFilePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "下载文件失败: {FileName} 从 {RemotePath}", fileName, remotePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync(
                        $"下载文件失败: {fileName}", 
                        ex, 
                        "FTP", 
                        "DownloadFile", 
                        fileName: fileName);
                }
                return null;
            }
        }

        /// <summary>
        /// 删除FTP服务器上的文件
        /// </summary>
        /// <param name="fileName">文件名</param>
        /// <param name="remotePath">FTP远程路径</param>
        private async Task DeleteRemoteFileAsync(string fileName, string? remotePath)
        {
            try
            {
                // 规范化远程路径和文件名拼接
                var normalizedPath = remotePath?.Trim() ?? "/";
                if (normalizedPath == "" || normalizedPath == "/")
                {
                    normalizedPath = "/";
                }
                else
                {
                    if (!normalizedPath.StartsWith("/"))
                    {
                        normalizedPath = "/" + normalizedPath;
                    }
                    normalizedPath = normalizedPath.TrimEnd('/');
                }
                
                var fileUrl = normalizedPath == "/" 
                    ? $"ftp://{_server}:{_port}/{fileName}"
                    : $"ftp://{_server}:{_port}{normalizedPath}/{fileName}";
                
                var request = (FtpWebRequest)WebRequest.Create(fileUrl);
                request.Method = WebRequestMethods.Ftp.DeleteFile;
                request.Credentials = new NetworkCredential(_username, _password);
                request.UsePassive = true;
                request.KeepAlive = false;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                {
                    _logger.LogInformation("已删除FTP服务器上的文件: {FileName} 从 {RemotePath}", fileName, remotePath);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogInformationAsync(
                            $"已删除FTP服务器上的文件: {fileName} 从 {remotePath}", 
                            "FTP", 
                            "DeleteFile", 
                            fileName: fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除FTP服务器上的文件失败: {FileName} 从 {RemotePath}", fileName, remotePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogWarningAsync(
                        $"删除FTP服务器上的文件失败: {fileName}", 
                        "FTP", 
                        "DeleteFile", 
                        fileName: fileName);
                }
                // 删除失败不抛出异常，只记录警告，避免影响后续文件处理
            }
        }
    }
}

