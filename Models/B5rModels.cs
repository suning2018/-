using System.Collections.Generic;
using System.Linq;

namespace FtpExcelProcessor.Models
{
    /// <summary>
    /// B5R数据级别枚举
    /// </summary>
    public enum B5rDataLevel
    {
        /// <summary>
        /// 必要数据（当前需要，带*注释）
        /// </summary>
        MustHave = 1,

        /// <summary>
        /// 应取数据（以后可能要，有注释但无*）
        /// </summary>
        ShouldHave = 2,

        /// <summary>
        /// 垃圾数据（不需要，无注释）
        /// </summary>
        Garbage = 3
    }

    /// <summary>
    /// B5R轴标识映射配置
    /// </summary>
    public static class B5rAxisMapping
    {
        /// <summary>
        /// 必要数据（当前需要，保存到FileData表）
        /// 使用Dictionary<Name, DataType>配置，提高数据识别精确度
        /// </summary>
        public static readonly Dictionary<string, string> MustHaveFields = new Dictionary<string, string>
        {
            { "AF_SCALE_MISMATCH", "UT_LENGTH_UM" },   // 比例不匹配*
            { "AF_STRAIGHTNESS_A", "UT_LENGTH_UM" },   // X轴直线度*
            { "AF_STRAIGHTNESS_B", "UT_LENGTH_UM" },   // Y轴直线度*
            { "AF_SQUARENESS", "UT_SQUARENESS" },       // 垂直度*
            { "AF_SERVO_MISMATCH", "UT_SERVO_MISMATCH" }, // 伺服不匹配*
            { "AF_CIRCULARITY", "UT_LENGTH_UM" }        // 圆度*
        };

        /// <summary>
        /// DT类型到单位的映射
        /// </summary>
        public static readonly Dictionary<string, string> DataTypeToUnit = new Dictionary<string, string>
        {
            { "UT_LENGTH_MM", "mm" },         // 长度单位毫米
            { "UT_LENGTH_UM", "µm" },         // 长度单位微米
            { "UT_FEEDRATE", "mm/min" },      // 进给率
            { "UT_SQUARENESS", "µm/m" },      // 垂直度
            { "UT_SERVO_MISMATCH", "ms" },    // 伺服不匹配
            { "UT_ANGLE", "°" },              // 角度
            { "UT_RANKING", "" },             // 排名（无单位）
            { "UT_PERCENT", "%" },            // 百分比
            { "UT_SCALE_ERROR", "ppm" },      // 比例误差
            { "UT_LENGTH_M", "m" },           // 长度单位米
            { "UT_SPEED", "mm/s" }           // 速度
        };

