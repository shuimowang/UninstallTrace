# Windows uninstaller comparison / Windows 卸载工具对比

Reviewed 2026-11-20 / 查阅日期 2026-11-20

## Published capabilities / 官方公布的能力

| Product | English | 简体中文 | Sources / 来源 |
|---|---|---|---|
| HiBit Uninstaller | Broad desktop toolkit with installation monitoring, batch and forced uninstall, Store apps, and browser extensions. Available as an installer or a portable download. | 覆盖安装监视、批量卸载、强制卸载、商店应用和浏览器扩展，提供安装版与便携版。 | [Official features / 官网](https://www.hibitsoft.ir/Uninstaller.html) |
| Bulk Crap Uninstaller | Open-source batch and automation reference. Uses original uninstallers, avoids batch collisions, detects orphaned or portable apps, and offers command-line workflows including dry-run. | 开源批量卸载工具。优先调用原厂卸载程序，处理批次冲突，识别孤立或便携应用，命令行支持预演。 | [Features / 官网](https://www.bcuninstaller.com/) · [Releases / 发布记录](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/releases) |
| Geek Uninstaller | Portable personal-use edition with instant filtering, leftover scanning, forced removal, and Store apps. Batch removal and real-time installation monitoring are listed under the paid Uninstall Tool edition. | 免费个人版主打便携、即时搜索、残留扫描、强制移除和商店应用。批量移除与实时安装监视列在付费 Uninstall Tool 下。 | [Overview / 官网](https://geekuninstaller.com/) · [Editions and releases / 版本与发布记录](https://geekuninstaller.com/download) |
| Revo Uninstaller Pro | Local installation traces, a vendor log database, forced removal by name or path, and selectable leftovers. Recovery includes restore points, the Recycle Bin and registry backups. | 提供本地安装追踪、厂商日志库、按名称或路径强制卸载和逐项审查残留。恢复方式包括系统还原点、回收站和注册表备份。 | [Product / 官网](https://www.revouninstaller.com/products/revo-uninstaller-pro/) · [Manual / 手册](https://www.revouninstaller.com/online-manual/uninstaller/) |

BCUninstaller's public GitHub repository displayed about 22.6k stars and 990 forks when reviewed. This measures engagement on GitHub. The available sources lack a common measure of active Windows users across all four products. [BCUninstaller repository](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)

查阅时，BCUninstaller 的 GitHub 仓库约有 2.26 万颗星和 990 个派生仓库。这反映 GitHub 社区关注度。公开资料缺少四款工具口径一致的 Windows 活跃用户数据。[BCUninstaller 仓库](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)

## Priorities / 功能优先级

1. **Improve attribution.** For recorded installations, retain timestamped evidence of created and modified files and registry keys. Show the source of each cleanup candidate and keep shared items out of default selections. **提高归属判断的准确性。** 为受监视的安装保留带时间的文件与注册表新建和修改记录，在预览中展示每项的来源，默认保留共享项。
2. **Use the Windows package lifecycle for Store apps.** Start with eligible packages for the current account. Show the exact package identity and protect frameworks and system packages. **接入 Windows 商店应用管理。** 优先支持当前账户可卸载的包，展示包的准确标识，并保护框架与系统包。[Windows package removal / Windows 包卸载接口](https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.removepackageasync)
3. **Make batch operations inspectable.** Preview each program and its uninstall command, run sequentially, preserve per-program outcomes, and recheck leftover ownership after each operation. **让批量卸载可预览。** 展示每个软件及其卸载命令，串行执行，逐项记录结果，每次操作后重新检查残留归属。
4. **Support precise recovery workflows.** Use a saved app identity or an exact app name and directory for a guarded scan of unregistered apps. Preserve review, quarantine and retryable restoration. **完善卸载失败后的处理。** 用保存的应用标识或精确应用名与目录，扫描登记已缺失的软件，沿用逐项确认、隔离及可重试恢复。

## C# and Rust / C# 与 Rust

Keep C# and WPF for the current Windows release. The desktop interface and uninstall/recovery workflow already have Windows verification coverage. C# can use Windows APIs through native interop, and WPF offers built-in controls for Windows desktop apps. .NET supports a self-contained single-file distribution. [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview) · [Win32 interop](https://learn.microsoft.com/en-us/windows/apps/develop/interop/call-win32-apis) · [.NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

当前 Windows 版继续使用 C# 和 WPF。桌面界面与卸载、恢复流程已有 Windows 验证覆盖。C# 可以通过互操作调用 Windows API，WPF 提供 Windows 桌面控件，.NET 支持自包含单文件发布。[WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview) · [Win32 互操作](https://learn.microsoft.com/en-us/windows/apps/develop/interop/call-win32-apis) · [.NET 单文件发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

Rust is a viable engine choice. Its ownership model offers memory safety without a garbage collector in safe Rust, and Microsoft's `windows-rs` provides Win32, COM and WinRT bindings. Native calls still require careful review, and the evidence rules and recovery guarantees must be tested in either implementation. A Rust desktop rewrite also needs a UI decision. Tauri uses WebView2 on Windows. [Ownership](https://doc.rust-lang.org/stable/book/ch04-00-understanding-ownership.html) · [Unsafe Rust](https://doc.rust-lang.org/book/ch20-01-unsafe-rust.html) · [windows-rs](https://github.com/microsoft/windows-rs) · [Tauri](https://v2.tauri.app/start/prerequisites/#webview2)

Rust 适合实现引擎。安全 Rust 通过所有权模型提供无需垃圾回收的内存安全，微软的 `windows-rs` 提供 Win32、COM 和 WinRT 绑定。原生调用仍需审查，两种实现都需要测试归属规则和恢复保证。如果以 Rust 重写桌面端，还要选择界面技术。Tauri 在 Windows 上使用 WebView2。[所有权](https://doc.rust-lang.org/stable/book/ch04-00-understanding-ownership.html) · [Unsafe Rust](https://doc.rust-lang.org/book/ch20-01-unsafe-rust.html) · [windows-rs](https://github.com/microsoft/windows-rs) · [Tauri](https://v2.tauri.app/start/prerequisites/#webview2)

Evaluate a small Rust helper when profiling identifies a specific problem. Compare equivalent builds on the same Windows machine and fixture, including download footprint, cold startup, peak memory, scan latency, and candidate and recovery correctness.

如果性能分析定位到具体瓶颈，再验证小型 Rust 辅助程序。在同一台 Windows 机器上用同样的测试数据和功能范围，比较下载体积、冷启动、峰值内存、扫描耗时及残留判断与恢复的正确性。
