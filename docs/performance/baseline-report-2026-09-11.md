# QuickNotes Baseline Performance Report (ROADMAP §6.5)

> **Date (UTC):** 2026-09-11 04:13:02Z  
> **Benchmark Duration:** 67,80 seconds  
> **Status:** Reproducible baseline on isolated synthetic dataset (Canonical Sample)  

## 1. Reproduction Command

```powershell
# Build Release solution
dotnet build -c Release

# Note: In an offline environment where the NuGet vulnerability audit endpoint cannot be reached,
# pass -p:NuGetAudit=false explicitly as an environment workaround:
# dotnet build -c Release -p:NuGetAudit=false

# Execute benchmark suite (synthetic 10,000 notes + 500 MB attachments)
.\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe benchmark
```

## 2. Test Environment

| Property | Value |
|---|---|
| **OS** | Microsoft Windows 10.0.26200 |
| **Runtime** | .NET 8.0.23 (X64) |
| **Build Configuration** | Release |
| **Processor** | 12th Gen Intel(R) Core(TM) i5-12400 (12 logical cores) |
| **RAM** | 31,8 GB |
| **Storage** | SSD/Fixed Disk (NTFS) |
| **Display / Theme** | 96 DPI / Light Theme |

## 3. Synthetic Dataset Profile

The benchmark operates on a strictly isolated portable profile generated deterministically from seed.
The live user profile (`%LOCALAPPDATA%\QuickNotes`) is never accessed or modified.

| Entity | Count / Size | Notes |
|---|---|---|
| **Seed** | `1337` | PRNG seed for byte-for-byte reproducibility |
| **Notes** | `10 000` | Titles, Markdown bodies, ~5% pinned, ~10% favorites, ~5% trash |
| **Tags** | `48` | 3-level tree (5 roots, 15 sub-tags, 25 sub-sub-tags) + synonyms |
| **Note-Tag Intersections** | Multi-branch | Single tags, double tags, triple tags, untagged |
| **History Revisions** | `5 001` | Historical revisions with snapshot tags |
| **Attachments** | `45` files (500,0 MB) | Binary and document payloads in `Attachments/` |
| **SQLite Database** | 25,60 MB | Schema v12, FTS5 indexed, WAL mode |
| **Generation Time** | 7,18 s | Canonical run (independent rerun: 12.80 s); excluded from product timings |

## 4. Benchmark Results vs ROADMAP §6.5 Targets

Canonical run results (with independent rerun verification noted below):

| Metric | ROADMAP Target | Measured Median | Measured p95 | Result | Notes |
|---|---|---|---|---|---|
| Cold process start to input-ready | p95 <= 1500 ms (1.5 s) | 1564,00 ms | 1603,20 ms | **FAIL** | Measured from process startup to MainWindow rendered and SearchBox focused. (Rerun p95: 1550.8 ms) |
| Search-as-you-type query completion (overall) | p95 <= 150 ms | 19,64 ms | 24,16 ms | **PASS** | Includes query parsing, FTS/tag search, Count, first-page fetch (50), and NoteCardViewModel instantiation. (Rerun p95: 24.92 ms) |
| Editor open to ready | p95 <= 300 ms | 36,13 ms | 43,98 ms | **PASS** | Instantiates NoteEditorViewModel, loads history/attachments/tags, and initializes NoteEditorWindow. (Rerun p95: 38.33 ms) |
| Idle working set | limit <= 400 MB | 197,24 MB | 201,12 MB | **PASS** | Physical working set after initial note card list materialization. (Rerun p95: 196.79 MB) |
| List scroll smoothness | >= 50 fps on reference dataset | N/A | N/A | **UNVERIFIED** | UNVERIFIED: CompositionTarget.Rendering callback deltas (154.5 FPS canonical, 183.6 FPS rerun) measure internal WPF rendering dispatch tick intervals rather than actual delivered display scanout / presentation frames or visual jank, and can exceed physical monitor refresh rate. Non-acceptance diagnostic only; not compared to 50-FPS target. |

