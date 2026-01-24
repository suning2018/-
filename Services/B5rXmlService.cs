using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using FtpExcelProcessor.Models;

namespace FtpExcelProcessor.Services
{
    /// <summary>
    /// B5R XML文件解析服务
    /// </summary>
    public class B5rXmlService
    {
        private readonly ILogger<B5rXmlService> _logger;

        public B5rXmlService(ILogger<B5rXmlService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 解析B5R XML文件
        /// </summary>
        public B5rFileData ParseB5rFile(string filePath)
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
                var doc = XDocument.Load(filePath);
                var root = doc.Root;

                if (root == null)
                {
                    _logger.LogWarning("B5R文件根元素为空: {FilePath}", filePath);
                    return fileData;
                }

                // 1. 提取轴标识
                string axisIdentifier = ExtractAxisIdentifier(root);
                _logger.LogInformation("提取到轴标识: {Axis}", axisIdentifier);

                // 2. 提取表头信息
                fileData.HeaderInfo = ExtractHeaderInfo(root, fileData.SourceFileName);
                _logger.LogInformation("提取表头信息完成: {HeaderCount}项", fileData.HeaderInfo.Count);

                // 3. 提取所有FEATURE数据
                fileData.AllFeatures = ExtractAllFeatures(root, axisIdentifier);

                // 4. 分类: 必须采集、应取数据、垃圾数据
                fileData.MustHaveFeatures = fileData.AllFeatures
                    .Where(f => f.DataLevel == B5rDataLevel.MustHave)
                    .ToList();

                fileData.ShouldHaveFeatures = fileData.AllFeatures
                    .Where(f => f.DataLevel == B5rDataLevel.ShouldHave)
                    .ToList();

                fileData.GarbageFeatures = fileData.AllFeatures
                    .Where(f => f.DataLevel == B5rDataLevel.Garbage)
                    .ToList();

                // 5. 保存文件大小
                var fileInfo = new FileInfo(filePath);
                fileData.FileSize = fileInfo.Length;

                _logger.LogInformation(
                    "B5R文件解析完成: {FileName}, 轴标识: {Axis}, 特征总数: {Total}, MustHave: {MustHaveCount}, ShouldHave: {ShouldHaveCount}, Garbage: {GarbageCount}, 文件大小: {FileSize}字节",
                    fileData.SourceFileName, axisIdentifier,
                    fileData.AllFeatures.Count, fileData.MustHaveFeatures.Count, fileData.ShouldHaveFeatures.Count, fileData.GarbageFeatures.Count, fileData.FileSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析B5R文件失败: {FilePath}", filePath);
                throw;
            }

            return fileData;
        }

        /// <summary>
        /// 提取轴标识(XY/YZ/ZX)
        /// </summary>
        private string ExtractAxisIdentifier(XElement root)
        {
            var plane = root.Descendants("PLANE").FirstOrDefault();
            if (plane != null)
            {
                var testPlane = plane.Attribute("TEST_PLANE")?.Value;
                if (!string.IsNullOrEmpty(testPlane))
                {
                    // 确保大写: XY, YZ, ZX
                    return testPlane.ToUpper();
                }
            }

            _logger.LogWarning("未找到PLANE元素，使用默认轴标识: XY");
            return "XY"; // 默认值
        }

