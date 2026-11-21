# UninstallTrace · 深度卸载

[English](README.md) · [下载 Windows 版](https://github.com/shuimowang/UninstallTrace/releases/latest) · [竞品分析](docs/competitive-analysis.md)

UninstallTrace 先运行软件自带的卸载程序，再检查遗留文件和注册表，逐项展示位置与归属依据。选中的项目会进入隔离区，可以从「清理恢复」还原，或永久删除副本来释放空间。

## 使用

1. 从发布页打开 `UninstallTrace.exe`。Windows x64 单文件版已包含 .NET 运行时。
2. 程序列表默认把有安装日期的程序按从新到旧排列，未知日期排在末尾。点击 **安装日期** 切换新旧顺序。按 **Ctrl+F** 搜索名称、发布者或版本，按 **F5** 刷新列表。
3. 选 **运行卸载** 启动原厂卸载，或选 **扫描残留** 检查有问题的卸载登记。程序已经从列表消失时，可以用 **扫描已移除程序**。
4. 核对每项的位置和依据，再勾选需要隔离的内容。要撤销一次残留清理，可从 **清理恢复** 还原该次隔离的文件和注册表项。要重新使用已卸载的软件，请用原安装包或商店重新安装。

点击 **大小** 表头可先看估算体积最大的程序。大小来自安装程序的登记，未登记的显示为「—」，按大小排序时排在末尾。拖动列表与详情之间的分隔线可以调整宽度。选中程序后，可以在详情中查看登记字节数，选中并复制完整版本号、路径或卸载命令。

如果文件被占用，**清理恢复** 会核对原路径和隔离位置。确认没有副本时，恢复和永久删除副本按钮会禁用。关闭占用文件的程序后，可以从该记录重新扫描。较早的记录使用 **查看卸载记录 → 继续扫描残留**，让你再次审查当前的候选项。

语言设置支持简体中文、英语和跟随 Windows。**批量卸载** 逐个运行原厂卸载程序，每个程序单独审查残留，结果需要确认时暂停。**卸载记录** 可以在稍后或重启后继续扫描。

桌面程序的日期来自卸载登记。部分安装程序省略此字段，MSI 的补丁或修复也可能更新它。商店应用使用 Windows 记录的包安装或上次更新日期。[Microsoft 关于 MSI 安装日期的说明](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)

## 清理范围

- 当前用户和本机的 Win32 卸载登记、32 位及 64 位程序，以及可核对产品编号的 MSI。
- 应用专用安装目录、常见应用数据目录、软件注册表项、App Paths、Run 和 RunOnce，以及可明确归属的快捷方式。
- 清理前重新核对归属，保护共享路径、Windows 和用户目录，并检查文件系统链接。
- 在残留审查中展开 **检查关联服务（只读）**，可以查看程序路径直接指向已核实独占安装目录的独立 Windows 服务。检查后，包含这些服务程序路径的候选项需要手动勾选。
- Windows 应用界面调用系统包管理器处理当前账号的商店应用。系统及受保护的包仅供查看，应用包和数据由 Windows 管理。

仅凭名称匹配的候选需要手动勾选。恢复范围限于 UninstallTrace 隔离的文件和注册表数据。原厂卸载程序、Windows 包管理器直接做出的改动由它们处理。遇到文件被占用或访问权限不足时，清理记录会列出待处理项目。服务登记目前只供核查。计划任务、驱动、其他用户的数据及需要安装期追踪的改动仍在当前清理范围之外。

## 构建和验证

在 Windows 上安装 .NET 10 SDK。集成验证使用临时文件和当前用户注册表中的模拟数据。

单独检查界面可运行 `dotnet run --project verification/DeepUninstaller.Verification.csproj -c Release -- --verify-ui`。启动项归属的本地测试使用内存样本；写入真实启动列表的卸载对照测试仅在一次性虚拟机中运行。

[真实卸载验证](.github/workflows/real-apps.yml) 分别在一次性 Windows 虚拟机上安装 Ghost Downloader、SumatraPDF 和 WinMerge。每组使用相同的原厂卸载结果比较新旧扫描器，并检查隔离、恢复和共享目录保护。[验证方法与结果](docs/uninstall-verification.md)

桌面残留扫描耗时和 C#、Rust 的 Windows API 对照结果见[性能实测](docs/scanner-performance.md)。

```powershell
dotnet build -c Release
dotnet run --project verification/DeepUninstaller.Verification.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release/win-x64
```

便携版尚未做代码签名。恢复数据与卸载记录保存在当前 Windows 用户配置文件中。项目使用 [MIT 许可证](LICENSE)。
