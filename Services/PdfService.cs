using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// PDF文件处理服务（Renishaw球杆仪诊断报告）
    /// </summary>
    public class PdfService
    {
        private readonly ILogger<PdfService> _logger;
        private readonly DatabaseLogService? _databaseLogService;

        public PdfService(ILogger<PdfService> logger, DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
        }

        /// <summary>
        /// 读取PDF文件内容（Renishaw球杆仪诊断报告格式）
        /// </summary>
        public Task<PdfFileData> ReadPdfFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            var fileData = new PdfFileData
            {
                SourceFileName = Path.GetFileName(filePath),
                ImportTime = DateTime.Now,
                FileType = "PDF"
            };

            try
            {
                using (var document = PdfDocument.Open(filePath))
                {
                    var allText = new List<string>();

                    // 提取所有页面的文本
                    foreach (var page in document.GetPages())
                    {
                        var pageText = page.Text;
                        allText.Add(pageText);
                    }

                    // 合并所有文本
                    var fullText = string.Join("\n", allText);

                    // 提取表头信息（传入文件名以便从文件名提取序列号）
                    fileData.HeaderInfo = ExtractHeaderInfo(fullText, fileData.SourceFileName);

                    // 提取诊断数据（传入HeaderInfo以获取XY/YZ/ZX标识）
                    fileData.DiagnosticData = ExtractDiagnosticData(fullText, fileData.HeaderInfo);
                    
                    _logger.LogInformation("PDF文件 {FileName} 提取完成: 表头{HeaderCount}项, 诊断数据{DiagCount}项", 
                        fileData.SourceFileName, fileData.HeaderInfo.Count, fileData.DiagnosticData.Count);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"读取PDF文件失败: {ex.Message}", ex);
            }

            return Task.FromResult(fileData);
        }

        /// <summary>
        /// 提取表头信息
        /// </summary>
        /// <param name="text">PDF文本内容</param>
        /// <param name="sourceFileName">源文件名（用于从文件名提取序列号）</param>
        internal Dictionary<string, string> ExtractHeaderInfo(string text, string sourceFileName = "")
        {
            var headerInfo = new Dictionary<string, string>();

            // 提取测试标识（如：ZX 220度 150mm 20251212-084641）
            var testIdMatch = Regex.Match(text, @"(ZX|XY|YZ)\s+\d+\s*度\s+\d+\s*mm\s+\d{8}-\d{6}");
            if (testIdMatch.Success)
            {
                headerInfo["TestId"] = testIdMatch.Value;
            }

            // 提取操作者
            var operatorMatch = Regex.Match(text, @"操作者[：:]\s*(\S+)");
            if (operatorMatch.Success)
            {
                headerInfo["Operator"] = operatorMatch.Groups[1].Value;
            }

            // 提取日期时间
            var dateMatch = Regex.Match(text, @"日期[：:]\s*(\d{4}[-\/年]\d{1,2}[-\/月]\d{1,2}\s+\d{2}:\d{2}:\d{2})");
            if (dateMatch.Success)
            {
                headerInfo["TestDate"] = dateMatch.Groups[1].Value;
            }

            // 提取机器名称（常用格式）
            var machinePatterns = new[]
            {
                @"机器名称\s*([^\s\n\r,，]+)",              // 机器名称T-V856S
                @"机器[：:]\s*([^\n\r,，]+)",              // 机器: XXX
                @"Machine[：:]\s*([^\n\r,，]+)",           // Machine: XXX
                @"设备[：:]\s*([^\n\r,，]+)"               // 设备: XXX
            };
            
            foreach (var pattern in machinePatterns)
            {
                var machineMatch = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (machineMatch.Success)
                {
                    var machineValue = machineMatch.Groups[1].Value.Trim();
                    // 清理可能包含的其他字段（如QC20-W等）
                    if (machineValue.Contains("QC20") || machineValue.Contains("上次校准"))
                    {
                        var parts = machineValue.Split(new[] { ",", "，", "QC20", "上次校准" }, StringSplitOptions.RemoveEmptyEntries);
                        machineValue = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                    }
                    
                    if (!string.IsNullOrWhiteSpace(machineValue))
                    {
                        headerInfo["Machine"] = machineValue;
                        _logger.LogInformation("成功提取Machine字段: {Machine}", machineValue);
                        break;
                    }
                }
            }
            
            // 如果未提取到Machine，记录警告并显示文本片段
            if (!headerInfo.ContainsKey("Machine"))
            {
                var textPreview = text.Length > 500 ? text.Substring(0, 500) : text;
                _logger.LogWarning("未能从PDF文本中提取到Machine字段。文本预览: {TextPreview}", textPreview);
            }

            // 提取QC20-W信息
            var qcMatch = Regex.Match(text, @"QC20-W[：:]\s*([A-F0-9]+)");
            if (qcMatch.Success)
            {
                headerInfo["QC20W"] = qcMatch.Groups[1].Value.Trim();
            }

            // 提取上次校准日期
            var calMatch = Regex.Match(text, @"上次校准[：:]\s*(\d{4}-\d{2}-\d{2})");
            if (calMatch.Success)
            {
                headerInfo["LastCalibration"] = calMatch.Groups[1].Value;
            }

            // 提取序列号（格式：序列号172600163），存入PartName字段
            var serialMatch = Regex.Match(text, @"序列号\s*(\d+)");
            if (serialMatch.Success)
            {
                headerInfo["PartName"] = serialMatch.Groups[1].Value;
                _logger.LogInformation("从PDF文本中提取序列号并存入PartName: {PartName}", headerInfo["PartName"]);
            }
            else if (!string.IsNullOrEmpty(sourceFileName))
            {
                // 如果PDF文本中没有序列号，尝试从文件名中提取（格式：YZ_172600163.pdf）
                var fileNameMatch = Regex.Match(sourceFileName, @"_(\d+)\.pdf", RegexOptions.IgnoreCase);
                if (fileNameMatch.Success)
                {
                    headerInfo["PartName"] = fileNameMatch.Groups[1].Value;
                    _logger.LogInformation("从文件名中提取序列号并存入PartName: {PartName}", headerInfo["PartName"]);
                }
            }

            return headerInfo;
        }

        /// <summary>
        /// 提取诊断数据（性能指标）
        /// </summary>
        internal Dictionary<string, string> ExtractDiagnosticData(string text, Dictionary<string, string> headerInfo)
        {
            var diagnosticData = new Dictionary<string, string>();
            
            // 从TestId中提取XY/YZ/ZX标识（如：ZX 220度 150mm 20251212-084641）
            string axisIdentifier = string.Empty;
            if (headerInfo.ContainsKey("TestId"))
            {
                var testId = headerInfo["TestId"];
                var axisMatch = Regex.Match(testId, @"\b(ZX|XY|YZ)\b");
                if (axisMatch.Success)
                {
                    axisIdentifier = axisMatch.Groups[1].Value;
                }
            }
            
            // 如果没有从TestId中提取到，尝试从文本中直接查找
            if (string.IsNullOrEmpty(axisIdentifier))
            {
                var axisMatch = Regex.Match(text, @"\b(ZX|XY|YZ)\b");
                if (axisMatch.Success)
                {
                    axisIdentifier = axisMatch.Groups[1].Value;
                }
            }
            
            // 构建键名后缀（如果有标识则添加，否则为空）
            string keySuffix = string.IsNullOrEmpty(axisIdentifier) ? string.Empty : $"_{axisIdentifier}";

            // 提取反向间隙X（格式：22%反向间隙 X-1.5µm-0.9µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var reversalXMatch = Regex.Match(text, @"(\d+)%\s*反向间隙\s*X\s*([-\d.]+)\s*µm\s*([-\d.]+)\s*µm");
            if (!reversalXMatch.Success)
            {
                // 改进：先匹配可能包含负号的模式，确保负号被捕获
                reversalXMatch = Regex.Match(text, @"(\d+)%反向间隙\s*X[^\d\-]*(-?\d+\.?\d*)\s*µm[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (reversalXMatch.Success)
            {
                diagnosticData[$"反向间隙X{keySuffix}_百分比"] = reversalXMatch.Groups[1].Value + "%";
                diagnosticData[$"反向间隙X{keySuffix}_值1"] = reversalXMatch.Groups[2].Value + "µm";
                if (reversalXMatch.Groups.Count > 3 && !string.IsNullOrEmpty(reversalXMatch.Groups[3].Value))
                {
                    diagnosticData[$"反向间隙X{keySuffix}_值2"] = reversalXMatch.Groups[3].Value + "µm";
                }
            }

            // 提取横向间隙Z（格式：19%横向间隙 Z1.4µm1.2µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var lateralZMatch = Regex.Match(text, @"(\d+)%\s*横向间隙\s*Z\s*([-\d.]+)\s*µm\s*([-\d.]+)\s*µm");
            if (!lateralZMatch.Success)
            {
                // 改进：确保负号被捕获
                lateralZMatch = Regex.Match(text, @"(\d+)%横向间隙\s*Z[^\d\-]*(-?\d+\.?\d*)\s*µm[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (lateralZMatch.Success)
            {
                diagnosticData[$"横向间隙Z{keySuffix}_百分比"] = lateralZMatch.Groups[1].Value + "%";
                diagnosticData[$"横向间隙Z{keySuffix}_值1"] = lateralZMatch.Groups[2].Value + "µm";
                if (lateralZMatch.Groups.Count > 3 && !string.IsNullOrEmpty(lateralZMatch.Groups[3].Value))
                {
                    diagnosticData[$"横向间隙Z{keySuffix}_值2"] = lateralZMatch.Groups[3].Value + "µm";
                }
            }

            // 提取反向跃冲X（格式：10%反向跃冲X-0.2µm0.7µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var spikeXMatch = Regex.Match(text, @"(\d+)%\s*反向跃冲\s*X\s*([-\d.]+)\s*µm\s*([-\d.]+)\s*µm");
            if (!spikeXMatch.Success)
            {
                // 改进：确保负号被捕获
                spikeXMatch = Regex.Match(text, @"(\d+)%反向跃冲X[^\d\-]*(-?\d+\.?\d*)\s*µm[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (spikeXMatch.Success)
            {
                diagnosticData[$"反向跃冲X{keySuffix}_百分比"] = spikeXMatch.Groups[1].Value + "%";
                diagnosticData[$"反向跃冲X{keySuffix}_值1"] = spikeXMatch.Groups[2].Value + "µm";
                if (spikeXMatch.Groups.Count > 3 && !string.IsNullOrEmpty(spikeXMatch.Groups[3].Value))
                {
                    diagnosticData[$"反向跃冲X{keySuffix}_值2"] = spikeXMatch.Groups[3].Value + "µm";
                }
            }

            // 提取反向跃冲Y（格式：18%反向跃冲Y1.2µm-1.8µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var spikeYMatch = Regex.Match(text, @"(\d+)%\s*反向跃冲\s*Y\s*([-\d.]+)\s*µm\s*([-\d.]+)\s*µm");
            if (!spikeYMatch.Success)
            {
                // 改进：确保负号被捕获
                spikeYMatch = Regex.Match(text, @"(\d+)%反向跃冲Y[^\d\-]*(-?\d+\.?\d*)\s*µm[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (spikeYMatch.Success)
            {
                diagnosticData[$"反向跃冲Y{keySuffix}_百分比"] = spikeYMatch.Groups[1].Value + "%";
                diagnosticData[$"反向跃冲Y{keySuffix}_值1"] = spikeYMatch.Groups[2].Value + "µm";
                if (spikeYMatch.Groups.Count > 3 && !string.IsNullOrEmpty(spikeYMatch.Groups[3].Value))
                {
                    diagnosticData[$"反向跃冲Y{keySuffix}_值2"] = spikeYMatch.Groups[3].Value + "µm";
                }
            }

            // 提取反向间隙Y（格式：16%反向间隙 Y-0.8µm-1.6µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var reversalYMatch = Regex.Match(text, @"(\d+)%\s*反向间隙\s*Y\s*([-\d.]+)\s*µm\s*([-\d.]+)\s*µm");
            if (!reversalYMatch.Success)
            {
                // 改进：先匹配可能包含负号的模式，确保负号被捕获
                // 匹配：16%反向间隙 Y 后面可能有特殊字符，然后是负号（可选）+数字
                reversalYMatch = Regex.Match(text, @"(\d+)%反向间隙\s*Y[^\d\-]*(-?\d+\.?\d*)\s*µm[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (!reversalYMatch.Success)
            {
                // 备用模式：处理只有单个值的情况（如：16% 反向间隙 Y -0.8µm）
                reversalYMatch = Regex.Match(text, @"(\d+)%\s*反向间隙\s*Y[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
                if (reversalYMatch.Success)
                {
                    diagnosticData[$"反向间隙Y{keySuffix}_百分比"] = reversalYMatch.Groups[1].Value + "%";
                    diagnosticData[$"反向间隙Y{keySuffix}_值1"] = reversalYMatch.Groups[2].Value + "µm";
                }
            }
            if (reversalYMatch.Success && reversalYMatch.Groups.Count > 3 && !string.IsNullOrEmpty(reversalYMatch.Groups[3].Value))
            {
                diagnosticData[$"反向间隙Y{keySuffix}_百分比"] = reversalYMatch.Groups[1].Value + "%";
                diagnosticData[$"反向间隙Y{keySuffix}_值1"] = reversalYMatch.Groups[2].Value + "µm";
                diagnosticData[$"反向间隙Y{keySuffix}_值2"] = reversalYMatch.Groups[3].Value + "µm";
            }
            else if (reversalYMatch.Success)
            {
                diagnosticData[$"反向间隙Y{keySuffix}_百分比"] = reversalYMatch.Groups[1].Value + "%";
                diagnosticData[$"反向间隙Y{keySuffix}_值1"] = reversalYMatch.Groups[2].Value + "µm";
            }

            // 提取垂直度（格式：15%垂直度-9.7µm/m，支持无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var verticalityMatch = Regex.Match(text, @"(\d+)%\s*垂直度\s*([-\d.]+)\s*µm/m");
            if (!verticalityMatch.Success)
            {
                // 改进：确保负号被捕获
                verticalityMatch = Regex.Match(text, @"(\d+)%垂直度[^\d\-]*(-?\d+\.?\d*)\s*µm/m", RegexOptions.Singleline);
            }
            if (verticalityMatch.Success)
            {
                diagnosticData[$"垂直度{keySuffix}_百分比"] = verticalityMatch.Groups[1].Value + "%";
                diagnosticData[$"垂直度{keySuffix}_值"] = verticalityMatch.Groups[2].Value + "µm/m";
            }

            // 提取反向跃冲Z（格式：13%反向跃冲Z1.3µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var spikeZMatch = Regex.Match(text, @"(\d+)%\s*反向跃冲\s*Z\s*([-\d.]+)\s*µm");
            if (!spikeZMatch.Success)
            {
                // 改进：确保负号被捕获
                spikeZMatch = Regex.Match(text, @"(\d+)%反向跃冲Z[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (spikeZMatch.Success)
            {
                diagnosticData[$"反向跃冲Z{keySuffix}_百分比"] = spikeZMatch.Groups[1].Value + "%";
                diagnosticData[$"反向跃冲Z{keySuffix}_值"] = spikeZMatch.Groups[2].Value + "µm";
            }

            // 提取反向间隙Z（格式：12%反向间隙 Z1.2µm或17%反向间隙 Z1.2µm，支持特殊符号和无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var reversalZMatch = Regex.Match(text, @"(\d+)%\s*反向间隙\s*Z\s*([-\d.]+)\s*µm");
            if (!reversalZMatch.Success)
            {
                // 改进：确保负号被捕获
                reversalZMatch = Regex.Match(text, @"(\d+)%反向间隙\s*Z[^\d\-]*(-?\d+\.?\d*)\s*µm", RegexOptions.Singleline);
            }
            if (reversalZMatch.Success)
            {
                diagnosticData[$"反向间隙Z{keySuffix}_百分比"] = reversalZMatch.Groups[1].Value + "%";
                diagnosticData[$"反向间隙Z{keySuffix}_值"] = reversalZMatch.Groups[2].Value + "µm";
            }

            // 提取伺服不匹配（格式：9%伺服不匹配-0.01ms，支持无空格）
            // 注意：需要确保捕获负号，即使负号和数字之间有特殊字符
            var servoMatch = Regex.Match(text, @"(\d+)%\s*伺服不匹配\s*([-\d.]+)\s*ms");
            if (!servoMatch.Success)
            {
                // 改进：确保负号被捕获
                servoMatch = Regex.Match(text, @"(\d+)%伺服不匹配[^\d\-]*(-?\d+\.?\d*)\s*ms", RegexOptions.Singleline);
            }
            if (servoMatch.Success)
            {
                diagnosticData[$"伺服不匹配{keySuffix}_百分比"] = servoMatch.Groups[1].Value + "%";
                diagnosticData[$"伺服不匹配{keySuffix}_值"] = servoMatch.Groups[2].Value + "ms";
            }

            // 提取圆度（格式：圆度10.2µm，支持无空格）
            var roundnessMatch = Regex.Match(text, @"圆度\s*([\d.]+)\s*µm");
            if (!roundnessMatch.Success)
            {
                roundnessMatch = Regex.Match(text, @"圆度([\d.]+)µm");
            }
            if (roundnessMatch.Success)
            {
                diagnosticData[$"圆度{keySuffix}"] = roundnessMatch.Groups[1].Value + "µm";
            }

            // 提取运行和拟合信息（格式：运行 1 运行 2 拟合 1 拟合 2）
            var runMatch = Regex.Match(text, @"运行\s*(\d+)\s*运行\s*(\d+)\s*拟合\s*(\d+)\s*拟合\s*(\d+)");
            if (runMatch.Success)
            {
                diagnosticData["运行1"] = runMatch.Groups[1].Value;
                diagnosticData["运行2"] = runMatch.Groups[2].Value;
                diagnosticData["拟合1"] = runMatch.Groups[3].Value;
                diagnosticData["拟合2"] = runMatch.Groups[4].Value;
            }

            // 提取最大分散度(Ps max)（格式：最大分散度 (Ps max)2.0 或 最大分散度(Psmax) 10.2µm，支持多种格式）
            string? psmaxValue = null;
            // 格式1: 最大分散度 (Ps max)2.0（数字直接跟在括号后面，可能没有µm单位）
            var psmaxMatch = Regex.Match(text, @"最大分散度\s*\(Ps\s+max\)\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (psmaxMatch.Success)
            {
                psmaxValue = psmaxMatch.Groups[1].Value;
            }
            else
            {
                // 格式2: 最大分散度(Psmax) 10.2µm
                psmaxMatch = Regex.Match(text, @"最大分散度\s*\(Psmax\)\s*([\d.]+)\s*µm", RegexOptions.IgnoreCase);
                if (psmaxMatch.Success)
                {
                    psmaxValue = psmaxMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(psmaxValue))
            {
                // 格式3: 最大分散度(Psmax)10.2µm（无空格）
                psmaxMatch = Regex.Match(text, @"最大分散度\s*\(Psmax\)([\d.]+)µm", RegexOptions.IgnoreCase);
                if (psmaxMatch.Success)
                {
                    psmaxValue = psmaxMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(psmaxValue))
            {
                // 格式4: Psmax 10.2µm
                psmaxMatch = Regex.Match(text, @"Psmax\s*([\d.]+)\s*µm", RegexOptions.IgnoreCase);
                if (psmaxMatch.Success)
                {
                    psmaxValue = psmaxMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(psmaxValue))
            {
                // 格式5: Psmax10.2µm（无空格）
                psmaxMatch = Regex.Match(text, @"Psmax([\d.]+)µm", RegexOptions.IgnoreCase);
                if (psmaxMatch.Success)
                {
                    psmaxValue = psmaxMatch.Groups[1].Value + "µm";
                }
            }
            if (!string.IsNullOrEmpty(psmaxValue))
            {
                diagnosticData[$"最大分散度(Psmax){keySuffix}"] = psmaxValue;
                _logger.LogInformation("成功提取最大分散度(Psmax): {Value}", psmaxValue);
            }
            else
            {
                _logger.LogWarning("未能从PDF文本中提取到最大分散度(Psmax)");
            }

            // 提取位置不确定度(P)（格式：位置不确定度 (P)3.9 或 位置不确定度(P) 5.3µm，支持多种格式）
            string? positionUncertaintyValue = null;
            // 格式1: 位置不确定度 (P)3.9（数字直接跟在括号后面，可能没有µm单位）
            var positionUncertaintyMatch = Regex.Match(text, @"位置不确定度\s*\(P\)\s*([\d.]+)(?:\s*µm)?", RegexOptions.IgnoreCase);
            if (positionUncertaintyMatch.Success)
            {
                var value = positionUncertaintyMatch.Groups[1].Value;
                // 检查后面是否有µm，如果有则添加，否则不加
                var afterMatch = text.Substring(positionUncertaintyMatch.Index + positionUncertaintyMatch.Length);
                if (afterMatch.TrimStart().StartsWith("µm", StringComparison.OrdinalIgnoreCase))
                {
                    positionUncertaintyValue = value + "µm";
                }
                else
                {
                    positionUncertaintyValue = value;
                }
            }
            if (string.IsNullOrEmpty(positionUncertaintyValue))
            {
                // 格式2: 位置不确定度(P)5.3µm（无空格）
                positionUncertaintyMatch = Regex.Match(text, @"位置不确定度\s*\(P\)([\d.]+)µm", RegexOptions.IgnoreCase);
                if (positionUncertaintyMatch.Success)
                {
                    positionUncertaintyValue = positionUncertaintyMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(positionUncertaintyValue))
            {
                // 格式3: 位置不确定度(P) 5.3µm（有空格）
                positionUncertaintyMatch = Regex.Match(text, @"位置不确定度\s*\(P\)\s*([\d.]+)\s*µm", RegexOptions.IgnoreCase);
                if (positionUncertaintyMatch.Success)
                {
                    positionUncertaintyValue = positionUncertaintyMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(positionUncertaintyValue))
            {
                // 格式4: 位置不确定度后面跟数字和µm的模式（可能没有括号）
                positionUncertaintyMatch = Regex.Match(text, @"位置不确定度[^\d]*([\d.]+)\s*µm", RegexOptions.IgnoreCase);
                if (positionUncertaintyMatch.Success)
                {
                    positionUncertaintyValue = positionUncertaintyMatch.Groups[1].Value + "µm";
                }
            }
            if (string.IsNullOrEmpty(positionUncertaintyValue))
            {
                // 最后尝试：直接查找"位置不确定度"到数字之间的内容
                var positionIndex = text.IndexOf("位置不确定度", StringComparison.OrdinalIgnoreCase);
                if (positionIndex >= 0)
                {
                    var preview = text.Substring(positionIndex, Math.Min(100, text.Length - positionIndex));
                    // 先尝试找括号后的数字
                    var parenMatch = Regex.Match(preview, @"\(P\)\s*([\d.]+)", RegexOptions.IgnoreCase);
                    if (parenMatch.Success)
                    {
                        positionUncertaintyValue = parenMatch.Groups[1].Value;
                    }
                    else
                    {
                        // 再尝试找µm前的数字
                        var umIndex = preview.IndexOf("µm", StringComparison.OrdinalIgnoreCase);
                        if (umIndex > 0)
                        {
                            var beforeUm = preview.Substring(0, umIndex);
                            var numberMatch = Regex.Match(beforeUm, @"([\d.]+)");
                            if (numberMatch.Success)
                            {
                                positionUncertaintyValue = numberMatch.Groups[1].Value + "µm";
                            }
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(positionUncertaintyValue))
            {
                diagnosticData[$"位置不确定度(P){keySuffix}"] = positionUncertaintyValue;
                _logger.LogInformation("成功提取位置不确定度(P): {Value}", positionUncertaintyValue);
            }
            else
            {
                _logger.LogWarning("未能从PDF文本中提取到位置不确定度(P)");
            }

            // 提取误差补偿的轴标识（Z），用于添加到反向间隙误差的键名中
            string compensationAxis = string.Empty;
            var compensationMatch = Regex.Match(text, @"误差补偿\s*[-－]\s*([XYZ])");
            if (compensationMatch.Success)
            {
                compensationAxis = compensationMatch.Groups[1].Value;
            }

            // 提取反向间隙误差（格式：反向间隙误差: -1 μm）
            // 直接查找"反向间隙误差"到"μm"之间的数字
            string? errorValue = null;
            var errorTextIndex = text.IndexOf("反向间隙误差", StringComparison.OrdinalIgnoreCase);
            if (errorTextIndex >= 0)
            {
                var preview = text.Substring(errorTextIndex, Math.Min(100, text.Length - errorTextIndex));
                var umIndex = preview.IndexOf("μm", StringComparison.OrdinalIgnoreCase);
                if (umIndex > 0)
                {
                    var beforeUm = preview.Substring(0, umIndex);
                    var numberMatch = Regex.Match(beforeUm, @"([-\d.]+)");
                    if (numberMatch.Success)
                    {
                        errorValue = numberMatch.Groups[1].Value + " μm";
                    }
                }
            }
            
            // 如果直接查找失败，尝试正则表达式匹配
            if (string.IsNullOrEmpty(errorValue))
            {
                var reversalErrorMatch = Regex.Match(text, @"反向间隙误差[：:\s]+([-\d.]+)\s*μm", RegexOptions.IgnoreCase);
                if (reversalErrorMatch.Success)
                {
                    errorValue = reversalErrorMatch.Groups[1].Value + " μm";
                }
            }
            
            if (!string.IsNullOrEmpty(errorValue))
            {
                // 将误差补偿的轴标识添加到键名中：反向间隙误差Z
                var errorKey = string.IsNullOrEmpty(compensationAxis) 
                    ? $"反向间隙误差{keySuffix}" 
                    : $"反向间隙误差{compensationAxis}";
                diagnosticData[errorKey] = errorValue;
            }

            return diagnosticData;
        }

    }

    /// <summary>
    /// PDF文件数据模型
    /// </summary>
    public class PdfFileData
    {
        public string SourceFileName { get; set; } = string.Empty;
        public DateTime ImportTime { get; set; }
        public string FileType { get; set; } = string.Empty;
        public Dictionary<string, string> HeaderInfo { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> DiagnosticData { get; set; } = new Dictionary<string, string>();
    }
}