### 4.1. Detailed Search Query Latencies

All searches measure end-to-end query completion: SQL/FTS query, Tag boolean evaluation, Count, page 1 (50 items) limit/offset fetch, and `NoteCardViewModel` materialization.

| Query Type | Query Expression | Samples | Median (ms) | p95 (ms) | Min (ms) | Max (ms) |
|---|---|---|---|---|---|---|
| Search: Title query | `QuarterlySynthesisReport` | 5 | 16,50 | 21,10 | 16,03 | 21,25 |
| Search: Body query | `deterministic_hyperthreading_checkpoint` | 5 | 20,41 | 21,01 | 17,40 | 21,04 |
| Search: Tag intersection | `tag:Backend AND tag:DotNet` | 5 | 22,78 | 24,98 | 21,67 | 25,20 |
| Search: No-hit query | `nonexistent_benchmark_token_xyz999` | 5 | 1,23 | 1,28 | 0,96 | 1,28 |

## 5. Raw Sample Observations

### 5.1. Canonical Sample (Run 1)
- **Cold Process Start (ms):** 1527,0, 1563,0, 1564,0, 1592,0, 1606,0
- **Idle Working Set (MB):** 193,6, 196,8, 197,2, 199,8, 201,5
- **Editor Open to Ready (ms):** 33,4, 34,2, 36,1, 36,8, 45,8
- **Search Overall (ms):** 1,0, 1,2, 1,2, 1,3, 1,3, 16,0, 16,2, 16,5, 17,4, 18,9, 20,4, 20,5, 20,9, 21,0, 21,3, 21,7, 22,2, 22,8, 24,1, 25,2
- **Non-acceptance Scroll Diagnostic (FPS):** 154.5 FPS (internal CompositionTarget.Rendering tick rate)

### 5.2. Independent Rerun Summary (Run 2)
- **Dataset Generation:** 12.80 s
- **Cold Process Start p95:** 1550.8 ms (**FAIL**)
- **Search Overall p95:** 24.92 ms (**PASS**)
- **Editor Open to Ready p95:** 38.33 ms (**PASS**)
- **Idle Working Set p95:** 196.79 MB (**PASS**)
- **Non-acceptance Scroll Diagnostic (FPS):** 183.6 FPS (internal CompositionTarget.Rendering tick rate)

## 6. Engineering Findings & Limitations

1. **Bounded Materialization:** Across all queries on 10,000 notes, first-page materialization strictly loaded at most 50 `NoteCardViewModel`s, confirming SQL-side pagination invariants (S9).
2. **Profile Isolation:** All database reads/writes, attachment file generation, and settings remained inside the disposable temporary directory. The live `%LOCALAPPDATA%\QuickNotes` profile remained bit-for-bit identical.
3. **Scroll Smoothness (FPS) - UNVERIFIED:** Marked **UNVERIFIED** (non-acceptance). Two executions reported 154.5 FPS and 183.6 FPS on the same ordinary display by dividing median `CompositionTarget.Rendering` callback deltas. Because `CompositionTarget.Rendering` measures internal WPF rendering dispatch tick intervals rather than actual delivered display scanout / presentation frames or visual jank, it can exceed the physical monitor refresh rate (60 Hz). Per ROADMAP §6.5 instructions, scroll FPS is marked UNVERIFIED, the ROADMAP checkbox remains unchecked, all P9/pass claims are removed, and the diagnostic is not compared to the 50-FPS target. Verification requires an ETW/PresentMon presentation trace or interactive validation on physical hardware.
4. **Canonical Sample and Independent Rerun Consistency:** Both the canonical run and the independent rerun confirm that search latency (~24–25 ms p95), editor opening (~38–44 ms p95), and idle working set (~197–201 MB p95) comfortably satisfy their respective ROADMAP targets. Both runs also demonstrate that cold startup (~1550–1603 ms p95) consistently fails the 1500 ms target.

## 7. Cold-start follow-up (2026-09-13)

