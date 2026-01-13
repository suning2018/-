-- =============================================
-- FTP文件处理系统 - 数据库初始化脚本
-- 说明：此脚本会创建所有必需的表和索引
-- 适用场景：新环境首次执行
-- 执行顺序：按顺序执行，脚本会自动检查表是否存在（如果表已存在则跳过）
-- =============================================

PRINT '========================================';
PRINT '开始初始化数据库...';
PRINT '========================================';

-- =============================================
-- 第一部分：基础表（FileInfo, FileData, SystemLog）
-- =============================================
PRINT '';
PRINT '--- 第一部分：创建基础表 ---';

-- =============================================
-- 1. FileInfo表 - 文件信息表
-- =============================================
-- 用途：存储从FTP服务器下载并处理的文件（Excel和PDF）的元数据信息
-- 说明：每个文件对应一条记录，包含文件的基本信息和处理状态
-- 关联：与FileData表通过SourceFileName字段关联（一对多关系）
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FileInfo]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[FileInfo] (
        -- 主键：自增ID，唯一标识每条文件记录
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        
        -- 源文件名：从FTP服务器下载的原始文件名（包含扩展名）
        -- 示例：'CMM_Report_20240101.xlsx' 或 'Ballbar_Diagnostic_20240101.pdf'
        [SourceFileName] NVARCHAR(500) NOT NULL,
        
        -- 文件类型：标识文件格式，取值为 'Excel' 或 'PDF'
        -- Excel：三坐标测量报告（.xlsx, .xls）
        -- PDF：Renishaw球杆仪诊断报告（.pdf）
        [FileType] NVARCHAR(50) NOT NULL,
        
        -- 零件号：从文件表头提取的零件编号
        -- Excel：从表头"Part Number"字段提取
        -- PDF：从报告标题或元数据中提取
        [PartNumber] NVARCHAR(200),
        
        -- 零件名称：从文件表头提取的零件名称
        -- Excel：从表头"Part Name"字段提取
        [PartName] NVARCHAR(500),
        
        -- 序列号：从文件表头提取的序列号
        -- Excel：从表头"Serial Number"字段提取
        -- PDF：从报告中的序列号信息提取
        [SerialNumber] NVARCHAR(200),
        
        -- 测试ID：测试标识符，用于关联测试记录
        [TestId] NVARCHAR(200),
        
        -- 操作员：执行测试的操作员姓名
        -- Excel：从表头"Operator"字段提取
        [Operator] NVARCHAR(200),
        
        -- 测试日期：测试执行的日期（字符串格式，保持原始格式）
        -- Excel：从表头"Test Date"字段提取
        -- PDF：从报告日期信息提取
        [TestDate] NVARCHAR(100),
        
        -- 机器：使用的测量机器名称
        -- Excel：从表头"Machine"字段提取
        [Machine] NVARCHAR(500),
        
        -- QC20W：球杆仪校准信息（仅PDF文件）
        -- PDF：从Renishaw报告中提取的QC20W校准信息
        [QC20W] NVARCHAR(200),
        
        -- 最后校准日期：机器最后校准的日期
        -- Excel：从表头"Last Calibration"字段提取
        [LastCalibration] NVARCHAR(50),
        
        -- 导入时间：文件被导入到数据库的时间（自动记录）
        [ImportTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 处理状态：标识文件是否已被处理（用于数据映射）
        -- 0：未处理，1：已处理
        -- 当文件数据被映射到业务表后，此字段更新为1
        [Processed] BIT NOT NULL DEFAULT 0,
        
        -- 处理时间：文件被处理完成的时间（当Processed=1时记录）
        [ProcessedTime] DATETIME NULL
        
        -- 注意：SourceFileName 不设置唯一约束，允许同名文件多次导入（不同时间）
    );

    -- 索引说明：
    -- idx_FileInfo_FileType：按文件类型查询（用于区分Excel和PDF文件）
    -- idx_FileInfo_PartNumber：按零件号查询（用于快速查找特定零件的文件）
    -- idx_FileInfo_SerialNumber：按序列号查询（用于快速查找特定序列号的文件）
    -- idx_FileInfo_TestId：按测试ID查询（用于关联测试记录）
    -- idx_FileInfo_ImportTime：按导入时间查询（用于时间范围查询和排序）
    -- idx_FileInfo_Processed：按处理状态查询（用于查找未处理的文件，执行数据映射）
    CREATE INDEX [idx_FileInfo_FileType] ON [dbo].[FileInfo]([FileType]);
    CREATE INDEX [idx_FileInfo_PartNumber] ON [dbo].[FileInfo]([PartNumber]);
    CREATE INDEX [idx_FileInfo_SerialNumber] ON [dbo].[FileInfo]([SerialNumber]);
    CREATE INDEX [idx_FileInfo_TestId] ON [dbo].[FileInfo]([TestId]);
    CREATE INDEX [idx_FileInfo_ImportTime] ON [dbo].[FileInfo]([ImportTime]);
    CREATE INDEX [idx_FileInfo_Processed] ON [dbo].[FileInfo]([Processed]);
    
    PRINT '✓ FileInfo表创建成功';
END
ELSE
BEGIN
    PRINT '○ FileInfo表已存在，跳过创建';
END

-- =============================================
-- 2. FileData表 - 文件数据表
-- =============================================
-- 用途：存储从Excel和PDF文件中提取的所有数据，采用键值对形式存储
-- 说明：将文件中的每一行、每一列的数据都存储为一条记录，便于后续数据映射
-- 关联：与FileInfo表通过FileInfoId字段关联（多对一关系，外键）
--       与DataMappingConfig表配合使用，将数据映射到业务表
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FileData]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[FileData] (
        -- 主键：自增ID，唯一标识每条数据记录
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        
        -- 文件信息ID：关联到FileInfo表的Id字段（外键）
        -- 用于标识这条数据来自哪个文件记录
        [FileInfoId] INT NOT NULL,
        
        -- 源文件名：保留字段，用于兼容和查询（冗余字段，便于查询）
        -- 用于标识这条数据来自哪个文件
        [SourceFileName] NVARCHAR(500) NOT NULL,
        
        -- 文件类型：标识数据来源的文件格式
        -- 'Excel'：来自Excel文件的数据
        -- 'PDF'：来自PDF文件的数据
        [FileType] NVARCHAR(50) NOT NULL,
        
        -- 行号：数据在源文件中的行号（从1开始）
        -- Excel：对应Excel工作表中的行号
        -- PDF：对应PDF中提取的数据行号
        [RowNumber] INT NOT NULL,
        
        -- 列名：数据在源文件中的列名或字段名
        -- Excel：对应Excel工作表中的列名（如'sn', 'InspectionResultPD', 'InspectionResultQC'等）
        -- PDF：对应PDF中提取的字段名（如'TestDate', 'Machine', 'QC20W'等）
        [ColumnName] NVARCHAR(200) NOT NULL,
        
        -- 列值：对应列名的数据值（字符串格式）
        -- 所有数据都以字符串形式存储，包括数字、日期等
        -- 示例：'SN001', 'PASS', '2024-01-01', '123.45'等
        [ColumnValue] NVARCHAR(MAX),
        
        -- 导入时间：数据被导入到数据库的时间（自动记录）
        [ImportTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 外键约束：关联到FileInfo表
        CONSTRAINT [FK_FileData_FileInfo] FOREIGN KEY ([FileInfoId]) REFERENCES [dbo].[FileInfo]([Id]) ON DELETE CASCADE
        
        -- 注意：不设置唯一约束，允许同名文件多次导入（每次导入都插入新记录）
    );

    -- 索引说明：
    -- idx_FileData_FileInfoId：按文件信息ID查询（用于快速查找特定文件的所有数据，主关联索引）
    -- idx_FileData_SourceFileName：按文件名查询（用于快速查找特定文件的所有数据，兼容查询）
    -- idx_FileData_FileType：按文件类型查询（用于区分Excel和PDF数据）
    -- idx_FileData_ImportTime：按导入时间查询（用于时间范围查询和排序）
    CREATE INDEX [idx_FileData_FileInfoId] ON [dbo].[FileData]([FileInfoId]);
    CREATE INDEX [idx_FileData_SourceFileName] ON [dbo].[FileData]([SourceFileName]);
    CREATE INDEX [idx_FileData_FileType] ON [dbo].[FileData]([FileType]);
    CREATE INDEX [idx_FileData_ImportTime] ON [dbo].[FileData]([ImportTime]);
    
    PRINT '✓ FileData表创建成功';
END
ELSE
BEGIN
    PRINT '○ FileData表已存在，跳过创建';
END

-- =============================================
-- 3. SystemLog表 - 系统日志表
-- =============================================
-- 用途：记录系统运行过程中的所有日志信息，包括信息、警告、错误等
-- 说明：用于系统监控、问题排查和审计追踪
-- 关联：独立表，不与其他表直接关联
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SystemLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SystemLog] (
        -- 主键：自增ID，唯一标识每条日志记录
        [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,
        
        -- 日志时间：日志记录的时间（自动记录）
        [LogTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 日志级别：日志的严重程度
        -- 取值：'Information', 'Warning', 'Error', 'Fatal'
        -- Information：一般信息，如文件下载成功、数据处理完成等
        -- Warning：警告信息，如文件格式不支持、数据格式异常等
        -- Error：错误信息，如数据库连接失败、文件读取错误等
        -- Fatal：致命错误，如程序崩溃等
        [LogLevel] NVARCHAR(50) NOT NULL,
        
        -- 分类：日志的分类，用于区分不同的功能模块
        -- 示例：'FTP', 'FileProcessing', 'Database', 'DataProcessing', 'SqlExecution'等
        [Category] NVARCHAR(200),
        
        -- 消息：日志的主要内容描述
        -- 示例：'成功下载文件：report.xlsx', '数据库连接失败：连接超时'等
        [Message] NVARCHAR(MAX) NOT NULL,
        
        -- 异常信息：如果日志记录的是异常，这里存储异常的详细信息
        -- 包括异常类型、堆栈跟踪等
        [Exception] NVARCHAR(MAX),
        
        -- 来源：日志来源的类或方法名
        -- 示例：'FtpService', 'ExcelService', 'DatabaseService'等
        [Source] NVARCHAR(200),
        
        -- 文件名：如果日志与特定文件相关，记录文件名
        -- 示例：'CMM_Report_20240101.xlsx'
        [FileName] NVARCHAR(500),
        
        -- 操作：执行的操作名称
        -- 示例：'DownloadFiles', 'ReadExcelFile', 'SaveExcelData'等
        [Operation] NVARCHAR(200),
        
        -- 用户ID：执行操作的用户标识（如果适用）
        [UserId] NVARCHAR(100),
        
        -- 附加数据：其他需要记录的额外信息（JSON格式）
        -- 可以存储结构化的额外数据，如参数、配置等
        [AdditionalData] NVARCHAR(MAX)
    );

    -- 索引说明：
    -- idx_LogTime：按日志时间查询（用于时间范围查询和排序，最常用）
    -- idx_LogLevel：按日志级别查询（用于筛选错误、警告等）
    -- idx_Category：按分类查询（用于查看特定模块的日志）
    -- idx_Source：按来源查询（用于查看特定服务或类的日志）
    CREATE INDEX [idx_LogTime] ON [dbo].[SystemLog]([LogTime]);
    CREATE INDEX [idx_LogLevel] ON [dbo].[SystemLog]([LogLevel]);
    CREATE INDEX [idx_Category] ON [dbo].[SystemLog]([Category]);
    CREATE INDEX [idx_Source] ON [dbo].[SystemLog]([Source]);
    
    PRINT '✓ SystemLog表创建成功';
END
ELSE
BEGIN
    PRINT '○ SystemLog表已存在';
END

-- =============================================
-- 第二部分：配置表（DataMappingConfig, SqlExecutionConfig, SqlExecutionLog）
-- =============================================
PRINT '';
PRINT '--- 第二部分：创建配置表 ---';

-- =============================================
-- 1. DataMappingConfig表 - 数据映射配置表
-- =============================================
-- 用途：配置如何将FileData表中的数据映射到实际业务表
-- 说明：定义数据映射规则，系统根据这些规则自动生成UPDATE或INSERT SQL语句
-- 关联：与FileData表配合使用，将数据映射到业务表（如QC_ProcessProgrammeRecordline等）
-- 工作流程：
--   1. 系统读取FileData表中的数据
--   2. 根据DataMappingConfig中的配置规则
--   3. 生成SQL语句并存储到SqlExecutionConfig表
--   4. 后续由SQL执行服务执行这些SQL语句
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[DataMappingConfig]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[DataMappingConfig] (
        -- 主键：自增ID，唯一标识每条映射配置
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        
        -- 配置名称：映射配置的唯一名称，用于标识和引用
        -- 示例：'UpdateInspectionResultPD', 'UpdateInspectionResultQC'
        [ConfigName] NVARCHAR(200) NOT NULL,
        
        -- 源表：数据来源的表名，通常为'FileData'
        -- 表示从哪个表读取数据
        [SourceTable] NVARCHAR(200) NOT NULL,
        
        -- 源文件类型：限制只处理特定类型的文件
        -- 'Excel'：只处理Excel文件的数据
        -- 'PDF'：只处理PDF文件的数据
        -- NULL：处理所有类型的文件
        [SourceFileType] NVARCHAR(50),
        
        -- 源匹配字段：用于匹配的源字段名（FileData表的ColumnName）
        -- 示例：'sn'（序列号字段）
        -- 说明：系统会使用这个字段的值去目标表中查找匹配的记录
        [SourceMatchField] NVARCHAR(200) NOT NULL,
        
        -- 源数据字段：要映射的数据字段名（FileData表的ColumnName）
        -- 示例：'InspectionResultPD', 'InspectionResultQC'
        -- 说明：这个字段的值将被更新到目标表
        [SourceDataField] NVARCHAR(200) NOT NULL,
        
        -- 目标表：数据要更新到的业务表名
        -- 示例：'QC_ProcessProgrammeRecordline'
        -- 说明：这是实际业务表，必须已存在于数据库中
        [TargetTable] NVARCHAR(200) NOT NULL,
        
        -- 目标匹配字段：目标表中用于匹配的字段名
        -- 示例：'sn'（序列号字段）
        -- 说明：使用源匹配字段的值在这个字段中查找匹配的记录
        [TargetMatchField] NVARCHAR(200) NOT NULL,
        
        -- 目标更新字段：目标表中要更新的字段名
        -- 示例：'InspectionResultPD', 'InspectionResultQC'
        -- 说明：源数据字段的值将更新到这个字段
        [TargetUpdateField] NVARCHAR(200) NOT NULL,
        
        -- 匹配条件：额外的匹配条件（SQL WHERE子句的一部分）
        -- 示例：'Status = ''Active'' AND IsDeleted = 0'
        -- 说明：在匹配时除了主匹配字段外，还可以添加其他条件
        [MatchCondition] NVARCHAR(500),
        
        -- 是否激活：标识此配置是否启用
        -- 1：启用，系统会使用此配置生成SQL
        -- 0：禁用，系统会忽略此配置
        [IsActive] BIT NOT NULL DEFAULT 1,
        
        -- 描述：配置的详细说明
        -- 示例：'根据sn匹配，将Excel的InspectionResultPD值更新到QC_ProcessProgrammeRecordline'
        [Description] NVARCHAR(500),
        
        -- 自定义SQL模板：支持复杂JOIN查询的SQL模板（可选）
        -- 如果使用自定义SQL模板，UseCustomSqlTemplate应设置为1
        -- 支持占位符：{FileInfoId}, {PartName}, {ColumnName}, {RowNumber}, {ColumnValue}等
        [CustomSqlTemplate] NVARCHAR(MAX),
        
        -- 是否使用自定义SQL模板：1=使用自定义SQL模板，0=使用标准映射
        [UseCustomSqlTemplate] BIT NOT NULL DEFAULT 0,
        
        -- 模板参数：模板参数说明（JSON格式）
        -- 示例：'{"ColumnName":"9. Results","RowNumber":"8"}'
        -- 说明：当SQL模板包含{ColumnName}或{RowNumber}时，需要在此指定匹配条件
        [TemplateParameters] NVARCHAR(MAX),
        
        -- 创建时间：配置创建的时间（自动记录）
        [CreateTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 更新时间：配置最后修改的时间（自动更新）
        [UpdateTime] DATETIME NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX [idx_DataMappingConfig_IsActive] ON [dbo].[DataMappingConfig]([IsActive]);
    CREATE INDEX [idx_DataMappingConfig_SourceTable] ON [dbo].[DataMappingConfig]([SourceTable]);
    CREATE INDEX [idx_DataMappingConfig_TargetTable] ON [dbo].[DataMappingConfig]([TargetTable]);
    
    PRINT '✓ DataMappingConfig表创建成功';
END
ELSE
BEGIN
    PRINT '○ DataMappingConfig表已存在';
END

-- =============================================
-- 2. SqlExecutionConfig表 - SQL执行配置表
-- =============================================
-- 用途：存储待执行的SQL语句及其配置信息
-- 说明：系统根据DataMappingConfig生成SQL后，存储到此表，然后由SQL执行服务执行
-- 关联：与DataMappingConfig表配合使用（通过ConfigName关联）
--       与SqlExecutionLog表关联（记录执行历史）
-- 工作流程：
--   1. DataProcessingService根据DataMappingConfig生成SQL
--   2. SQL语句存储到SqlExecutionConfig表（SqlType='Mapping'）
--   3. SqlExecutionService定期检查此表中的待执行SQL
--   4. 执行SQL并记录结果到SqlExecutionLog表
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SqlExecutionConfig]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SqlExecutionConfig] (
        -- 主键：自增ID，唯一标识每条SQL配置
        [Id] INT IDENTITY(1,1) PRIMARY KEY,
        
        -- 配置名称：SQL配置的唯一名称
        -- 示例：'UpdateInspectionResultPD_20240101_001'
        -- 说明：通常包含映射配置名称、时间戳等信息
        [ConfigName] NVARCHAR(200) NOT NULL,
        
        -- SQL类型：SQL语句的类型
        -- 'Mapping'：由数据映射生成的SQL（最常见）
        -- 'Manual'：手动创建的SQL
        -- 说明：系统主要处理SqlType='Mapping'的SQL
        [SqlType] NVARCHAR(50) NOT NULL,
        
        -- SQL语句：要执行的SQL语句（完整的UPDATE、INSERT、DELETE或MERGE语句）
        -- 示例：'UPDATE QC_ProcessProgrammeRecordline SET InspectionResultPD = @Value WHERE sn = @MatchValue'
        -- 说明：支持参数化查询，参数通过Parameters字段传递
        [SqlStatement] NVARCHAR(MAX) NOT NULL,
        
        -- 参数：SQL语句的参数（JSON格式）
        -- 示例：'{"@Value":"PASS","@MatchValue":"SN001"}'
        -- 说明：参数以键值对形式存储，键为参数名，值为参数值
        [Parameters] NVARCHAR(MAX),
        
        -- 描述：SQL配置的详细说明
        -- 示例：'更新序列号SN001的InspectionResultPD字段为PASS'
        [Description] NVARCHAR(500),
        
        -- 是否激活：标识此SQL配置是否启用
        -- 1：启用，系统会执行此SQL
        -- 0：禁用，系统会忽略此SQL
        [IsActive] BIT NOT NULL DEFAULT 1,
        
        -- 执行顺序：SQL的执行顺序（数字越小越先执行）
        -- 说明：当有多个SQL需要执行时，按此顺序执行
        [ExecutionOrder] INT NOT NULL DEFAULT 0,
        
        -- 启用验证：是否启用SQL验证（强制验证，必须为1）
        -- 1：启用，执行前会验证SQL的安全性（检查危险关键字、WHERE子句等）
        -- 注意：所有SQL都必须通过验证才能执行，此字段保留用于兼容性，但应始终为1
        [ValidationEnabled] BIT NOT NULL DEFAULT 1,
        
        -- 最后执行时间：SQL最后一次执行的时间
        -- NULL：从未执行过
        -- 有值：最后一次执行的时间
        [LastExecuteTime] DATETIME NULL,
        
        -- 最后执行结果：SQL最后一次执行的结果
        -- 示例：'Success: 1 row affected', 'Error: Table not found'
        [LastExecuteResult] NVARCHAR(MAX),
        
        -- 执行次数：SQL已执行的次数
        -- 说明：每次成功执行后此值加1
        [ExecuteCount] INT NOT NULL DEFAULT 0,
        
        -- 创建时间：SQL配置创建的时间（自动记录）
        [CreateTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 更新时间：SQL配置最后修改的时间（自动更新）
        [UpdateTime] DATETIME NOT NULL DEFAULT GETDATE()
    );

    CREATE INDEX [idx_SqlExecutionConfig_IsActive] ON [dbo].[SqlExecutionConfig]([IsActive]);
    CREATE INDEX [idx_SqlExecutionConfig_ExecutionOrder] ON [dbo].[SqlExecutionConfig]([ExecutionOrder]);
    
    PRINT '✓ SqlExecutionConfig表创建成功';
END
ELSE
BEGIN
    PRINT '○ SqlExecutionConfig表已存在';
END

-- =============================================
-- 3. SqlExecutionLog表 - SQL执行日志表
-- =============================================
-- 用途：记录每次SQL执行的详细信息，用于审计和问题排查
-- 说明：每次执行SQL时都会记录一条日志，包括执行结果、耗时、影响行数等
-- 关联：与SqlExecutionConfig表通过ConfigId关联（多对一关系）
--       与FileInfo表通过SourceFileName关联（可选）
--
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[SqlExecutionLog]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[SqlExecutionLog] (
        -- 主键：自增ID，唯一标识每条执行日志
        [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,
        
        -- 配置ID：关联到SqlExecutionConfig表的Id字段
        -- 用于标识执行的是哪个SQL配置
        [ConfigId] INT NOT NULL,
        
        -- 配置名称：SQL配置的名称（冗余字段，便于查询）
        -- 示例：'UpdateInspectionResultPD_20240101_001'
        [ConfigName] NVARCHAR(200) NOT NULL,
        
        -- SQL语句：实际执行的SQL语句（完整语句）
        -- 说明：记录执行时的完整SQL，包括参数值（如果适用）
        [SqlStatement] NVARCHAR(MAX) NOT NULL,
        
        -- 参数：执行时使用的参数（JSON格式）
        -- 示例：'{"@Value":"PASS","@MatchValue":"SN001"}'
        [Parameters] NVARCHAR(MAX),
        
        -- 执行时间：SQL执行的时间（自动记录）
        [ExecutionTime] DATETIME NOT NULL DEFAULT GETDATE(),
        
        -- 执行耗时：SQL执行花费的时间（毫秒）
        -- 说明：用于性能分析和优化
        [ExecutionDuration] BIGINT,
        
        -- 影响行数：SQL执行后影响的数据行数
        -- UPDATE/DELETE：更新的行数或删除的行数
        -- INSERT：插入的行数
        -- 0：没有影响任何行（可能是WHERE条件不匹配）
        [RowsAffected] INT,
        
        -- 是否成功：SQL执行是否成功
        -- 1：成功执行
        -- 0：执行失败
        [IsSuccess] BIT NOT NULL,
        
        -- 错误信息：如果执行失败，记录错误信息
        -- 示例：'Table ''QC_ProcessProgrammeRecordline'' not found'
        -- NULL：执行成功时为空
        [ErrorMessage] NVARCHAR(MAX),
        
        -- 源文件名：如果SQL与特定文件相关，记录文件名
        -- 示例：'CMM_Report_20240101.xlsx'
        -- NULL：如果SQL与文件无关
        [SourceFileName] NVARCHAR(500)
    );

    CREATE INDEX [idx_SqlExecutionLog_ConfigId] ON [dbo].[SqlExecutionLog]([ConfigId]);
    CREATE INDEX [idx_SqlExecutionLog_ExecutionTime] ON [dbo].[SqlExecutionLog]([ExecutionTime]);
    CREATE INDEX [idx_SqlExecutionLog_IsSuccess] ON [dbo].[SqlExecutionLog]([IsSuccess]);
    
    PRINT '✓ SqlExecutionLog表创建成功';
END
ELSE
BEGIN
    PRINT '○ SqlExecutionLog表已存在';
END

-- =============================================
-- 第三部分：插入示例配置数据
-- =============================================
PRINT '';
PRINT '--- 第三部分：插入示例配置 ---';

-- 插入数据映射配置示例
IF NOT EXISTS (SELECT * FROM [dbo].[DataMappingConfig] WHERE ConfigName = 'UpdateInspectionResultPD')
BEGIN
    INSERT INTO [dbo].[DataMappingConfig] 
    (ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField, 
     TargetTable, TargetMatchField, TargetUpdateField, Description)
    VALUES 
    ('UpdateInspectionResultPD', 'FileData', 'Excel', 'sn', 'InspectionResultPD',
     'QC_ProcessProgrammeRecordline', 'sn', 'InspectionResultPD',
     '根据sn匹配，将Excel的InspectionResultPD值更新到QC_ProcessProgrammeRecordline');
    PRINT '✓ 已插入示例配置：UpdateInspectionResultPD';
END
ELSE
BEGIN
    PRINT '○ 示例配置UpdateInspectionResultPD已存在';
END

IF NOT EXISTS (SELECT * FROM [dbo].[DataMappingConfig] WHERE ConfigName = 'UpdateInspectionResultQC')
BEGIN
    INSERT INTO [dbo].[DataMappingConfig] 
    (ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField, 
     TargetTable, TargetMatchField, TargetUpdateField, Description)
    VALUES 
    ('UpdateInspectionResultQC', 'FileData', 'Excel', 'sn', 'InspectionResultQC',
     'QC_ProcessProgrammeRecordline', 'sn', 'InspectionResultQC',
     '根据sn匹配，将Excel的InspectionResultQC值更新到QC_ProcessProgrammeRecordline');
    PRINT '✓ 已插入示例配置：UpdateInspectionResultQC';
END
ELSE
BEGIN
    PRINT '○ 示例配置UpdateInspectionResultQC已存在';
END

-- =============================================
-- 完成
-- =============================================
PRINT '';
PRINT '========================================';
PRINT '数据库初始化完成！';
PRINT '========================================';
PRINT '';
PRINT '已创建的表：';
PRINT '  基础表：FileInfo, FileData, SystemLog';
PRINT '  配置表：DataMappingConfig, SqlExecutionConfig, SqlExecutionLog';
PRINT '';
PRINT '提示：';
PRINT '  - 如果表已存在，脚本会自动跳过创建';
PRINT '  - 示例配置已插入到DataMappingConfig表';
PRINT '  - 业务表（如QC_ProcessProgrammeRecordline）需要在业务数据库中已存在';
PRINT '  - 通过DataMappingConfig配置将FileData数据映射到实际业务表';
PRINT '';

