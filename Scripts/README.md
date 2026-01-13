# 数据库初始化脚本说明

## 脚本文件

- **InitializeDatabase.sql** - 完整的数据库初始化脚本（推荐使用）

## 使用方法

### 方式一：使用 SQL Server Management Studio (SSMS)

1. 打开 SQL Server Management Studio
2. 连接到目标数据库服务器
3. 选择要使用的数据库（或创建新数据库）
4. 打开 `InitializeDatabase.sql` 文件
5. 点击"执行"按钮（F5）运行脚本

### 方式二：使用命令行

```bash
sqlcmd -S 服务器名 -d 数据库名 -U 用户名 -P 密码 -i InitializeDatabase.sql
```

### 方式三：使用 Azure Data Studio

1. 打开 Azure Data Studio
2. 连接到数据库
3. 打开 `InitializeDatabase.sql` 文件
4. 点击"运行"按钮执行脚本

## 脚本功能

此脚本会按顺序创建以下内容：

### 第一部分：基础表
- **FileInfo** - 文件信息表（存储Excel和PDF文件的基本信息）
- **FileData** - 文件数据表（存储文件中的具体数据）
- **SystemLog** - 系统日志表（存储程序运行日志）

### 第二部分：业务表
- **MeasurementData** - 测量数据表（存储Excel三坐标测量数据）
- **DiagnosticData** - 诊断数据表（存储PDF诊断数据）

### 第三部分：配置表
- **DataMappingConfig** - 数据映射配置表（配置数据更新规则）
- **SqlExecutionConfig** - SQL执行配置表（存储可执行的SQL语句）
- **SqlExecutionLog** - SQL执行日志表（记录SQL执行历史）

### 第四部分：示例配置
- 自动插入示例数据映射配置

## 注意事项

1. **安全性**：脚本会自动检查表是否存在，不会重复创建或覆盖现有数据
2. **兼容性**：如果 FileInfo 表已存在但缺少 Processed 字段，脚本会自动添加
3. **幂等性**：可以多次执行，不会产生错误或重复数据
4. **索引**：所有表都会自动创建必要的索引以提高查询性能

## 执行结果

脚本执行后会显示：
- ✓ 表示表创建成功
- ○ 表示表已存在（跳过）
- → 表示对现有表进行了更新（如添加字段）

## 后续步骤

1. 检查执行结果，确认所有表都已创建
2. 根据需要修改 `DataMappingConfig` 表中的配置
3. 在 `SqlExecutionConfig` 表中添加需要执行的SQL配置
4. 运行程序开始处理文件

## 常见问题

**Q: 如果执行失败怎么办？**
A: 检查错误信息，通常是权限问题或数据库连接问题。确保数据库用户有创建表的权限。

**Q: 可以只执行部分脚本吗？**
A: 可以，但建议按顺序执行。如果某个表已存在，脚本会自动跳过。

**Q: 如何重置数据库？**
A: 删除所有表后重新执行脚本，或使用 DROP TABLE 语句删除表后再执行。

