# Real Windows uninstall checks / 真实卸载验证

Each product runs on a fresh GitHub-hosted Windows VM. The script checks that the app and its settings are absent, verifies the pinned installer's SHA-256, snapshots its Windows uninstall registration, and exercises the installed program. It runs the vendor uninstaller with unattended switches and waits for the registration, executable and application processes to disappear.

每个产品使用独立的 Windows 虚拟机。测试核对安装包哈希与安装前状态，让软件实际写入配置，再运行原厂静默卸载。两个扫描器读取同一份卸载前登记，并检查同一个卸载后文件系统和注册表。

| Product | Watched application artifacts |
| --- | --- |
| SumatraPDF 3.5.2 x64 | The PDF reader writes its initial `SumatraPDF-settings.txt` using an explicitly selected, initially empty application-data location. |
| WinMerge 2.16.60 x64, per-user | The comparison tool runs once with its registry preferences and once with an explicitly selected INI file under `%APPDATA%\WinMerge`. The test records the settings that actually appeared. |

The reports distinguish items the vendor removed, program-generated settings that survived, and a test marker added after removal. A similarly named sibling folder and an unrelated folder contain negative controls. A separate scenario supplies a peer registration whose uninstaller points into the tested install folder. The candidate also adds that registration *after* review and checks that cleanup skips the shared directory. After removing the test peer, it quarantines eligible targets and checks that restoring them reproduces file contents and registry data.

报告把原厂已删除的项目、程序写入并遗留的设置，以及卸载后人工放入的测试文件分别统计。共享目录场景使用模拟的第二条卸载登记。候选项经隔离与恢复后，再核对文件内容和注册表数据。这两款程序和这些已知位置只提供可复现的样本，不能据此推算全盘检出率、误删率或其他卸载工具的成绩。

Both engine reports are attached to each successful [workflow run](https://github.com/shuimowang/UninstallTrace/actions/workflows/real-apps.yml). The regular [verification suite](../verification/Program.cs) also exercises interrupted deletion, registry restore failures, and name collisions.
