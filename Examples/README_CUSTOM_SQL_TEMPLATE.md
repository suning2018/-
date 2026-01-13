# 自定义SQL模板使用说明

## 概述

自定义SQL模板功能允许您配置复杂的JOIN查询，将FileData表中的数据更新到业务表中。这对于需要多表关联、复杂条件匹配的场景非常有用。

## 功能特点

- ✅ 支持复杂的JOIN查询（多表关联）
- ✅ 支持占位符替换（FileInfoId、PartName、ColumnName等）
- ✅ 支持按列名和行号过滤数据
- ✅ 自动生成SQL并保存到SqlExecutionConfig表
- ✅ 与标准映射配置共存

## 数据库表结构扩展

首先需要执行SQL脚本添加新字段：

```sql
-- 执行 Scripts/AddCustomSqlTemplateSupport.sql
```

该脚本会添加以下字段到`DataMappingConfig`表：
- `CustomSqlTemplate` (NVARCHAR(MAX)) - 自定义SQL模板
- `UseCustomSqlTemplate` (BIT) - 是否使用自定义SQL模板（1=使用，0=使用标准映射）
- `TemplateParameters` (NVARCHAR(MAX)) - 模板参数说明（JSON格式）

## 配置示例

### 示例1：复杂JOIN查询更新QC_ProcessProgrammeRecordline

```sql
INSERT INTO DataMappingConfig 
(ConfigName, SourceTable, SourceFileType, SourceMatchField, SourceDataField,
 TargetTable, TargetMatchField, TargetUpdateField, MatchCondition, Description,
 UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, IsActive, CreateTime, UpdateTime)
VALUES 
('更新QC记录_平面度检验结果',
 'FileData',
 'Excel',
 'PartName',
 '9. Results',
 'QC_ProcessProgrammeRecordline',
 'sn',
 'InspectionResultPD',
 NULL,
 '根据PartName匹配，更新QC_ProcessProgrammeRecordline的InspectionResultPD字段（平面度检验结果）',
 1,  -- 使用自定义SQL模板
 -- 自定义SQL模板
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
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName
    FROM QC_ProcessProgrammeRecordline qpl  
    LEFT JOIN QC_ProcessProgrammeRecord qpr ON qpl.ProcessProgrammeRecordId = qpr.Id 
    WHERE qpl.InspectionItemName = ''精度检验：大底面平面度''
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id',
 -- 模板参数说明
 N'{"ColumnName":"9. Results","RowNumber":"8"}',
 1,
 GETDATE(),
 GETDATE());
```

## 占位符说明

SQL模板支持以下占位符：

| 占位符 | 说明 | 来源 |
|--------|------|------|
| `{FileInfoId}` | 当前文件的FileInfo.Id | FileInfo表 |
| `{SourceFileName}` | 源文件名 | FileInfo表 |
| `{PartName}` | 零件名称 | FileInfo表 |
| `{PartNumber}` | 零件号 | FileInfo表 |
| `{SerialNumber}` | 序列号 | FileInfo表 |
| `{FileType}` | 文件类型（Excel/PDF） | FileInfo表 |
| `{ColumnName}` | 列名 | FileData表（需在TemplateParameters中指定） |
| `{RowNumber}` | 行号 | FileData表（需在TemplateParameters中指定） |
| `{ColumnValue}` | 列值 | FileData表（需在TemplateParameters中指定） |

## TemplateParameters格式

`TemplateParameters`字段使用JSON格式，用于指定需要匹配的列名和行号：

```json
{
  "ColumnName": "9. Results",
  "RowNumber": "8"
}
```

**注意**：
- 如果SQL模板中包含`{ColumnName}`、`{RowNumber}`或`{ColumnValue}`，必须在`TemplateParameters`中指定`ColumnName`和`RowNumber`
- 系统会为每个匹配的数据行生成一条SQL记录
- 如果模板中不包含这些占位符，则不需要指定`TemplateParameters`（可以为NULL）

## 工作流程

1. **文件处理阶段**：
   - 系统读取FileData表中的数据
   - 检查是否有`UseCustomSqlTemplate = 1`的配置

2. **SQL生成阶段**：
   - 如果配置了自定义SQL模板，系统会：
     - 替换模板中的占位符（如`{FileInfoId}`、`{PartName}`等）
     - 如果模板包含`{ColumnName}`或`{RowNumber}`，会根据`TemplateParameters`过滤数据行
     - 为每个匹配的数据行生成一条SQL记录

3. **SQL执行阶段**：
   - 生成的SQL保存到`SqlExecutionConfig`表
   - SQL执行服务会验证并执行这些SQL
   - 执行结果记录到`SqlExecutionLog`表

## 使用场景

### 场景1：多表JOIN更新

需要从FileData和FileInfo中提取数据，与业务表进行JOIN，然后更新业务表。

