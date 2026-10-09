# QuickNotes publish-time ReadyToRun cold-start comparison (ROADMAP §6.5)

> **Date (UTC):** 2026-09-13 09:03:49Z
> **Schedule:** ABBA, warmup 2 + measured 10 launches per variant
> **Readiness:** `QN_PERF_STARTUP_READY_MS` after `SearchBox.Focus` at `DispatcherPriority.Input`
> **Live profile:** SHA-256 fingerprint unchanged (`CE3E6FD95480F1EFD20B398354A3B04474EA1A85A5446E722A236525D4D2918D`)

This is a same-run comparison of two framework-dependent `win-x64` publishes of the same source. The 2026-09-11 canonical report is historical context only. `CompositionTarget.Rendering` was not used.

Publish files were written only to unique `artifacts/publish/cold-start/<run-id>/{baseline,r2r}` (or temp) directories. They were **not** copied into `QuickNotes.App/bin` or `obj`.

## 1. Reproduction

```powershell
pwsh -NoProfile -File .\scripts\Measure-ColdStartReadyToRun.ps1
```

| Step | Command |
|---|---|
| Baseline publish | `dotnet publish "D:\work\QuickNotes\QuickNotes.App\QuickNotes.App.csproj" -c Release -r win-x64 --self-contained false -o "D:\work\QuickNotes\artifacts\publish\cold-start\20260913T090200Z_1792b6e8\baseline" --nologo` |
| ReadyToRun publish | `dotnet publish "D:\work\QuickNotes\QuickNotes.App\QuickNotes.App.csproj" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o "D:\work\QuickNotes\artifacts\publish\cold-start\20260913T090200Z_1792b6e8\r2r" --nologo` |
| Process timeout | 60000 ms |

## 2. Environment

| Property | Value |
|---|---|
| OS | Microsoft Windows 10.0.26200 |
| Runtime | .NET 8.0.23 (X64) |
| Build | Release, RID win-x64, framework-dependent |
| CPU | 12th Gen Intel(R) Core(TM) i5-12400 (12 logical cores) |
| RAM | 31.8 GB |
| Storage | SSD/Fixed Disk / NTFS |

## 3. Dataset

Synthetic isolated profile (seed 1337): 10,000 notes, 48 tags, 5,001 revisions, SQLite 25.79 MB. `%LOCALAPPDATA%\QuickNotes` was not used as `--isolated-profile`.

Launches reuse one unique temp dataset profile (notes, FTS, attachments on disk). `PRAGMA integrity_check` runs after generation and before every process. Each process is `--isolated-profile` + `--perf-startup`. The temp dataset is deleted after the run. `%LOCALAPPDATA%\QuickNotes` is never the profile.

## 4. Publish sizes

| Variant | Files | Bytes |
|---|---|---|
| framework-dependent win-x64 (no ReadyToRun) | 28 | 39,892,023 |
| framework-dependent win-x64 PublishReadyToRun=true | 28 | 53,785,343 |

## 5. Cold start to input-ready vs p95 ≤ 1500 ms

| Variant | n | min | median | p95 | max | vs 1500 ms | vs same-run baseline p95 |
|---|---|---|---|---|---|---|---|
| framework-dependent win-x64 (no ReadyToRun) | 10 | 1717.0 | 1758.0 | 1768.3 | 1771.0 | FAIL | 0.0 ms |
| framework-dependent win-x64 PublishReadyToRun=true | 10 | 1337.0 | 1360.5 | 1385.7 | 1387.0 | PASS | -382.6 ms |

### 5.1. Measured samples (ms)

- **Baseline:** 1717.0, 1737.0, 1741.0, 1750.0, 1757.0, 1759.0, 1760.0, 1763.0, 1765.0, 1771.0
- **ReadyToRun:** 1337.0, 1346.0, 1348.0, 1351.0, 1352.0, 1369.0, 1370.0, 1381.0, 1384.0, 1387.0
- **Baseline warmup (excluded):** 51266.0, 1760.0
- **ReadyToRun warmup (excluded):** 2139.0, 1366.0

## 6. Gate

ReadyToRun measured p95 is ≤ 1.5 s on 10 samples. ROADMAP §6.5 cold-start is closed **only** for this framework-dependent `win-x64` ReadyToRun publish. Ordinary `dotnet build` output is unchanged and is not ReadyToRun.

Same-run non-R2R publish remains FAIL (p95 1768.3 ms). Historical in-bin 2026-09-11/13 figures are context only.

### 6.1. First-open vs measured cold start

The first warmup process (`db_initialize` 50176 ms, ready 51266 ms) rebuilt FTS when the published SQLite stack first opened the Tools-generated database. The second baseline warmup was 1760 ms. Measured samples are after that one-time initialize, matching the existing warmup + measured method. This first-open cost is **not** the §6.5 gate number.

A discarded earlier attempt copied only `quicknotes.db` without attachment files into empty launch profiles and produced ~50 s every launch (`db_initialize` dominated). That run is not used for the gate.

## 7. Isolation

| Check | Result |
|---|---|
| Live profile hash (runner, before/after whole script including publish) | `3DB88A2D55CF066AD6D1022156DA23BFF8FABBC1003A6868903B5ACCE548C78B` identical |
| Live profile hash (tool, before/after launches) | `CE3E6FD95480F1EFD20B398354A3B04474EA1A85A5446E722A236525D4D2918D` identical |
| `PRAGMA integrity_check` | ok on the synthetic DB after generation and before every process |
| Publish copy-back into `QuickNotes.App/bin` or `obj` | not done |

### 7.1. First warmup phases (baseline publish, ms cumulative)

| Phase | Cumulative ms |
|---|---|
| single_instance | 4 |
| db_integrity | 335 |
| db_initialize | 50176 |
| composition | 50269 |
| main_viewmodel | 50890 |
| window_shown | 51256 |
| searchbox_focused | 51266 |
