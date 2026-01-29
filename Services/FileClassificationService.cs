using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// 文件分类服务 - 根据文件类型和处理结果将文件移动到不同目录
    /// </summary>
    public class FileClassificationService
    {
        private readonly string _excelSuccessPath;
        private readonly string _excelFailedPath;
        private readonly string _pdfSuccessPath;
        private readonly string _pdfFailedPath;
        private readonly ILogger<FileClassificationService> _logger;
        private readonly DatabaseLogService? _databaseLogService;

        public FileClassificationService(
            IConfiguration configuration,
            ILogger<FileClassificationService> logger,
            DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
            
            // 从配置读取目录路径
            _excelSuccessPath = configuration["FileSettings:ExcelSuccessPath"] ?? "Processed/Excel/Success";
            _excelFailedPath = configuration["FileSettings:ExcelFailedPath"] ?? "Processed/Excel/Failed";
            _pdfSuccessPath = configuration["FileSettings:PdfSuccessPath"] ?? "Processed/PDF/Success";
            _pdfFailedPath = configuration["FileSettings:PdfFailedPath"] ?? "Processed/PDF/Failed";
            
            // 确保所有目录存在
            Directory.CreateDirectory(_excelSuccessPath);
            Directory.CreateDirectory(_excelFailedPath);
            Directory.CreateDirectory(_pdfSuccessPath);
            Directory.CreateDirectory(_pdfFailedPath);
        }

        /// <summary>
        /// 移动文件到成功目录
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="fileType">文件类型（"Excel" 或 "PDF"）</param>
        /// <returns>移动后的文件路径</returns>
        public async Task<string> MoveToSuccessAsync(string filePath, string fileType)
        {
            return await MoveFileAsync(filePath, fileType, isSuccess: true);
        }

        /// <summary>
        /// 移动文件到失败目录
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="fileType">文件类型（"Excel" 或 "PDF"）</param>
        /// <returns>移动后的文件路径</returns>
        public async Task<string> MoveToFailedAsync(string filePath, string fileType)
        {
            return await MoveFileAsync(filePath, fileType, isSuccess: false);
        }

        /// <summary>
        /// 移动文件到指定目录
        /// </summary>
        private async Task<string> MoveFileAsync(string filePath, string fileType, bool isSuccess)
        {
            // 确保使用绝对路径，避免相对路径在不同工作目录下的问题
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
            }

            if (!File.Exists(filePath))
            {
                var error = $"要移动的文件不存在: {filePath}";
                _logger.LogError(error);
                throw new FileNotFoundException(error);
            }

            try
            {
                var fileName = Path.GetFileName(filePath);
                var targetDir = GetTargetDirectory(fileType, isSuccess);
                
                // 如果目标文件已存在，添加时间戳
                var targetPath = Path.Combine(targetDir, fileName);
                if (File.Exists(targetPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var extension = Path.GetExtension(fileName);
                    targetPath = Path.Combine(targetDir, $"{nameWithoutExt}_{timestamp}{extension}");
                }

                // 移动文件
                File.Move(filePath, targetPath);

                var status = isSuccess ? "成功" : "失败";
                var message = $"文件已移动到{status}目录: {fileName} -> {targetPath}";
                _logger.LogInformation(message);
                
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogInformationAsync(
                        message, 
                        "FileClassification", 
                        "MoveFile", 
                        fileName: fileName, 
                        operation: $"MoveTo{status}");
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                var error = $"移动文件失败: {filePath}, 错误: {ex.Message}";
                _logger.LogError(ex, error);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync(
                        $"移动文件失败: {Path.GetFileName(filePath)}", 
                        ex, 
                        "FileClassification", 
                        "MoveFile", 
                        fileName: Path.GetFileName(filePath));
                }
                throw new Exception(error, ex);
            }
        }

        /// <summary>
        /// 获取目标目录
        /// </summary>
        private string GetTargetDirectory(string fileType, bool isSuccess)
        {
            return fileType.ToLower() switch
            {
                "excel" => isSuccess ? _excelSuccessPath : _excelFailedPath,
                "pdf" => isSuccess ? _pdfSuccessPath : _pdfFailedPath,
                "b5r" => isSuccess ? _pdfSuccessPath : _pdfFailedPath, // B5R文件使用PDF目录
                _ => throw new ArgumentException($"不支持的文件类型: {fileType}", nameof(fileType))
            };
        }
    }
}
