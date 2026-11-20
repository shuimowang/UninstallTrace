# UninstallTrace · 深度卸载

[English](README.md) · [下载 Windows 版](https://github.com/shuimowang/UninstallTrace/releases/latest) · [竞品分析](docs/competitive-analysis.md)

UninstallTrace 先运行软件自带的卸载程序，再检查遗留文件和注册表，逐项展示位置与归属依据。选中的项目会进入隔离区，可以从恢复历史还原，或永久删除副本来释放空间。

## 使用

1. 从发布页打开 `UninstallTrace.exe`。Windows x64 单文件版已包含 .NET 运行时。
2. 按名称、发布者或版本搜索程序。按 **Ctrl+F** 聚焦搜索框，按 **F5** 刷新列表。
3. 选 **运行卸载** 启动原厂卸载，或选 **扫描残留** 检查有问题的卸载登记。程序已经从列表消失时，可以用 **扫描已移除程序**。
4. 核对每项的位置和依据，再勾选需要隔离的内容。只要恢复副本仍在，就可以从 **恢复历史** 还原。

语言设置支持简体中文、英语和跟随 Windows。**批量卸载** 逐个运行原厂卸载程序，每个程序单独审查残留，结果需要确认时暂停。**卸载记录** 可以在稍后或重启后继续扫描。

## 清理范围

- 当前用户和本机的 Win32 卸载登记、32 位及 64 位程序，以及可核对产品编号的 MSI。
- 应用专用安装目录、常见应用数据目录、软件注册表项、App Paths、Run 和 RunOnce，以及可明确归属的快捷方式。
- 清理前重新核对归属，保护共享路径、Windows 和用户目录，并检查文件系统链接。
- Windows 应用界面调用系统包管理器处理当前账号的商店应用。系统及受保护的包仅供查看，应用包和数据由 Windows 管理。

仅凭名称匹配的候选需要手动勾选。恢复历史覆盖 UninstallTrace 隔离的文件和注册表数据。原厂卸载程序、Windows 包管理器直接做出的改动由它们处理。遇到文件被占用或访问权限不足时，历史记录会列出待处理项目。当前清理范围主要覆盖桌面应用的常见残留，Windows 服务、计划任务、驱动、其他用户的数据及需要安装期追踪的改动仍待完善。

## 构建和验证

在 Windows 上安装 .NET 10 SDK。集成验证使用临时文件和当前用户注册表中的模拟数据。

```powershell
dotnet build -c Release
dotnet run --project verification/DeepUninstaller.Verification.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release/win-x64
```

便携版尚未做代码签名。恢复数据与卸载记录保存在当前 Windows 用户配置文件中。项目使用 [MIT 许可证](LICENSE)。
