-- =============================================
-- 检查和修复 FileData 表结构
-- 用途：检查 FileData 表是否存在 FileInfoId 列，如果不存在则添加
-- =============================================

PRINT '========================================';
PRINT '检查 FileData 表结构...';
PRINT '========================================';

-- 检查表是否存在
IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[FileData]') AND type in (N'U'))
BEGIN
    PRINT '✓ FileData 表存在';
    
    -- 检查 FileInfoId 列是否存在
    IF NOT EXISTS (
        SELECT * FROM sys.columns 
        WHERE object_id = OBJECT_ID(N'[dbo].[FileData]') 
        AND name = 'FileInfoId'
    )
    BEGIN
        PRINT '✗ FileInfoId 列不存在，开始添加...';
        
        -- 添加 FileInfoId 列
        ALTER TABLE [dbo].[FileData]
        ADD [FileInfoId] INT NULL;
        
        -- 如果有现有数据，需要先处理（这里假设没有数据，或者需要手动处理）
        -- 如果有数据，需要先更新 FileInfoId 的值，然后再设置为 NOT NULL
        
        -- 设置为 NOT NULL（如果表中没有数据）
        -- 如果有数据，需要先更新 FileInfoId 的值
        -- ALTER TABLE [dbo].[FileData] ALTER COLUMN [FileInfoId] INT NOT NULL;
        
        -- 添加外键约束
        IF NOT EXISTS (
            SELECT * FROM sys.foreign_keys 
            WHERE name = 'FK_FileData_FileInfo'
        )
        BEGIN
            ALTER TABLE [dbo].[FileData]
            ADD CONSTRAINT [FK_FileData_FileInfo] 
            FOREIGN KEY ([FileInfoId]) REFERENCES [dbo].[FileInfo]([Id]) ON DELETE CASCADE;
            
            PRINT '✓ 外键约束 FK_FileData_FileInfo 已添加';
        END
        
        -- 添加索引
        IF NOT EXISTS (
            SELECT * FROM sys.indexes 
            WHERE name = 'idx_FileData_FileInfoId' 
            AND object_id = OBJECT_ID(N'[dbo].[FileData]')
        )
        BEGIN
            CREATE INDEX [idx_FileData_FileInfoId] ON [dbo].[FileData]([FileInfoId]);
            PRINT '✓ 索引 idx_FileData_FileInfoId 已添加';
        END
        
        PRINT '✓ FileInfoId 列已添加';
    END
    ELSE
    BEGIN
        PRINT '✓ FileInfoId 列已存在';
    END
    
    -- 显示当前表结构
    PRINT '';
    PRINT '当前 FileData 表结构：';
    SELECT 
        c.name AS ColumnName,
        t.name AS DataType,
        c.max_length AS MaxLength,
        c.is_nullable AS IsNullable
    FROM sys.columns c
    INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'[dbo].[FileData]')
    ORDER BY c.column_id;
END
ELSE
BEGIN
    PRINT '✗ FileData 表不存在，请先执行 InitializeDatabase.sql 创建表';
END

PRINT '';
PRINT '========================================';
PRINT '检查完成';
PRINT '========================================';
