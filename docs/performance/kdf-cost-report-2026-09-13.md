# QuickNotes PBKDF2 cost measurement (ROADMAP §10.2)

> **Status:** Local measurement accepted as an observation on this machine/runtime only. **Not** a universal cost. **PBKDF2 raise is not accepted.** **Argon2id is not a production default.** Production iteration defaults were **not** changed. Isolated Argon2id spike: ADR-016 / `QuickNotes.Tools.exe argon2id` / `docs/performance/argon2id-feasibility-2026-09-13.md`.

Correction 2026-09-13 (security review): a two-KDF assumption must not produce a sync default; 150 ms is not derived from search p95; experimental points are not candidates.

## 1. Reproduction

```powershell
dotnet build QuickNotes.sln -c Release
& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe kdf
```

Dummy ASCII password and a fixed 32-byte non-secret salt. Primitive: `Rfc2898DeriveBytes.Pbkdf2`, SHA-256, dkLen 32. Warmups 3, repeats 5, probe 50_000. No user database, DPAPI, live profile, or cloud.

Archive-only historical command (unchanged): `QuickNotes.Tools.exe` with no args.

## 2. Machine

| Property | Value |
|---|---|
| OS | Microsoft Windows 10.0.26200 |
| Runtime | .NET 8.0.23 |
| Processor count | 12 |
| Processor | Intel64 Family 6 Model 151 Stepping 2, GenuineIntel |
| Same host family as | ADR-002 B4 (2026-09-10) and `docs/performance/baseline-report-2026-09-11.md` |

This is **one** desktop host. It is not a low-end SKU. A global default needs additional low-end measurements.

## 3. Measured current production N

Canonical sample **2026-09-13T05:06:03Z** (probe 50_000 → 5.175 ms):

| Circuit | Production N (unchanged) | Median | Min | Max |
|---|---|---|---|---|
| Note | 120_000 | 12.580 ms | 12.432 ms | 13.110 ms |
| Sync / blob (one envelope) | 100_000 | 10.183 ms | 10.090 ms | 10.259 ms |
| Archive | 2_490_000 | 255.841 ms | 253.264 ms | 261.062 ms |

Repeat **2026-09-13T07:38:18Z** (probe 50_000 → 5.077 ms; same constants, run noise only): note 11.763 ms, sync 10.067 ms, archive 244.271 ms.

Archive shipping N remains in the ADR-002 150–400 ms band on this host. Note/sync current N are a single derivation, not a product-path target.

## 4. Sync cost model (not two KDF)

Each QNSP package and each QNBA blob has its own salt and calls `DeriveKey`. Cycle cost is **O(packages + attachments)**, not a fixed two-derivation budget.

Linear scale from the canonical per-envelope median **10.183 ms** at production **100_000**:

| Packages | Attachments | Derivations | Estimated KDF median |
|---|---|---|---|
| 1 | 0 | 1 | 10.183 ms |
| 1 | 1 | 2 | 20.366 ms |
| 1 | 10 | 11 | 112.013 ms |
| 1 | 100 | 101 | 1028.483 ms |

07:38Z measured per-envelope 10.067 ms scales to 10.067 / 20.135 / 110.741 / 1016.807 ms for the same 1+0/1/10/100 grid.

A former **720_000** figure was a per-envelope ~73 ms sample (05:06Z confirm 73.239 ms, min 71.618, max 75.022). It is **not** a sync candidate: it came from incorrectly dividing a wall-clock target by two KDF calls.

## 5. Experimental timing points (not defaults)

Single-derivation wall-clock probes on this host only. **Not** taken from search p95 (ROADMAP §6.5 is a different product path). **Not** accepted as a global note/sync default. Low-end hardware was not measured.

| Run | Circuit | Approx. wall clock | N | Median | Min–max |
|---|---|---|---|---|---|
| 07:38Z | Note | ~50 ms | 490_000 | 48.490 ms | 47.335–49.000 |
| 07:38Z | Note | ~100 ms | 980_000 | 99.156 ms | 94.980–105.109 |
| 07:38Z | Note | ~150 ms | 1_470_000 | 144.391 ms | 141.960–145.582 |
| 05:06Z | Note | ~150 ms | 1_440_000 | 148.160 ms | 147.339–148.758 |
| 07:38Z | Sync (one envelope) | ~50 ms | 490_000 | 47.777 ms | 47.688–49.210 |
| 07:38Z | Sync (one envelope) | ~100 ms | 980_000 | 96.592 ms | 95.124–98.832 |
| 07:38Z | Sync (one envelope) | ~150 ms | 1_470_000 | 143.640 ms | 141.759–153.359 |
| 05:06Z | Sync (one envelope) | ~75 ms | 720_000 | 73.239 ms | 71.618–75.022 |

**1.44 million / 1.47 million** are experimental ~150 ms points on this CPU. They are not a note candidate and must not be shipped as a default without low-end data.

If a ~150 ms **per-envelope** N were applied to sync, 1 package + 100 attachments would be on the order of **~15 s** of sequential PBKDF2 on this host (linear). That is why there is no single sync candidate.

## 6. Cloud envelope DoS (per-envelope max unchanged)

Untrusted QNSP/QNBA readers accept up to **5_000_000** iterations **per envelope** (`KdfDescriptorLimits.CloudEnvelope`) before `Pbkdf2`. Cycle cost remains `O(packages + attachments)`.

A **per-sync-cycle** inbound budget of **20_000_000** claimed PBKDF2 iterations (`UntrustedInboundKdfWorkBudget`) is reserved before each inbound derivation in `RunSyncCycleAsync` pull. The documented normal 1 package + 100 attachments at production 100_000 is 10_100_000 and fits with ~2× headroom. Local encrypt/push is not charged. Exhaustion is a distinct fail-closed result (not offline/auth) and does not network-retry. Production defaults and the per-envelope cap were **not** changed.

**PBKDF2 raise / Argon2id production default:** not accepted. The cycle budget is a cumulative PBKDF2 CPU guard, not a cost change and not an Argon2id model.

## 7. Recommendation (conservative)

- Measurement of **current** production N is accepted as a local observation.
- **PBKDF2 raise: not accepted.** Experimental 490_000 (~50 ms) is not a global default. 1 package + 100 attachments at 490_000 would claim 49_490_000 iterations vs the 20_000_000 inbound cycle budget. Low-end hardware was not measured.
- **Argon2id production default: not accepted.** Memory-hard Argon2id is evaluated in ADR-016 as an isolated Tools spike, **not** claimed as the only future path and **not** shipped in `QuickNotes.App`.
- No unified note/sync iteration count. Archive shipping **2_490_000** stays (ADR-002 B4).
- Production constants stay **120_000 / 100_000 / 2_490_000**.
- Untrusted inbound cycle KDF budget is **20_000_000** claimed PBKDF2 iterations (not an N raise; not an Argon2id budget).

CLI strategy id: `measurement-accepted-no-kdf-change`.
Argon2id spike strategy id: `adr-016-isolated-argon2id-spike-not-production-default`.
