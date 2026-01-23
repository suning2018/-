using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// B5R文件处理服务（Renishaw球杆仪原始数据文件）
    /// B5R和PDF内容相同，只是文件格式不同，复用PDF的提取逻辑
    /// </summary>
    public class B5rService
    {
        private readonly ILogger<B5rService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly PdfService _pdfService;

        public B5rService(ILogger<B5rService> logger, PdfService pdfService, DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
            // 复用PdfService的提取逻辑（B5R和PDF内容相同）
            _pdfService = pdfService;
        }

        /// <summary>
        /// 读取B5R文件内容（内容与PDF相同，只是格式不同）
        /// </summary>
        public Task<B5rFileData> ReadB5rFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            var fileData = new B5rFileData
            {
                SourceFileName = Path.GetFileName(filePath),
                ImportTime = DateTime.Now,
                FileType = "B5R"
            };

            try
            {
                // 读取文件内容（B5R文件是文本格式，内容与PDF相同）
                string textContent;
                try
                {
                    textContent = File.ReadAllText(filePath, Encoding.UTF8);
                }
                catch
                {
                    // 如果UTF-8失败，尝试默认编码
                    textContent = File.ReadAllText(filePath, Encoding.Default);
                }

                // 复用PDF的提取逻辑（内容相同）
                fileData.HeaderInfo = _pdfService.ExtractHeaderInfo(textContent, fileData.SourceFileName);
                fileData.DiagnosticData = _pdfService.ExtractDiagnosticData(textContent, fileData.HeaderInfo);

                // 保存文件大小
                var fileInfo = new FileInfo(filePath);
                fileData.FileSize = fileInfo.Length;
                
                _logger.LogInformation("B5R文件 {FileName} 提取完成: 表头{HeaderCount}项, 诊断数据{DiagCount}项, 文件大小{FileSize}字节", 
                    fileData.SourceFileName, fileData.HeaderInfo.Count, fileData.DiagnosticData.Count, fileData.FileSize);
            }
            catch (Exception ex)
            {
                throw new Exception($"读取B5R文件失败: {ex.Message}", ex);
            }

            return Task.FromResult(fileData);
        }
    }

    /// <summary>
    /// B5R文件数据模型
    /// </summary>
    public class B5rFileData
    {
        public string SourceFileName { get; set; } = string.Empty;
        public DateTime ImportTime { get; set; }
        public string FileType { get; set; } = string.Empty;
        public Dictionary<string, string> HeaderInfo { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> DiagnosticData { get; set; } = new Dictionary<string, string>();
        public long FileSize { get; set; }
    }
}
