# FTP Excel文件处理程序

这是一个C#控制台应用程序，用于从FTP服务器下载Excel文件，读取内容并保存到数据库，然后备份文件。

## 功能特性

- ✅ 从FTP服务器下载Excel和PDF文件（.xlsx, .xls, .pdf）
- ✅ 读取Excel文件内容（三坐标测量报告格式）
- ✅ 读取PDF文件内容（Renishaw球杆仪诊断报告格式）
- ✅ 将数据保存到SQL Server数据库
- ✅ 自动备份已处理的文件
- ✅ 自动清理旧备份文件
- ✅ 文件日志记录（使用Serilog）
- ✅ 数据库日志记录（SystemLog表）

## 环境要求

- .NET 8.0 或更高版本
- Windows/Linux/macOS

## 配置说明

编辑 `appsettings.json` 文件配置以下参数：

### FTP设置
```json
"FtpSettings": {
  "Server": "ftp.example.com",        // FTP服务器地址
  "Username": "your_username",        // FTP用户名
  "Password": "your_password",        // FTP密码
  "RemotePath": "/excel_files",       // FTP远程路径
  "Port": 21                          // FTP端口
}
```

### 数据库设置
```json
"ConnectionStrings": {
  "SQLServer": "Server=localhost;Database=ExcelDataDB;User Id=sa;Password=your_password;TrustServerCertificate=true;Encrypt=false;Connection Timeout=30;"
}
```

### 备份设置
```json
"BackupSettings": {
  "BackupPath": "Backup",             // 备份目录
  "KeepDays": 30                      // 保留天数
}
```

### 文件设置
```json
"FileSettings": {
  "LocalDownloadPath": "Downloads",   // 本地下载目录
  "FilePattern": "*.xlsx"             // 文件匹配模式
}
```

## 使用方法

1. **配置FTP和数据库信息**
   - 编辑 `appsettings.json` 文件，填入正确的FTP服务器信息和数据库连接字符串

2. **安装依赖**
   ```bash
   dotnet restore
   ```

3. **运行程序**
   ```bash
   dotnet run
   ```
   
   程序会自动执行以下流程：
   - 从FTP下载文件
   - 解析文件并存储到FileInfo和FileData表
   - 自动处理未处理的数据，更新到业务表（MeasurementData、DiagnosticData）

4. **编译发布**
   ```bash
   dotnet publish -c Release -o publish
   ```

## 数据库结构

程序会自动创建以下表结构：

### ExcelFileInfo表（文件信息表）

存储Excel文件的元数据信息：

| 字段名 | 类型 | 说明 |
|--------|------|------|
| Id | INT | 主键，自增ID |
| SourceFileName | NVARCHAR(500) | 源文件名（从FTP下载的Excel文件名） |
| PartNumber | NVARCHAR(200) | 零件号（从Excel表头提取） |
| PartName | NVARCHAR(500) | 零件名称（从Excel表头提取） |
| SerialNumber | NVARCHAR(200) | 序列号（从Excel表头提取） |
| ImportTime | DATETIME | 导入时间 |

**索引**：
- `idx_FileInfo_PartNumber` - 按零件号查询
- `idx_FileInfo_SerialNumber` - 按序列号查询
- `idx_FileInfo_ImportTime` - 按导入时间查询

### ExcelData表（数据表）

存储Excel文件中的测量数据：

| 字段名 | 类型 | 说明 |
|--------|------|------|
| Id | INT | 主键，自增ID |
| SourceFileName | NVARCHAR(500) | 源文件名 |
| RowNumber | INT | Excel文件中的行号 |
| ColumnName | NVARCHAR(200) | Excel列名（如：Char No., Reference Location, Results等） |
| ColumnValue | NVARCHAR(MAX) | 列值（单元格内容） |
| ImportTime | DATETIME | 导入时间 |

**约束和索引**：
- 唯一约束：`UK_ExcelData` - 确保同一文件的同一行同一列只能有一条记录
- `idx_SourceFileName` - 按文件名查询索引
- `idx_ImportTime` - 按导入时间查询索引

## 注意事项

1. 确保FTP服务器可以正常访问
2. 确保SQL Server数据库服务器可以正常访问
3. 确保有足够的磁盘空间用于下载和备份
4. 数据库表会在首次运行时自动创建
5. 已下载的文件会在处理完成后自动删除
6. 备份文件会保留指定天数，过期文件会自动清理

## 依赖包

- EPPlus: Excel文件处理
- Microsoft.Data.SqlClient: SQL Server数据库支持
- Microsoft.Extensions.Configuration: 配置管理
- Microsoft.Extensions.Logging: 日志记录

## 许可证

EPPlus在非商业用途下免费使用。