```sql
UPDATE QC_ProcessProgrammeRecordline 
SET InspectionResultPD = F.ColumnValue
FROM (
    SELECT fi.PartName, fd.ColumnValue 
    FROM FileData fd
    LEFT JOIN FileInfo fi ON fd.FileInfoId = fi.Id 
    WHERE fd.ColumnName = '9. Results' 
      AND fd.RowNumber = 8
      AND fd.FileInfoId = {FileInfoId}
) F 
JOIN (
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName
    FROM QC_ProcessProgrammeRecordline qpl  
    LEFT JOIN QC_ProcessProgrammeRecord qpr ON qpl.ProcessProgrammeRecordId = qpr.Id 
    WHERE qpl.InspectionItemName = '精度检验：大底面平面度'
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id
```

### 场景2：更新多个字段

一次更新多个字段：

```sql
UPDATE QC_ProcessProgrammeRecordline 
SET 
    InspectionResultPD = F.ColumnValue,
    InspectionResultQC = F.ColumnValue,
    UpdateTime = GETDATE()
FROM (
    SELECT fi.PartName, fd.ColumnValue 
    FROM FileData fd
    LEFT JOIN FileInfo fi ON fd.FileInfoId = fi.Id 
    WHERE fd.ColumnName = '9. Results' 
      AND fd.RowNumber = 8
      AND fd.FileInfoId = {FileInfoId}
) F 
JOIN (
    SELECT qpl.Id, qpl.sn, qpl.InspectionItemName
    FROM QC_ProcessProgrammeRecordline qpl  
    WHERE qpl.InspectionItemName = '精度检验：大底面平面度'
) R ON F.PartName = R.sn
WHERE QC_ProcessProgrammeRecordline.Id = R.Id
```

## 注意事项

1. **SQL验证**：
   - 生成的SQL必须符合系统的SQL验证规则
   - 不能包含：DROP, TRUNCATE, ALTER, CREATE, EXEC, EXECUTE
   - 只允许：UPDATE, INSERT, DELETE, MERGE
   - UPDATE和DELETE必须包含WHERE条件

2. **行数限制**：
   - DML操作最多影响10条记录（系统会自动添加SET ROWCOUNT 10）

3. **占位符替换**：
   - 占位符使用大括号`{}`包围
   - 字符串值会自动转义（单引号会转义为两个单引号）
   - 如果占位符对应的值为NULL，会替换为空字符串

4. **性能考虑**：
   - 复杂的JOIN查询可能影响性能
   - 建议在业务表上创建适当的索引
   - 避免在模板中使用子查询（如果可能）

5. **调试建议**：
   - 先在数据库中手动测试SQL模板
   - 确认占位符替换后的SQL是否正确
   - 检查生成的SQL是否保存到`SqlExecutionConfig`表
   - 查看`SqlExecutionLog`表了解执行结果

## 查询和验证

### 查看自定义SQL模板配置

```sql
SELECT Id, ConfigName, UseCustomSqlTemplate, CustomSqlTemplate, TemplateParameters, Description
FROM DataMappingConfig
WHERE UseCustomSqlTemplate = 1
ORDER BY Id;
```

### 查看生成的SQL

```sql
SELECT Id, ConfigName, SqlType, SqlStatement, Parameters, Description, CreateTime
FROM SqlExecutionConfig
WHERE SqlType = 'Mapping'
  AND ConfigName LIKE 'CustomTemplate_%'
ORDER BY CreateTime DESC;
```

### 查看SQL执行日志

```sql
SELECT TOP 100
    l.Id,
    l.ConfigName,
    l.ExecutionTime,
    l.RowsAffected,
    l.IsSuccess,
    l.ErrorMessage,
    c.SqlStatement
FROM SqlExecutionLog l
LEFT JOIN SqlExecutionConfig c ON l.ConfigId = c.Id
WHERE c.ConfigName LIKE 'CustomTemplate_%'
ORDER BY l.ExecutionTime DESC;
```

## 完整示例

参考 `Examples/CustomSqlTemplateExample.sql` 文件，其中包含多个完整的配置示例。

## 与标准映射的区别

| 特性 | 标准映射 | 自定义SQL模板 |
|------|---------|--------------|
| 配置复杂度 | 简单 | 复杂 |
| SQL生成方式 | 自动生成简单UPDATE | 使用自定义SQL模板 |
| 支持JOIN | ❌ | ✅ |
| 支持多表关联 | ❌ | ✅ |
| 灵活性 | 低 | 高 |
| 适用场景 | 简单的字段映射 | 复杂的业务逻辑 |

## 常见问题

**Q: 为什么生成的SQL没有执行？**
A: 检查以下几点：
1. `SqlExecutionConfig`表中`IsActive`是否为1
2. `ExecuteCount`是否为0（如果已执行过，需要重置）
3. 查看`SqlExecutionLog`表了解执行结果和错误信息

**Q: 如何调试SQL模板？**
A: 
1. 手动替换占位符，在数据库中测试SQL
2. 查看`SqlExecutionConfig`表中生成的SQL
3. 检查`SqlExecutionLog`表中的错误信息

**Q: 可以同时使用标准映射和自定义SQL模板吗？**
A: 可以，系统会分别处理标准映射配置和自定义SQL模板配置。

**Q: 占位符替换失败怎么办？**
A: 检查占位符拼写是否正确，确保使用大括号`{}`。如果值为NULL，会替换为空字符串。
