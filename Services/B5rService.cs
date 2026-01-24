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
    /// 使用XML解析方式提取数据
    /// </summary>
    public class B5rService
    {
        private readonly ILogger<B5rService> _logger;
        private readonly DatabaseLogService? _databaseLogService;
        private readonly B5rXmlService _b5rXmlService;

        public B5rService(ILogger<B5rService> logger, B5rXmlService b5rXmlService, DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
            // 使用XML解析服务
            _b5rXmlService = b5rXmlService;
        }

        /// <summary>
        /// 读取B5R文件内容（使用XML解析）
        /// </summary>
        public Task<B5rFileData> ReadB5rFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            try
            {
                // 使用XML解析服务解析文件
                var fileData = _b5rXmlService.ParseB5rFile(filePath);

                return Task.FromResult(fileData);
            }
            catch (Exception ex)
            {
                throw new Exception($"读取B5R文件失败: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// B5R文件数据模型（复用Models.B5rFileData）
    /// 注意：这个类保留是为了向后兼容，建议使用Models.B5rFileData
    /// </summary>
    public class B5rFileData
    {
        public string SourceFileName { get; set; } = string.Empty;
        public DateTime ImportTime { get; set; }
        public string FileType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        // 注意：新版本使用AllFeatures、MustHaveFeatures、ShouldHaveFeatures、GarbageFeatures
        public Dictionary<string, string> HeaderInfo { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> DiagnosticData { get; set; } = new Dictionary<string, string>();
        public List<Models.B5rFeatureData> AllFeatures { get; set; } = new List<Models.B5rFeatureData>();
        public List<Models.B5rFeatureData> MustHaveFeatures { get; set; } = new List<Models.B5rFeatureData>();
        public List<Models.B5rFeatureData> ShouldHaveFeatures { get; set; } = new List<Models.B5rFeatureData>();
        public List<Models.B5rFeatureData> GarbageFeatures { get; set; } = new List<Models.B5rFeatureData>();
        public List<Models.B5rFeatureData> ValidFeatures => MustHaveFeatures.Concat(ShouldHaveFeatures).ToList();
    }
}
