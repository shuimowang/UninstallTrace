# Real Windows uninstall checks / 真实卸载验证

Each product runs on a fresh GitHub-hosted Windows VM. The script checks that the app and its settings are absent, verifies the pinned installer's SHA-256, snapshots its Windows uninstall registration, and exercises the installed program. It runs the vendor uninstaller with unattended switches and waits for the registration, executable and application processes to disappear.

每个产品使用独立的 Windows 虚拟机。测试核对安装包哈希与安装前状态，让软件实际写入配置，再运行原厂静默卸载。两个扫描器读取同一份卸载前登记，并检查同一个卸载后文件系统和注册表。

| Product | Watched application artifacts |
| --- | --- |
| SumatraPDF 3.5.2 x64 | The PDF reader writes its initial `SumatraPDF-settings.txt` using an explicitly selected, initially empty application-data location. |
| WinMerge 2.16.60 x64, per-user | The comparison tool runs once with its registry preferences and once with an explicitly selected INI file under `%APPDATA%\WinMerge`. The test records the settings that actually appeared. |
| Ghost Downloader 4.5.11 x64 | The Chinese installer registers a custom folder outside the standard application roots. The test watches the program's real startup log and separately tracks controlled installation, per-user data and startup-value samples. |

The reports distinguish items the vendor removed, program-generated settings that survived, and a test marker added after removal. A similarly named sibling folder and an unrelated folder contain negative controls. A separate scenario supplies a peer registration whose uninstaller points into the tested install folder. The candidate also adds that registration *after* review and checks that cleanup skips the shared directory. After removing the test peer, it quarantines eligible targets and checks that restoring them reproduces file contents and registry data.

报告把原厂已删除的项目、程序写入并遗留的设置，以及人工设置的测试项目分别统计。共享目录场景使用模拟的第二条卸载登记。候选项经隔离与恢复后，再核对文件内容和注册表数据。样本只涵盖受监测的位置，不能据此推算全盘检出率、误删率或其他卸载工具的成绩。

Both engine reports are attached to each successful [workflow run](https://github.com/shuimowang/UninstallTrace/actions/workflows/real-apps.yml). The regular [verification suite](../verification/Program.cs) also exercises interrupted deletion, registry restore failures, and name collisions.

## v0.1.4 results / 本次结果

| Installer | Artifacts watched before uninstall | Removed by vendor | Remaining settings found, v0.1.3 | Remaining settings found, v0.1.4 |
| --- | ---: | ---: | ---: | ---: |
| SumatraPDF | 1 | 0 | 1/1 | 1/1 |
| WinMerge | 2 | 1 | 0/1 | 1/1 |

Both versions found the artificial positive marker in each case and skipped both unrelated control paths per VM. When a simulated second registration pointed its uninstaller into the same install folder, v0.1.3 still offered the folder in both cases. v0.1.4 excluded both folders and preserved the peer file when it rechecked a selection made before the peer registered. With the peer removed, both quarantine-and-restore checks passed.

WinMerge 的原厂卸载移除了它的注册表设置，遗留了程序按测试指定位置写出的 INI。v0.1.4 把该文件所在的配置目录作为需手动勾选的候选项列出。SumatraPDF 的设置目录在两个版本中均能识别。两组隔离与恢复都已核对文件内容。[逐项报告与运行日志](https://github.com/shuimowang/UninstallTrace/actions/runs/44755748850)

## Ghost Downloader regression in v0.1.6 / 本次回归

The vendor installer registered `Ghost Downloader 版本 4.5.11`. Its uninstaller removed the launch log on the disposable runner. For a deterministic comparison we measured three controlled items: an install-folder file, a separate file under `%LOCALAPPDATA%\GhostDownloader`, and the quoted `Run\GhostDownloader` startup command. v0.1.5 identified 1 of 3. The new scanner identified all 3. Quarantine and restore preserved their data, two unrelated files, and another value in the same Run key.

虚拟机上原厂卸载删除了实际生成的日志。对另行设置的三个可重复测试项，v0.1.5 只发现安装目录，新版还识别出应用数据目录和具体的自启动值。隔离、原样恢复及三个对照项的检查均通过。[运行记录](https://github.com/shuimowang/UninstallTrace/actions/runs/44781077182)
