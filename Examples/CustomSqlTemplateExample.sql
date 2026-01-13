-- =============================================
-- 自定义SQL模板配置示例
-- 说明：支持复杂的JOIN查询和自定义SQL模板
-- =============================================

-- =============================================
-- 示例1：复杂JOIN查询更新QC_ProcessProgrammeRecordline
-- =============================================
-- 需求：从FileData中提取PartName和ColumnValue（条件：ColumnName='9. Results' AND RowNumber=8）
--       与QC_ProcessProgrammeRecordline和QC_ProcessProgrammeRecord进行JOIN
--       匹配条件：PartName=SN AND InspectionItemName='精度检验：大底面平面度'
--       更新字段：InspectionResultPD
-- =============================================

INSERT INTO DataMappingConfig 
(ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField,
 TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description,
 UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, IsActive, CreateTime, UpdateTime)
VALUES 
('更新QC记录_平面度检验结果',
 'FileData',
 'Excel',
 'PartName',  -- 源匹配字段（用于占位符，实际不使用）
 '9. Results',  -- 源数据字段（用于占位符，实际不使用）
 'QC_ProcessProgrammeRecordline',
 'sn',  -- 目标匹配字段（用于占位符，实际不使用）
 'InspectionResultPD',  -- 目标更新字段（用于占位符，实际不使用）
 NULL,
 '根据PartName匹配，更新QC_ProcessProgrammeRecordline的InspectionResultPD字段（平面度检验结果）',
 1,  -- 使用自定义SQL模板
 -- 自定义SQL模板（支持占位符）
 N'UPDATE QC_ProcessProgrammeRecordline 
SET InspectionResultPD = F.ColumnValue
FROM (
    SELECT fi.PartName, fd.RowNumber, fd.ColumnValue 
    FROM FileData fd
    LEFT JOIN FileInfo fi ON fd.FileInfoId = fi.Id 
    WHERE fd.ColumnName = ''9. Results'' 
      AND fd.RowNumber = 8
      AND fd.FileInfoId = {FileInfoId}
) F 
JOIN (
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName, qpl.InspectionResultPD, qpl.InspectionResultQC, qpl.InspectionResultQC2 
    FROM QC_ProcessProgrammeRecordline qpl  
    LEFT JOIN QC_ProcessProgrammeRecord qpr ON qpl.ProcessProgrammeRecordId = qpr.Id 
    WHERE qpl.InspectionItemName = ''精度检验：大底面平面度''
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id',
 -- 模板参数说明（JSON格式）
 N'{"ColumnName":"9. Results","RowNumber":"8"}',
 1,
 GETDATE(),
 GETDATE());

-- =============================================
-- 示例2：更新多个字段（InspectionResultPD和InspectionResultQC）
-- =============================================
INSERT INTO DataMappingConfig 
(ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField,
 TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description,
 UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, IsActive, CreateTime, UpdateTime)
VALUES 
('更新QC记录_多个检验结果',
 'FileData',
 'Excel',
 'PartName',
 '9. Results',
 'QC_ProcessProgrammeRecordline',
 'sn',
 'InspectionResultPD',
 NULL,
 '根据PartName匹配，更新QC_ProcessProgrammeRecordline的多个检验结果字段',
 1,
 N'UPDATE QC_ProcessProgrammeRecordline 
SET 
    InspectionResultPD = F.ColumnValue,
    InspectionResultQC = F.ColumnValue,
    UpdateTime = GETDATE()
FROM (
    SELECT fi.PartName, fd.ColumnValue 
    FROM FileData fd
    LEFT JOIN FileInfo fi ON fd.FileInfoId = fi.Id 
    WHERE fd.ColumnName = ''9. Results'' 
      AND fd.RowNumber = 8
      AND fd.FileInfoId = {FileInfoId}
) F 
JOIN (
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName
    FROM QC_ProcessProgrammeRecordline qpl  
    LEFT JOIN QC_ProcessProgrammeRecord qpr ON qpl.ProcessProgrammeRecordId = qpr.Id 
    WHERE qpl.InspectionItemName = ''精度检验：大底面平面度''
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id',
 N'{"ColumnName":"9. Results","RowNumber":"8"}',
 1,
 GETDATE(),
 GETDATE());

-- =============================================
-- 示例3：使用不同的列名和行号
-- =============================================
INSERT INTO DataMappingConfig 
(ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField,
 TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description,
 UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, IsActive, CreateTime, UpdateTime)
VALUES 
('更新QC记录_其他检验项',
 'FileData',
 'Excel',
 'PartName',
 '10. Results',
 'QC_ProcessProgrammeRecordline',
 'sn',
 'InspectionResultQC',
 NULL,
 '根据PartName匹配，更新其他检验项的InspectionResultQC字段',
 1,
 N'UPDATE QC_ProcessProgrammeRecordline 
SET InspectionResultQC = F.ColumnValue
FROM (
    SELECT fi.PartName, fd.ColumnValue 
    FROM FileData fd
    LEFT JOIN FileInfo fi ON fd.FileInfoId = fi.Id 
    WHERE fd.ColumnName = ''10. Results'' 
      AND fd.RowNumber = 9
      AND fd.FileInfoId = {FileInfoId}
) F 
JOIN (
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName
    FROM QC_ProcessProgrammeRecordline qpl  
    LEFT JOIN QC_ProcessProgrammeRecord qpr ON qpl.ProcessProgrammeRecordId = qpr.Id 
    WHERE qpl.InspectionItemName = ''精度检验：其他项目''
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id',
 N'{"ColumnName":"10. Results","RowNumber":"9"}',
 1,
 GETDATE(),
 GETDATE());

-- =============================================
-- 占位符说明
-- =============================================
-- {FileInfoId}      - 当前文件的FileInfo.Id
-- {SourceFileName}  - 源文件名
-- {PartName}        - 零件名称（从FileInfo表）
-- {PartNumber}      - 零件号（从FileInfo表）
-- {SerialNumber}    - 序列号（从FileInfo表）
-- {FileType}        - 文件类型（Excel/PDF）
-- {ColumnName}      - 列名（从FileData表，需要在TemplateParameters中指定）
-- {RowNumber}       - 行号（从FileData表，需要在TemplateParameters中指定）
-- {ColumnValue}     - 列值（从FileData表，需要在TemplateParameters中指定）
--
-- 注意：
-- 1. 如果SQL模板中包含 {ColumnName}、{RowNumber} 或 {ColumnValue}，
--    必须在 TemplateParameters 中指定 ColumnName 和 RowNumber
-- 2. 系统会为每个匹配的数据行生成一条SQL记录
-- 3. 生成的SQL会保存到 SqlExecutionConfig 表，由SQL执行服务执行
-- 4. SQL必须符合验证规则（不能包含DROP、TRUNCATE等危险关键字）
-- =============================================

-- =============================================
-- 查询配置（验证是否添加成功）
-- =============================================
SELECT Id, ConfigName, UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, Description
FROM DataMappingConfig
WHERE UseCustomSqlTemplate = 1
ORDER BY Id;
