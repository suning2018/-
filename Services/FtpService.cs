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
        private readonly bool _useUtf8;
        private readonly bool _fallbackToGbk;
        private System.Text.Encoding _currentEncoding;

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
            _useUtf8 = bool.Parse(_configuration["FtpSettings:UseUtf8"] ?? "true");
            _fallbackToGbk = bool.Parse(_configuration["FtpSettings:FallbackToGbk"] ?? "true");
            _currentEncoding = _useUtf8 ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding("GBK");
        }

        /// <summary>
        /// 规范化FTP路径
        /// </summary>
        private string NormalizePath(string path)
        {
            var normalized = path?.Trim() ?? "/";
            if (normalized == "" || normalized == "/")
                return "/";

            if (!normalized.StartsWith("/"))
                normalized = "/" + normalized;

            return normalized.TrimEnd('/');
        }

        /// <summary>
        /// 构建文件URL
        /// </summary>
        private string BuildFileUrl(string normalizedPath, string fileName)
        {
            return normalizedPath == "/"
                ? $"ftp://{_server}:{_port}/{fileName}"
                : $"ftp://{_server}:{_port}{normalizedPath}/{fileName}";
        }

        /// <summary>
        /// 使用指定编码获取文件列表
        /// </summary>
        private async Task<List<string>> GetFileListAsyncWithEncoding(string remotePath, System.Text.Encoding encoding, string[] fileExtensions)
        {
            var fileList = new List<string>();

            try
            {
                var normalizedPath = NormalizePath(remotePath);
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{_server}:{_port}{normalizedPath}");
                request.Method = WebRequestMethods.Ftp.ListDirectory;
                request.Credentials = new NetworkCredential(_username, _password);
                request.UsePassive = true;
                request.UseBinary = true;
                request.KeepAlive = false;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, encoding))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var extension = Path.GetExtension(line).ToLower();
                            if (fileExtensions.Contains(extension))
                            {
                                fileList.Add(line);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "使用 {Encoding} 编码获取文件列表失败: {RemotePath}", encoding.EncodingName, remotePath);
            }

            return fileList;
        }

        /// <summary>
        /// 获取FTP服务器上的文件列表（从指定目录）
        /// </summary>
        /// <param name="remotePath">FTP远程路径</param>
        /// <param name="fileExtensions">文件扩展名过滤（如 [".xlsx", ".xls"] 或 [".pdf"]）</param>
        private async Task<List<string>> GetFileListAsync(string remotePath, string[] fileExtensions)
        {
            // 先尝试当前编码
            var fileList = await GetFileListAsyncWithEncoding(remotePath, _currentEncoding, fileExtensions);

            // 如果没有获取到文件且允许回退，尝试另一种编码
            if (fileList.Count == 0 && _fallbackToGbk)
            {
                var fallbackEncoding = _currentEncoding == System.Text.Encoding.UTF8
                    ? System.Text.Encoding.GetEncoding("GBK")
                    : System.Text.Encoding.UTF8;

                _logger.LogInformation("当前编码未获取到文件，尝试使用 {Encoding} 编码", fallbackEncoding.EncodingName);
                fileList = await GetFileListAsyncWithEncoding(remotePath, fallbackEncoding, fileExtensions);

                // 如果成功，更新当前编码
                if (fileList.Count > 0)
                {
                    _currentEncoding = fallbackEncoding;
                    _logger.LogInformation("切换到 {Encoding} 编码成功", fallbackEncoding.EncodingName);
                }
            }

            return fileList;
        }

        /// <summary>
        /// 获取FTP服务器上的文件夹列表（从指定目录）
        /// </summary>
        /// <param name="remotePath">FTP远程路径</param>
        private async Task<List<string>> GetFolderListAsync(string remotePath)
        {
            // 先尝试当前编码
            var folderList = await GetFolderListAsyncWithEncoding(remotePath, _currentEncoding);

            // 如果没有获取到文件夹且允许回退，尝试另一种编码
            if (folderList.Count == 0 && _fallbackToGbk)
            {
                var fallbackEncoding = _currentEncoding == System.Text.Encoding.UTF8
                    ? System.Text.Encoding.GetEncoding("GBK")
                    : System.Text.Encoding.UTF8;

                _logger.LogDebug("当前编码未获取到文件夹，尝试使用 {Encoding} 编码", fallbackEncoding.EncodingName);
                folderList = await GetFolderListAsyncWithEncoding(remotePath, fallbackEncoding);
            }

            return folderList;
        }

        /// <summary>
        /// 使用指定编码获取文件夹列表
        /// </summary>
        private async Task<List<string>> GetFolderListAsyncWithEncoding(string remotePath, System.Text.Encoding encoding)
        {
            var folderList = new List<string>();

            try
            {
                var normalizedPath = NormalizePath(remotePath);
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{_server}:{_port}{normalizedPath}");
                request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
                request.Credentials = new NetworkCredential(_username, _password);
                request.UsePassive = true;
                request.UseBinary = true;
                request.KeepAlive = false;

                using (var response = (FtpWebResponse)await request.GetResponseAsync())
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, encoding))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            // 解析详细列表，识别文件夹
                            // Unix格式: drwxr-xr-x 1 owner group 4096 Jan 01 12:00 foldername
                            // Windows格式: 01-01-20  12:00PM    <DIR>          foldername
                            try
                            {
                                if (IsFolder(line))
                                {
                                    var folderName = ExtractFolderName(line);
                                    if (!string.IsNullOrEmpty(folderName) && folderName != "." && folderName != "..")
                                    {
                                        folderList.Add(folderName);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "解析文件夹行失败，跳过该行: {Line}", line);
                                // 继续处理下一行，不中断整个流程
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "使用 {Encoding} 编码获取FTP文件夹列表失败: {RemotePath}", encoding.EncodingName, remotePath);
            }

            return folderList;
        }

        /// <summary>
        /// 判断是否为文件夹
        /// </summary>
        private bool IsFolder(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmedLine = line.Trim();

            // Unix格式: 以'd'开头表示文件夹
            // 例如: drwxr-xr-x 1 owner group 4096 Jan 01 12:00 foldername
            if (trimmedLine.Length > 0 && trimmedLine[0] == 'd')
            {
                return true;
            }

            // Windows格式: 包含"<DIR>"标记
            // 例如: 01-01-20  12:00PM    <DIR>          foldername
            if (trimmedLine.Contains("<DIR>"))
            {
                return true;
            }

            // 有些FTP服务器可能使用其他标记
            // 例如: drwxrwxrwx    0 Jan 01 00:00 foldername
            if (trimmedLine.StartsWith("drw") || trimmedLine.StartsWith("d-r"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从详细列表行中提取文件夹名称
        /// </summary>
        private string ExtractFolderName(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            try
            {
                var trimmedLine = line.Trim();

                // Windows格式: 最后一部分是文件夹名
                // 例如: 01-01-20  12:00PM    <DIR>          foldername
                if (trimmedLine.Contains("<DIR>"))
                {
                    var parts = trimmedLine.Split(new[] { "<DIR>" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        return parts[1].Trim();
                    }
                }

                // Unix格式: 最后一部分是文件夹名
                // 例如: drwxr-xr-x 1 owner group 4096 Jan 01 12:00 foldername
                // 或者: drwxrwxrwx    0 Jan 01 00:00 foldername
                var lastSpaceIndex = trimmedLine.LastIndexOf(' ');
                if (lastSpaceIndex > 0)
                {
                    return trimmedLine.Substring(lastSpaceIndex + 1).Trim();
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "提取文件夹名称失败: {Line}", line);
                return string.Empty;
            }
        }

        /// <summary>
        /// 递归获取文件夹内的所有文件
        /// </summary>
        /// <param name="remotePath">FTP远程路径</param>
        /// <param name="fileExtensions">文件扩展名过滤</param>
        private async Task<List<(string FileName, string FolderPath)>> GetAllFilesInFolderAsync(string remotePath, string[] fileExtensions)
        {
            var allFiles = new List<(string FileName, string FolderPath)>();

            if (string.IsNullOrWhiteSpace(remotePath))
            {
                _logger.LogWarning("获取文件夹内文件失败: 路径为空");
                return allFiles;
            }

            try
            {
                var normalizedPath = NormalizePath(remotePath);

                // 1. 获取当前目录的文件列表
                try
                {
                    var files = await GetFileListAsync(remotePath, fileExtensions);
                    foreach (var fileName in files)
                    {
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            allFiles.Add((fileName, remotePath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取文件列表失败: {RemotePath}", remotePath);
                    // 继续尝试获取文件夹列表
                }

                // 2. 获取当前目录的文件夹列表
                try
                {
                    var folders = await GetFolderListAsync(remotePath);
                    foreach (var folderName in folders)
                    {
                        if (!string.IsNullOrEmpty(folderName) && folderName != "." && folderName != "..")
                        {
                            // 递归获取子文件夹内的文件
                            var subFolderPath = normalizedPath == "/"
                                ? $"/{folderName}"
                                : $"{normalizedPath}/{folderName}";

                            try
                            {
                                var subFiles = await GetAllFilesInFolderAsync(subFolderPath, fileExtensions);
                                allFiles.AddRange(subFiles);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "递归获取子文件夹内文件失败: {SubFolderPath}", subFolderPath);
                                // 继续处理其他子文件夹
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取文件夹列表失败: {RemotePath}", remotePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "递归获取文件夹内文件失败: {RemotePath}", remotePath);
            }

            return allFiles;
        }

        /// <summary>
        /// 下载文件（从Excel和PDF两个目录分别下载，支持文件夹）
        /// </summary>
        public async Task<List<string>> DownloadFilesAsync()
        {
            var downloadedFiles = new List<string>();
            var processedFolders = new HashSet<string>();

            // 1. 从Excel目录下载Excel文件（包括子文件夹）
            var allExcelFiles = await GetAllFilesInFolderAsync(_excelRemotePath, new[] { ".xlsx", ".xls" });
            foreach (var (fileName, folderPath) in allExcelFiles)
            {
                var localPath = await DownloadFileAsync(fileName, folderPath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                    // 记录处理过的文件夹（不包括根目录）
                    if (folderPath != _excelRemotePath)
                    {
                        processedFolders.Add(folderPath);
                    }
                }
            }

            // 2. 从PDF目录下载PDF和B5R文件（包括子文件夹）
            var allPdfFiles = await GetAllFilesInFolderAsync(_pdfRemotePath, new[] { ".pdf" });
            foreach (var (fileName, folderPath) in allPdfFiles)
            {
                var localPath = await DownloadFileAsync(fileName, folderPath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                    // 记录处理过的文件夹（不包括根目录）
                    if (folderPath != _pdfRemotePath)
                    {
                        processedFolders.Add(folderPath);
                    }
                }
            }

            // 3. 从PDF目录下载B5R文件（包括子文件夹）
            var allB5rFiles = await GetAllFilesInFolderAsync(_pdfRemotePath, new[] { ".b5r" });
            foreach (var (fileName, folderPath) in allB5rFiles)
            {
                var localPath = await DownloadFileAsync(fileName, folderPath);
                if (!string.IsNullOrEmpty(localPath))
                {
                    downloadedFiles.Add(localPath);
                    // 记录处理过的文件夹（不包括根目录）
                    if (folderPath != _pdfRemotePath)
                    {
                        processedFolders.Add(folderPath);
                    }
                }
            }

            // 4. 删除所有处理过的文件夹（递归删除）
            foreach (var folderPath in processedFolders)
            {
                await DeleteRemoteFolderAsync(folderPath);
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
                // 使用绝对路径，避免相对路径在不同工作目录下的问题
                var localFilePath = Path.Combine(Directory.GetCurrentDirectory(), _localPath, fileName);

                // 如果文件已存在，跳过下载，但仍尝试删除FTP服务器上的文件
                if (File.Exists(localFilePath))
                {
                    _logger.LogDebug("文件 {FileName} 已存在，跳过下载，但会删除FTP服务器上的文件", fileName);
                    await DeleteRemoteFileAsync(fileName, remotePath);
                    return localFilePath;
                }

                var normalizedPath = NormalizePath(remotePath);
                var fileUrl = BuildFileUrl(normalizedPath, fileName);

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

                _logger.LogInformation("已下载文件: {FileName} (编码: {Encoding})", fileName, _currentEncoding.EncodingName);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogInformationAsync(
                        $"已下载文件: {fileName} (编码: {_currentEncoding.EncodingName})",
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
                var normalizedPath = NormalizePath(remotePath ?? "/");
                var fileUrl = BuildFileUrl(normalizedPath, fileName);

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

        /// <summary>
        /// 递归删除FTP服务器上的文件夹及其所有内容
        /// </summary>
        /// <param name="remotePath">FTP远程路径</param>
        private async Task DeleteRemoteFolderAsync(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                _logger.LogWarning("删除FTP文件夹失败: 路径为空");
                return;
            }

            try
            {
                var normalizedPath = NormalizePath(remotePath);
                _logger.LogInformation("开始删除FTP文件夹: {RemotePath}", remotePath);

                // 1. 递归删除文件夹内的所有文件
                try
                {
                    var allFiles = await GetAllFilesInFolderAsync(remotePath, new[] { ".xlsx", ".xls", ".pdf", ".b5r" });
                    foreach (var (fileName, filePath) in allFiles)
                    {
                        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(filePath))
                        {
                            await DeleteRemoteFileAsync(fileName, filePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除文件夹内文件时发生错误: {RemotePath}", remotePath);
                    // 继续尝试删除文件夹本身
                }

                // 2. 递归删除所有子文件夹
                try
                {
                    var subFolders = await GetFolderListAsync(remotePath);
                    foreach (var subFolderName in subFolders)
                    {
                        if (!string.IsNullOrEmpty(subFolderName) && subFolderName != "." && subFolderName != "..")
                        {
                            var subFolderPath = normalizedPath == "/"
                                ? $"/{subFolderName}"
                                : $"{normalizedPath}/{subFolderName}";

                            await DeleteRemoteFolderAsync(subFolderPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除子文件夹时发生错误: {RemotePath}", remotePath);
                    // 继续尝试删除文件夹本身
                }

                // 3. 删除文件夹本身
                try
                {
                    var folderUrl = normalizedPath == "/"
                        ? $"ftp://{_server}:{_port}/"
                        : $"ftp://{_server}:{_port}{normalizedPath}";

                    var request = (FtpWebRequest)WebRequest.Create(folderUrl);
                    request.Method = WebRequestMethods.Ftp.RemoveDirectory;
                    request.Credentials = new NetworkCredential(_username, _password);
                    request.UsePassive = true;
                    request.UseBinary = false; // 删除文件夹使用ASCII模式
                    request.KeepAlive = false;

                    using (var response = (FtpWebResponse)await request.GetResponseAsync())
                    {
                        _logger.LogInformation("已删除FTP文件夹: {RemotePath}", remotePath);
                        if (_databaseLogService != null)
                        {
                            await _databaseLogService.LogInformationAsync(
                                $"已删除FTP文件夹: {remotePath}",
                                "FTP",
                                "DeleteFolder");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除FTP文件夹本身失败: {RemotePath}", remotePath);
                    if (_databaseLogService != null)
                    {
                        await _databaseLogService.LogWarningAsync(
                            $"删除FTP文件夹本身失败: {remotePath}",
                            "FTP",
                            "DeleteFolder");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除FTP文件夹过程中发生未处理的异常: {RemotePath}", remotePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogWarningAsync(
                        $"删除FTP文件夹过程中发生未处理的异常: {remotePath}",
                        "FTP",
                        "DeleteFolder");
                }
                // 删除失败不抛出异常，只记录警告，避免影响后续文件处理
            }
        }
    }
}