        /// <summary>
        /// 提取表头信息
        /// </summary>
        private Dictionary<string, string> ExtractHeaderInfo(XElement root, string sourceFileName)
        {
            var headerInfo = new Dictionary<string, string>();

            // 测试名称
            var testSpec = root.Descendants("TEST_SPEC").FirstOrDefault();
            if (testSpec != null)
            {
                var testName = testSpec.Attribute("NAME")?.Value;
                if (!string.IsNullOrEmpty(testName))
                {
                    headerInfo["TestId"] = testName;
                }
            }

            // 操作员
            var operatorNode = root.Descendants("OPERATOR").FirstOrDefault();
            if (operatorNode != null && !string.IsNullOrEmpty(operatorNode.Value))
            {
                headerInfo["Operator"] = operatorNode.Value.Trim();
            }

            // 测试日期
            var dateStarted = root.Descendants("DATE_STARTED").FirstOrDefault();
            if (dateStarted != null && !string.IsNullOrEmpty(dateStarted.Value))
            {
                // 添加"快速检测日期: "前缀，与PDF格式保持一致
                headerInfo["TestDate"] = "快速检测日期: " + dateStarted.Value.Trim();
            }

            // 机器名称
            var machineTested = root.Descendants("MACHINE_TESTED").FirstOrDefault();
            if (machineTested != null)
            {
                var machineName = machineTested.Attribute("NAME")?.Value;
                if (!string.IsNullOrEmpty(machineName))
                {
                    // 添加"机器:"后缀，与PDF格式保持一致
                    headerInfo["Machine"] = machineName.Trim() + "机器:";
                }
            }

            // QC20-W序列号
            var serialNumberNode = root.Descendants("SERIAL_NUMBER").FirstOrDefault();
            if (serialNumberNode != null && !string.IsNullOrEmpty(serialNumberNode.Value))
            {
                headerInfo["QC20W"] = serialNumberNode.Value.Trim();
            }

            // 上次校准日期
            var calibratedNode = root.Descendants("LAST_CALIBRATED").FirstOrDefault();
            if (calibratedNode != null && !string.IsNullOrEmpty(calibratedNode.Value))
            {
                headerInfo["LastCalibration"] = calibratedNode.Value.Trim();
            }

            // 提取序列号并存入PartName
            var partNumberNode = root.Descendants("PART_NUMBER").FirstOrDefault();
            if (partNumberNode != null && !string.IsNullOrEmpty(partNumberNode.Value))
            {
                headerInfo["PartName"] = partNumberNode.Value.Trim();
            }
            else
            {
                // 如果XML中没有，从文件名中提取序列号
                // 文件名格式：XY 360度 150mm 20251218-194332_172001002.b5r
                var fileNameMatch = Regex.Match(sourceFileName, @"_(\d+)\.b5r", RegexOptions.IgnoreCase);
                if (fileNameMatch.Success)
                {
                    headerInfo["PartName"] = fileNameMatch.Groups[1].Value;
                    _logger.LogInformation("从文件名中提取序列号并存入PartName: {PartName}", headerInfo["PartName"]);
                }
            }

            return headerInfo;
        }

        /// <summary>
        /// 提取所有FEATURE数据
        /// </summary>
        private List<B5rFeatureData> ExtractAllFeatures(XElement root, string axisIdentifier)
        {
            var features = new List<B5rFeatureData>();
            var featureElements = root.Descendants("FEATURE");

            foreach (var element in featureElements)
            {
                var name = element.Attribute("NAME")?.Value ?? string.Empty;
                var dataType = element.Attribute("DT")?.Value ?? string.Empty;
                var value = element.Value?.Trim() ?? string.Empty;

                // 使用NAME+DT组合判断数据级别
                var dataLevel = B5rAxisMapping.GetDataLevel(name, dataType);

                var featureData = new B5rFeatureData
                {
                    Name = name,
                    DataType = dataType,
                    Value = value,
                    AxisIdentifier = axisIdentifier,
                    DataLevel = dataLevel
                };

                features.Add(featureData);

                // 记录不同级别的特征
                if (dataLevel == B5rDataLevel.MustHave)
                {
                    var columnName = B5rAxisMapping.GetColumnName(name, axisIdentifier);
                    _logger.LogDebug("MustHave特征: {Name} (DT:{DataType}) -> {ColumnName} = {Value}",
                        name, dataType, columnName, value);
                }
                else if (dataLevel == B5rDataLevel.ShouldHave)
                {
                    var columnName = B5rAxisMapping.GetColumnName(name, axisIdentifier);
                    _logger.LogDebug("ShouldHave特征: {Name} (DT:{DataType}) -> {ColumnName} = {Value}",
                        name, dataType, columnName, value);
                }
            }

            return features;
        }
    }
}
