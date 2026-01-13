-- =============================================
-- 为DataMappingConfig表添加自定义SQL模板支持
-- 说明：支持复杂的JOIN查询和自定义SQL模板
-- =============================================

-- 添加CustomSqlTemplate字段（如果不存在）
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DataMappingConfig]') AND name = 'CustomSqlTemplate')
BEGIN
    ALTER TABLE [dbo].[DataMappingConfig] ADD [CustomSqlTemplate] NVARCHAR(MAX) NULL;
    PRINT '✓ 已添加CustomSqlTemplate字段到DataMappingConfig表';
END
ELSE
BEGIN
    PRINT '○ CustomSqlTemplate字段已存在';
END

-- 添加UseCustomSqlTemplate字段（如果不存在）
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DataMappingConfig]') AND name = 'UseCustomSqlTemplate')
BEGIN
    ALTER TABLE [dbo].[DataMappingConfig] ADD [UseCustomSqlTemplate] BIT NOT NULL DEFAULT 0;
    PRINT '✓ 已添加UseCustomSqlTemplate字段到DataMappingConfig表';
END
ELSE
BEGIN
    PRINT '○ UseCustomSqlTemplate字段已存在';
END

-- 添加TemplateParameters字段（如果不存在）- 用于存储模板参数说明（JSON格式）
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[DataMappingConfig]') AND name = 'TemplateParameters')
BEGIN
    ALTER TABLE [dbo].[DataMappingConfig] ADD [TemplateParameters] NVARCHAR(MAX) NULL;
    PRINT '✓ 已添加TemplateParameters字段到DataMappingConfig表';
END
ELSE
BEGIN
    PRINT '○ TemplateParameters字段已存在';
END

PRINT '';
PRINT '========================================';
PRINT '字段说明：';
PRINT '  CustomSqlTemplate: 自定义SQL模板，支持占位符 {FileInfoId}, {ColumnName}, {RowNumber}, {PartName} 等';
PRINT '  UseCustomSqlTemplate: 是否使用自定义SQL模板（1=使用，0=使用标准映射）';
PRINT '  TemplateParameters: 模板参数说明（JSON格式），用于说明模板中使用的占位符';
PRINT '========================================';
