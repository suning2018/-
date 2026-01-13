using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace FtpExcelProcessor.Services
{
    public class ExcelService
    {
        private readonly ILogger<ExcelService> _logger;
        private readonly DatabaseLogService? _databaseLogService;

        public ExcelService(ILogger<ExcelService> logger, DatabaseLogService? databaseLogService = null)
        {
            _logger = logger;
            _databaseLogService = databaseLogService;
            // 设置EPPlus的许可证上下文（非商业用途）
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        /// <summary>
        /// 读取Excel文件内容（三坐标测量报告格式）
        /// </summary>
        public async Task<ExcelFileData> ReadExcelFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            var fileData = new ExcelFileData
            {
                SourceFileName = Path.GetFileName(filePath),
                ImportTime = DateTime.Now
            };

            try
            {
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets[2]; // 读取第一个工作表

                    if (worksheet == null)
                    {
                        throw new Exception("Excel文件中没有找到工作表。");
                    }

                    var rowCount = worksheet.Dimension?.Rows ?? 0;
                    var colCount = worksheet.Dimension?.Columns ?? 0;

                    if (rowCount == 0 || colCount == 0)
                    {
                        return fileData;
                    }

                    // 读取表头信息
                    fileData.HeaderInfo = ExtractHeaderInfo(worksheet, rowCount, colCount);

                    // 查找数据表开始行
                    int dataStartRow = FindDataTableStartRow(worksheet, rowCount, colCount);
                    
                    // 读取列标题
                    var headers = new List<string>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        var headerValue = GetCellText(worksheet.Cells[dataStartRow, col]);
                        headers.Add(string.IsNullOrEmpty(headerValue) ? $"Column{col}" : headerValue);
                    }
                    
                    // 读取数据行
                    for (int row = dataStartRow + 1; row <= rowCount; row++)
                    {
                        // 跳过空行
                        bool isEmptyRow = true;
                        for (int col = 1; col <= colCount; col++)
                        {
                            if (!string.IsNullOrWhiteSpace(GetCellText(worksheet.Cells[row, col])))
                            {
                                isEmptyRow = false;
                                break;
                            }
                        }
                        if (isEmptyRow) continue;

                        // 跳过表头或表单信息行
                        if (IsHeaderOrFormRow(worksheet, row, colCount)) continue;

                        var rowData = new ExcelRowData
                        {
                            RowNumber = row,
                            Columns = new Dictionary<string, string>()
                        };

                        for (int col = 1; col <= colCount && col <= headers.Count; col++)
                        {
                            rowData.Columns[headers[col - 1]] = GetCellText(worksheet.Cells[row, col]);
                        }

                        fileData.Rows.Add(rowData);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取Excel文件失败: {FilePath}", filePath);
                if (_databaseLogService != null)
                {
                    await _databaseLogService.LogErrorAsync($"读取Excel文件失败: {fileData.SourceFileName}", ex, "Excel", "ReadExcelFile", fileName: fileData.SourceFileName, operation: "Read");
                }
                throw;
            }

            return fileData;
        }

        /// <summary>
        /// 提取表头信息（Part Number, Part Name, Serial Number等）
        /// </summary>
        private Dictionary<string, string> ExtractHeaderInfo(ExcelWorksheet worksheet, int rowCount, int colCount)
        {
            var headerInfo = new Dictionary<string, string>();

            // 在前20行中查找表头信息
            for (int row = 1; row <= Math.Min(20, rowCount); row++)
            {
                for (int col = 1; col <= colCount; col++)
                {
                    var cell = worksheet.Cells[row, col];
                    var cellValue = GetCellText(cell);
                    if (string.IsNullOrEmpty(cellValue))
                        continue;

                    // 查找常见的表头关键字
                    if (cellValue.Contains("Part Number", StringComparison.OrdinalIgnoreCase) ||
                        cellValue.Contains("零件号", StringComparison.OrdinalIgnoreCase) ||
                        cellValue.Contains("Part No", StringComparison.OrdinalIgnoreCase))
                    {
                        // 获取右侧或下方的值
                        var value = GetAdjacentValue(worksheet, row, col, colCount);
                        if (!string.IsNullOrEmpty(value))
                        {
                            headerInfo["PartNumber"] = value;
                        }
                    }
                    else if (cellValue.Contains("Part Name", StringComparison.OrdinalIgnoreCase) ||
                             cellValue.Contains("零件名称", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = GetAdjacentValue(worksheet, row, col, colCount);
                        if (!string.IsNullOrEmpty(value))
                        {
                            headerInfo["PartName"] = value;
                        }
                    }
                    else if (cellValue.Contains("Serial Number", StringComparison.OrdinalIgnoreCase) ||
                             cellValue.Contains("序列号", StringComparison.OrdinalIgnoreCase) ||
                             cellValue.Contains("Serial No", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = GetAdjacentValue(worksheet, row, col, colCount);
                        if (!string.IsNullOrEmpty(value))
                        {
                            headerInfo["SerialNumber"] = value;
                        }
                    }
                }
            }

            return headerInfo;
        }

        /// <summary>
        /// 获取单元格的文本值（处理各种格式：普通文本、公式、富文本、特殊字符等）
        /// </summary>
        private string GetCellText(OfficeOpenXml.ExcelRange cell)
        {
            if (cell == null)
                return string.Empty;

            try
            {
                // 1. 优先使用Text属性（显示文本，已格式化）
                var text = cell.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }

                // 2. 如果单元格包含富文本，获取富文本内容
                if (cell.RichText.Count > 0)
                {
                    var richText = string.Join("", cell.RichText.Select(rt => rt.Text));
                    if (!string.IsNullOrWhiteSpace(richText))
                    {
                        return richText.Trim();
                    }
                }

                // 3. 使用Value属性（原始值）
                var value = cell.Value;
                if (value != null)
                {
                    // 如果是公式，尝试获取计算后的值
                    if (cell.Formula != null && !string.IsNullOrEmpty(cell.Formula))
                    {
                        // 公式单元格，使用Text或Value
                        return cell.Text?.Trim() ?? value.ToString()?.Trim() ?? string.Empty;
                    }
                    
                    return value.ToString()?.Trim() ?? string.Empty;
                }

                // 4. 如果都为空，返回空字符串
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取相邻单元格的值（优先右侧，其次下方）
        /// </summary>
        private string GetAdjacentValue(ExcelWorksheet worksheet, int row, int col, int maxCol)
        {
            // 先检查右侧
            if (col < maxCol)
            {
                var rightCell = worksheet.Cells[row, col + 1];
                var rightValue = GetCellText(rightCell);
                if (!string.IsNullOrEmpty(rightValue))
                {
                    return rightValue;
                }
            }

            // 再检查下方
            var bottomCell = worksheet.Cells[row + 1, col];
            return GetCellText(bottomCell);
        }

        /// <summary>
        /// 查找数据表开始行（包含列标题的行）
        /// </summary>
        private int FindDataTableStartRow(ExcelWorksheet worksheet, int rowCount, int colCount)
        {
            var commonHeaders = new[] { 
                "Char No", "Reference Location", "Characteristic Designator", "Requirement", "Results",
                "特征编号", "参考位置", "特征标识", "要求", "结果"
            };

            // 查找包含列标题的行
            for (int row = 1; row <= Math.Min(50, rowCount); row++)
            {
                int matchCount = 0;
                for (int col = 1; col <= colCount; col++)
                {
                    var cellValue = GetCellText(worksheet.Cells[row, col]);
                    if (commonHeaders.Any(h => cellValue.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchCount++;
                    }
                }
                if (matchCount >= 2) return row;
            }

            // 如果没找到，查找以数字开头的行（特征编号）
            for (int row = 1; row <= Math.Min(100, rowCount); row++)
            {
                var firstCell = GetCellText(worksheet.Cells[row, 1]);
                if (int.TryParse(firstCell, out int charNo) && charNo >= 1 && charNo <= 1000)
                {
                    // 检查是否有足够的数据列
                    int dataColCount = 0;
                    for (int col = 1; col <= colCount; col++)
                    {
                        if (!string.IsNullOrEmpty(GetCellText(worksheet.Cells[row, col])))
                            dataColCount++;
                    }
                    if (dataColCount >= 3) return row;
                }
            }

            return 1; // 默认返回第1行
        }

        /// <summary>
        /// 判断是否为表头或表单信息行（应跳过）
        /// </summary>
        private bool IsHeaderOrFormRow(ExcelWorksheet worksheet, int row, int colCount)
        {
            var skipKeywords = new[] {
                "Form", "表单", "First Article Inspection", "Part Number", "Part Name", "Serial Number",
                "FAI Report Number", "Drawing Number", "Signature", "Reviewed By", "Customer Approval"
            };

            var firstCell = GetCellText(worksheet.Cells[row, 1]);
            // 如果第一列是数字，可能是数据行，不跳过
            if (int.TryParse(firstCell, out _)) return false;

            // 检查是否包含跳过关键词
            for (int col = 1; col <= colCount; col++)
            {
                var cellValue = GetCellText(worksheet.Cells[row, col]);
                if (skipKeywords.Any(keyword => cellValue.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Excel文件数据模型（包含表头信息和数据行）
    /// </summary>
    public class ExcelFileData
    {
        public string SourceFileName { get; set; } = string.Empty;
        public DateTime ImportTime { get; set; }
        public Dictionary<string, string> HeaderInfo { get; set; } = new Dictionary<string, string>();
        public List<ExcelRowData> Rows { get; set; } = new List<ExcelRowData>();
    }

    /// <summary>
    /// Excel行数据模型
    /// </summary>
    public class ExcelRowData
    {
        public int RowNumber { get; set; }
        public Dictionary<string, string> Columns { get; set; } = new Dictionary<string, string>();
    }
}

