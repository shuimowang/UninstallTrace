# Scanner performance / 扫描性能

## Desktop leftover scan

On one Windows x64 machine, both Release builds scanned the same 12 registrations sampled from 85 eligible desktop entries. The sample spans installation scopes, installer types and the presence of registered paths and icons. Each process warmed up on all 12 entries, then made 10 passes. Eight separate processes ran in balanced order (ABBA, BAAB), giving 480 timed scans per build. Inventory loading, warm-up, output serialization, optional service inspection and quarantine are outside the timings.

| Warm desktop scan | v0.1.4 | v0.1.5 |
| --- | ---: | ---: |
| Median | 27.20 ms | 15.78 ms |
| P95 | 56.29 ms | 33.74 ms |

The optimized scanner checks path ownership by string comparison before asking Windows about filesystem safety. Every potentially matching path still receives live checks. The two builds returned the same fingerprints for candidates, confidence, evidence and warnings throughout this sample.

同一批登记、每版 480 次预热后扫描，中位耗时下降约 42%。这是单机抽样，测量的是桌面残留扫描，包含启动、原厂卸载或隔离的整个流程有不同的耗时。

To repeat, build each engine against the same [benchmark harness](../verification/ScannerBenchmark.cs). Run the baseline, then pass its report into the other build to replay the same inventory and sample. The report contains sample ordinals, timings and hashes.

```powershell
dotnet $baselineVerificationDll --benchmark-scan 10 > baseline.json
dotnet $candidateVerificationDll --benchmark-scan baseline.json 10 > candidate.json
```

## C# and Rust on a Windows API call

A separate [hosted Windows experiment](https://github.com/shuimowang/UninstallTrace/actions/runs/44760296802) compared a Release C# program and optimized Rust program calling `GetFileAttributesW`. Both read the same 256 paths (directories, files and missing paths) eight times per timed batch. Each language produced 60 measured batches across four processes after warm-up. Input decoding, startup and output formatting were outside the timer. The attribute checksums matched in every batch.

| 2,048 path queries per batch | Median | P95 |
| --- | ---: | ---: |
| C# / .NET | 31.48 ms | 42.77 ms |
| Rust | 32.54 ms | 56.72 ms |

This probe measures the call pattern on one hosted machine. It gives no estimate for a complete Rust scanner, cold launch time, memory usage or installer size. The application continues to use C# and WPF.

两种语言调用相同 Windows 接口的中位数相近。本次实验证明现有扫描器可以通过减少无关查询提速，整机重写的耗时和收益仍需用功能等价的实现测量。
