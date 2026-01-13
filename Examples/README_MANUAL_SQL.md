# 手动创建待执行SQL示例（直接在数据库中执行）

本文档提供了直接在数据库中手动插入SQL配置的示例。

## 基本格式

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('配置名称', 
 'Manual', 
 'SQL语句',
 '参数JSON字符串或NULL',
 '描述信息',
 1, 0, 1, GETDATE(), GETDATE());
```

## 字段说明

| 字段 | 类型 | 说明 | 必填 |
|------|------|------|------|
| ConfigName | NVARCHAR(200) | 配置名称，建议使用唯一标识 | 是 |
| SqlType | NVARCHAR(50) | SQL类型，手动创建使用 'Manual' | 是 |
| SqlStatement | NVARCHAR(MAX) | SQL语句 | 是 |
| Parameters | NVARCHAR(MAX) | 参数JSON字符串，格式：'{"@Param1":"Value1","@Param2":123}'，不需要参数时设为NULL | 否 |
| Description | NVARCHAR(500) | 描述信息 | 否 |
| IsActive | BIT | 是否激活，必须为 1 | 是 |
| ExecutionOrder | INT | 执行顺序，数字越小越先执行 | 是 |
| ValidationEnabled | BIT | 是否启用验证，必须为 1（强制验证） | 是 |
| CreateTime | DATETIME | 创建时间，使用 GETDATE() | 是 |
| UpdateTime | DATETIME | 更新时间，使用 GETDATE() | 是 |

## 示例

### 示例1：简单的UPDATE语句

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('手动更新订单状态_20240101_001', 
 'Manual', 
 'UPDATE Orders SET Status = @Status WHERE OrderId = @OrderId',
 '{"@Status":"Completed","@OrderId":"12345"}',
 '手动更新订单ID为12345的状态为已完成',
 1, 0, 1, GETDATE(), GETDATE());
```

### 示例2：INSERT语句

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('插入新用户记录', 
 'Manual', 
 'INSERT INTO Users (UserName, Email, CreateTime, Status) VALUES (@UserName, @Email, GETDATE(), ''Active'')',
 '{"@UserName":"testuser","@Email":"test@example.com"}',
 '插入一个新用户记录',
 1, 0, 1, GETDATE(), GETDATE());
```

### 示例3：DELETE语句（必须包含WHERE条件）

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('删除过期日志', 
 'Manual', 
 'DELETE FROM SystemLog WHERE LogTime < DATEADD(DAY, -30, GETDATE()) AND LogLevel = ''Information''',
 NULL,
 '删除30天前的信息级别日志',
 1, 20, 1, GETDATE(), GETDATE());
```

### 示例4：不带参数的UPDATE语句

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('更新所有待处理订单', 
 'Manual', 
 'UPDATE Orders SET Status = ''Processing'', UpdateTime = GETDATE() WHERE Status = ''Pending'' AND CreateTime < DATEADD(HOUR, -1, GETDATE())',
 NULL,
 '更新1小时前创建的待处理订单为处理中状态',
 1, 10, 1, GETDATE(), GETDATE());
```

### 示例5：使用时间戳生成唯一配置名称

```sql
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('手动SQL_' + CONVERT(VARCHAR(20), GETDATE(), 112) + '_' + RIGHT('0000' + CAST(ABS(CHECKSUM(NEWID())) % 10000 AS VARCHAR), 4), 
 'Manual', 
 'UPDATE YourTable SET Column1 = @Value WHERE Id = @Id',
 '{"@Value":"NewValue","@Id":123}',
 '使用时间戳生成唯一配置名称',
 1, 0, 1, GETDATE(), GETDATE());
```

## SQL验证规则

所有SQL都必须符合以下规则：

1. **禁止的危险关键字**：`DROP`, `TRUNCATE`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`
2. **允许的SQL类型**：只允许 `UPDATE`, `INSERT`, `DELETE`, `MERGE`
3. **WHERE条件要求**：`UPDATE` 和 `DELETE` 必须包含 `WHERE` 条件
4. **语法验证**：SQL语法必须正确
5. **行数限制**：执行时自动限制最多影响10行数据

## 参数JSON格式

参数必须是有效的JSON字符串：

```json
{"@Param1":"字符串值","@Param2":123,"@Param3":true,"@Param4":null}
```

**注意**：
- 参数名必须以 `@` 开头
- 字符串值需要用双引号括起来
- 数字不需要引号
- 布尔值使用 `true` 或 `false`
- NULL值使用 `null`（小写）
- 如果SQL不需要参数，Parameters字段设置为 `NULL`

## 执行顺序

- `ExecutionOrder` 字段控制执行顺序
- 数字越小越先执行
- 相同顺序的SQL按ID排序执行
- 建议手动创建的SQL使用较大的顺序号（如10、20、30），避免与自动生成的SQL冲突

## 注意事项

1. **SQL验证是强制的**：即使 `ValidationEnabled` 设置为1，系统也会强制验证所有SQL
2. **配置名称唯一性**：建议使用时间戳或唯一标识符确保名称唯一
3. **自动执行**：SQL添加到数据库后，系统会在下次循环时自动执行
4. **执行限制**：DML语句（UPDATE/INSERT/DELETE/MERGE）最多影响10行数据
5. **字符串转义**：SQL语句中的单引号需要用两个单引号转义（如 `''Active''`）

## 查询待执行的SQL

```sql
SELECT Id, ConfigName, SqlType, SqlStatement, Parameters, Description, 
       ExecutionOrder, IsActive, ValidationEnabled, CreateTime
FROM SqlExecutionConfig 
WHERE IsActive = 1 
  AND (LastExecuteTime IS NULL OR ExecuteCount = 0)
ORDER BY ExecutionOrder, Id;
```

## 查看执行结果

### 查看SQL执行历史

```sql
SELECT TOP 100
    l.Id,
    l.ConfigName,
    l.ExecutionTime,
    l.ExecutionDuration,
    l.RowsAffected,
    l.IsSuccess,
    l.ErrorMessage,
    c.Description
FROM SqlExecutionLog l
LEFT JOIN SqlExecutionConfig c ON l.ConfigId = c.Id
ORDER BY l.ExecutionTime DESC;
```

### 查看特定SQL的执行结果

```sql
SELECT 
    c.ConfigName,
    c.LastExecuteTime,
    c.LastExecuteResult,
    c.ExecuteCount,
    l.ExecutionTime,
    l.RowsAffected,
    l.IsSuccess,
    l.ErrorMessage
FROM SqlExecutionConfig c
LEFT JOIN SqlExecutionLog l ON c.Id = l.ConfigId
WHERE c.ConfigName = '手动更新订单状态_20240101_001'
ORDER BY l.ExecutionTime DESC;
```

## 完整示例文件

更多示例请查看 `Examples/ManualSqlInsertExamples.sql` 文件。
