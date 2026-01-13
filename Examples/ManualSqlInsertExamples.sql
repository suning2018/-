-- =============================================
-- 手动创建待执行SQL的示例（直接在数据库中执行）
-- =============================================
-- 说明：这些SQL语句可以直接在数据库中执行，用于手动添加待执行的SQL配置
-- 执行后，系统会在下次循环时自动执行这些SQL
-- =============================================

-- =============================================
-- 示例1：简单的UPDATE语句
-- =============================================
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

-- =============================================
-- 示例2：带多个条件的UPDATE语句
-- =============================================
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('批量更新产品价格', 
 'Manual', 
 'UPDATE Products SET Price = @NewPrice, UpdateTime = GETDATE() WHERE CategoryId = @CategoryId AND Status = ''Active'' AND Price < @OldPrice',
 '{"@NewPrice":99.99,"@CategoryId":5,"@OldPrice":100.00}',
 '更新类别5中价格低于100的活跃产品价格为99.99',
 1, 5, 1, GETDATE(), GETDATE());

-- =============================================
-- 示例3：INSERT语句
-- =============================================
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

-- =============================================
-- 示例4：DELETE语句（必须包含WHERE条件）
-- =============================================
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

-- =============================================
-- 示例5：MERGE语句
-- =============================================
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('合并更新库存信息', 
 'Manual', 
 'MERGE INTO Inventory AS target USING (SELECT @ProductId AS ProductId, @Quantity AS Quantity) AS source ON target.ProductId = source.ProductId WHEN MATCHED THEN UPDATE SET Quantity = source.Quantity, UpdateTime = GETDATE() WHEN NOT MATCHED THEN INSERT (ProductId, Quantity, CreateTime) VALUES (source.ProductId, source.Quantity, GETDATE())',
 '{"@ProductId":1001,"@Quantity":50}',
 '合并更新产品1001的库存信息，如果不存在则插入',
 1, 15, 1, GETDATE(), GETDATE());

-- =============================================
-- 示例6：更新业务表数据（实际业务场景）
-- =============================================
INSERT INTO SqlExecutionConfig 
(ConfigName, SqlType, SqlStatement, Parameters, Description, 
 IsActive, ExecutionOrder, ValidationEnabled, CreateTime, UpdateTime)
VALUES 
('更新QC记录状态', 
 'Manual', 
 'UPDATE QC_ProcessProgrammeRecordline SET InspectionResultPD = @Result WHERE sn = @SerialNumber',
 '{"@Result":"PASS","@SerialNumber":"SN001"}',
 '更新序列号SN001的检验结果为PASS',
 1, 0, 1, GETDATE(), GETDATE());

-- =============================================
-- 示例7：不带参数的UPDATE语句
-- =============================================
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

-- =============================================
-- 示例8：使用时间戳的配置名称（推荐）
-- =============================================
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

-- =============================================
-- 注意事项
-- =============================================
-- 1. ConfigName：配置名称，建议使用唯一标识（如时间戳）
-- 2. SqlType：SQL类型，手动创建的通常使用 'Manual'
-- 3. SqlStatement：SQL语句，必须符合验证规则：
--    - 不能包含：DROP, TRUNCATE, ALTER, CREATE, EXEC, EXECUTE
--    - 只允许：UPDATE, INSERT, DELETE, MERGE
--    - UPDATE和DELETE必须包含WHERE条件
-- 4. Parameters：参数JSON字符串，格式：'{"@Param1":"Value1","@Param2":123}'
--    如果不需要参数，设置为 NULL
-- 5. Description：描述信息，可选但建议填写
-- 6. IsActive：必须为 1（激活状态）
-- 7. ExecutionOrder：执行顺序，数字越小越先执行
-- 8. ValidationEnabled：必须为 1（强制验证）
-- 9. CreateTime 和 UpdateTime：使用 GETDATE() 自动设置
--
-- 执行限制：
-- - DML语句（UPDATE/INSERT/DELETE/MERGE）最多影响10行数据
-- - 系统会在下次循环时自动执行这些SQL
-- - 执行结果会记录到 SqlExecutionLog 表
-- =============================================

-- =============================================
-- 查询待执行的SQL（验证是否添加成功）
-- =============================================
SELECT Id, ConfigName, SqlType, SqlStatement, Parameters, Description, 
       ExecutionOrder, IsActive, ValidationEnabled, CreateTime
FROM SqlExecutionConfig 
WHERE IsActive = 1 
  AND (LastExecuteTime IS NULL OR ExecuteCount = 0)
ORDER BY ExecutionOrder, Id;

-- =============================================
-- 查看SQL执行历史
-- =============================================
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