        /// <summary>
        /// 根据DT类型获取单位
        /// </summary>
        public static string GetUnit(string dataType)
        {
            if (string.IsNullOrEmpty(dataType))
            {
                return string.Empty;
            }

            if (DataTypeToUnit.TryGetValue(dataType, out string? unit))
            {
                return unit ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>
        /// 应取数据（以后可能要，保存到FileData表）
        /// 使用Dictionary<Name, DataType>配置，提高数据识别精确度
        /// </summary>
        public static readonly Dictionary<string, string> ShouldHaveFields = new Dictionary<string, string>
        {
            { "AF_CYCLIC_ERROR_A_FWD", "UT_LENGTH_UM" }, // X轴周期误差
            { "AF_CYCLIC_ERROR_B_FWD", "UT_LENGTH_UM" }, // Y轴周期误差
            { "AF_BACKLASH_A_POS", "UT_LENGTH_UM" },     // X轴反向间隙
            { "AF_BACKLASH_A_NEG", "UT_LENGTH_UM" },     // X轴反向间隙
            { "AF_BACKLASH_B_POS", "UT_LENGTH_UM" },     // Y轴反向间隙
            { "AF_BACKLASH_B_NEG", "UT_LENGTH_UM" },     // Y轴反向间隙
            { "AF_LATERAL_PLAY_A_POS", "UT_LENGTH_UM" }, // X轴横向间隙
            { "AF_LATERAL_PLAY_A_NEG", "UT_LENGTH_UM" }, // X轴横向间隙
            { "AF_LATERAL_PLAY_B_POS", "UT_LENGTH_UM" }, // Y轴横向间隙
            { "AF_LATERAL_PLAY_B_NEG", "UT_LENGTH_UM" }, // Y轴横向间隙
            { "AF_REVERSAL_SPIKES_A_POS", "UT_LENGTH_UM" }, // X反向跃冲
            { "AF_REVERSAL_SPIKES_B_POS", "UT_LENGTH_UM" }, // Y反向跃冲
            { "AF_REVERSAL_SPIKES_A_NEG", "UT_LENGTH_UM" }, // X反向跃冲
            { "AF_REVERSAL_SPIKES_B_NEG", "UT_LENGTH_UM" }  // Y反向跃冲
        };

        /// <summary>
        /// 获取带轴标识的ColumnName（用于FileData表）
        /// </summary>
        /// <param name="fieldName">字段名称</param>
        /// <param name="axisIdentifier">轴标识 (XY/YZ/ZX)</param>
        /// <returns>带轴标识的ColumnName</returns>
        public static string GetColumnName(string fieldName, string axisIdentifier)
        {
            if (string.IsNullOrEmpty(axisIdentifier))
            {
                return fieldName;
            }
            return $"{fieldName}_{axisIdentifier}";
        }

        /// <summary>
        /// 判断数据级别（使用NAME+DT组合匹配）
        /// </summary>
        /// <param name="name">字段名称</param>
        /// <param name="dataType">数据类型</param>
        /// <returns>数据级别</returns>
        public static B5rDataLevel GetDataLevel(string name, string dataType)
        {
            // 优先检查MustHave
            foreach (var field in MustHaveFields)
            {
                if (string.Equals(name, field.Key, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(dataType, field.Value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return B5rDataLevel.MustHave;
                }
            }

            // 检查ShouldHave
            foreach (var field in ShouldHaveFields)
            {
                if (string.Equals(name, field.Key, System.StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(dataType, field.Value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return B5rDataLevel.ShouldHave;
                }
            }

            // 其他都是垃圾数据
            return B5rDataLevel.Garbage;
        }

        /// <summary>
        /// 判断是否为必须采集的字段（向后兼容）
        /// </summary>
        /// <param name="fieldName">字段名称</param>
        /// <returns>是否必须采集</returns>
        [System.Obsolete("请使用GetDataLevel方法判断数据级别")]
        public static bool IsMustHaveField(string fieldName)
        {
            foreach (var mustHaveField in MustHaveFields)
            {
                if (mustHaveField.Key.Equals(fieldName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// B5R XML FEATURE数据模型
    /// </summary>
    public class B5rFeatureData
    {
        /// <summary>
        /// FEATURE名称 (如: AF_SCALE_MISMATCH)
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 数据类型 (如: UT_LENGTH_UM)
        /// </summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// 元素值
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// 轴标识 (XY/YZ/ZX)
        /// </summary>
        public string AxisIdentifier { get; set; } = string.Empty;

        /// <summary>
        /// 数据级别（MustHave/ShouldHave/Garbage）
        /// </summary>
        public B5rDataLevel DataLevel { get; set; }

        /// <summary>
        /// 是否必须采集（向后兼容）
        /// </summary>
        public bool IsMustHave => DataLevel == B5rDataLevel.MustHave;

        /// <summary>
        /// 是否应取数据
        /// </summary>
        public bool IsShouldHave => DataLevel == B5rDataLevel.ShouldHave;

        /// <summary>
        /// 是否为垃圾数据
        /// </summary>
        public bool IsGarbage => DataLevel == B5rDataLevel.Garbage;
    }

    /// <summary>
    /// B5R文件完整数据模型
    /// </summary>
    public class B5rFileData
    {
        /// <summary>
        /// 源文件名
        /// </summary>
        public string SourceFileName { get; set; } = string.Empty;

        /// <summary>
        /// 导入时间
        /// </summary>
        public DateTime ImportTime { get; set; }

        /// <summary>
        /// 文件类型
        /// </summary>
        public string FileType { get; set; } = "B5R";

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// 表头信息（Operator, TestDate, Machine等）
        /// </summary>
        public Dictionary<string, string> HeaderInfo { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 所有FEATURE数据（包括MustHave、ShouldHave、Garbage）
        /// </summary>
        public List<B5rFeatureData> AllFeatures { get; set; } = new List<B5rFeatureData>();

        /// <summary>
        /// 必要数据（当前需要）
        /// </summary>
        public List<B5rFeatureData> MustHaveFeatures { get; set; } = new List<B5rFeatureData>();

        /// <summary>
        /// 应取数据（以后可能要）
        /// </summary>
        public List<B5rFeatureData> ShouldHaveFeatures { get; set; } = new List<B5rFeatureData>();

        /// <summary>
        /// 垃圾数据（不需要）
        /// </summary>
        public List<B5rFeatureData> GarbageFeatures { get; set; } = new List<B5rFeatureData>();

        /// <summary>
        /// 有效数据（MustHave + ShouldHave）
        /// </summary>
        public List<B5rFeatureData> ValidFeatures => MustHaveFeatures.Concat(ShouldHaveFeatures).ToList();
    }
}