Source-level startup work only. The 1.5 s p95 target is unchanged and remains **FAIL**. ReadyToRun/publish into `dotnet build` output was attempted and **reverted**: mixing RID publish native SQLite (`e_sqlite3`) into ordinary build/test output caused isolated-profile recovery dialogs. The preserved files under `D:\work\QuickNotes\.tmp\qn_perf_ds\quicknotes_corrupted_*.db` (including `quicknotes_corrupted_20260913_025802_7b255880a632428c9fd1ead85c55bfe7.db`) pass `PRAGMA integrity_check` with stock framework-dependent SQLite; the dialog was a false corrupt probe from the mixed native stack, not user-data loss. Live `%LOCALAPPDATA%\QuickNotes` was not modified.

### 7.1. Same-day before/after (isolated 10,000 notes + 500 MB attachments)

Same machine as §2. Reproduction remains `dotnet build -c Release` then `QuickNotes.Tools.exe benchmark` with `--isolated-profile` / `--perf-startup`. Ready-for-input is still MainWindow loaded, `SearchBox.Focus()`, `DispatcherPriority.Input`.

| Run | Cold samples (ms) | Median | p95 | vs 1500 ms |
|---|---|---|---|---|
| Before source-level opts (this machine, 2026-09-13) | 2148, 1842, 1951, 1791, 1820 | 1842.0 | 2108.6 | FAIL |
| After source-level opts, count-only FTS probe | 1779, 1768, 1761, 1755, 1850 | 1768.0 | 1835.8 | FAIL |
| After exact FTS `EXCEPT` probe (this follow-up) | 1830, 1720, 1730, 1736, 1705 | 1730.0 | 1811.2 | FAIL |

Other suite metrics after exact FTS probe: search overall p95 23.5 ms **PASS**; editor p95 43.6 ms **PASS**; idle working set p95 213.5 MB **PASS**; scroll **UNVERIFIED**.

Exact FTS probe cost on the 10,000-note DB (SQL only, 1 warmup + 5 samples): 89.2, 83.8, 85.4, 92.2, 83.5, 82.3 ms; median 83.8 ms. That work runs on every current-schema start and is required for correctness.

### 7.2. Measured startup phases (`--perf-startup`, after opts, sample #2)

| Phase | Cumulative ms | Notes |
|---|---|---|
| single_instance | 4 | Mutex |
| db_integrity | 154 | One `PRAGMA integrity_check` on the 25.8 MB file |
| db_initialize | 639 | Current-schema skip + exact FTS `EXCEPT` probe (~84 ms median on 10k notes) |
| composition | 733 | Typed composition graph |
| main_viewmodel | 1357 | First-page notes, tag tree, templates |
| window_shown | 1711 | WPF Show |
| searchbox_focused | 1720 | Input-ready (unchanged contract) |

Dominant remaining cost is JIT + WPF show + first queries, not a second integrity_check. Safe duplicate-work cuts recovered ~270 ms p95 versus the same-day before run; that is not enough to close 1.5 s. Closing the gate needs a *correct* deployment-time ReadyToRun/publish pipeline (not build-output mutation) or an ADR to change the number.

### 7.3. What changed in source

- Skip a second `PRAGMA integrity_check` only via `CanReusePriorIntegrityCheck` when the recovery probe just verified the same existing non-empty file as healthy. Missing/empty files and every recovery branch still run `Initialize`'s check.
- Skip `EnsureCreated` and `ApplySchemaUpgrades` on a current schema. Skip FTS rebuild only when triggers exist, FTS has unique NoteIds, and a two-way SQL `EXCEPT` proves every unprotected Notes row equals its FTS `(NoteId, coalesce(Title,''), Text)` row; any mismatch (wrong id, stale title/text, extra/missing, protected leak) does a full FTS DELETE+INSERT. Diagnostics are returned as `DbInitializeOutcome` per call.
- Isolated `--perf-startup` phase marks only; ready signal unchanged.

