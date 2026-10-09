# QuickNotes Argon2id feasibility spike (ADR-016)

> **Status:** Isolated observation on this machine/runtime only. **Not** a production KDF. **Not** an accepted envelope default. PBKDF2 shipping N **unchanged** (120_000 / 100_000 / 2_490_000). `QuickNotes.App` does not reference Argon2.

## 1. Reproduction

```powershell
dotnet build QuickNotes.sln -c Release
& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe argon2id
```

Dummy ASCII password and a fixed 32-byte non-secret salt. Primitive: `Konscious.Security.Cryptography.Argon2id` 1.3.1, dkLen 32, saltLen 32. Warmups 1, repeats 3. No user database, DPAPI, live profile, or cloud.

Laboratory-only `--profile tiny` (m=8 KiB) is for tests; it is not an OWASP/RFC set.

## 2. Machine

Same host family as `docs/performance/kdf-cost-report-2026-09-13.md` (Windows 10.0.26200, .NET 8.0.23, 12 CPU, Intel Family 6 Model 151). Not a low-end SKU.

## 3. Canonical sample 2026-09-13T09:34:15Z

| Profile | m (KiB) | t | p | Median | Min–max | Linear 1+100 time (est.) |
|---|---|---|---|---|---|---|
| owasp-interactive-2023 | 19_456 | 2 | 1 | 92.827 ms | 91.389–98.713 | 9376 ms |
| owasp-high-memory-2023 | 47_104 | 1 | 1 | 51.476 ms | 48.608–122.652 | 5199 ms |
| rfc9106-second-recommended | 65_536 | 3 | 4 | 80.614 ms | 76.526–82.044 | 8142 ms |

Working-set deltas are process RSS noise after sequential profiles, not a precise per-derivation peak. Memory-hard cost is still **per envelope**.

PBKDF2 comparison on this host (existing report, not re-measured here): note 120_000 = 12.580 ms; sync 100_000 = 10.183 ms/envelope; 1+100 = 1028.483 ms; archive 2_490_000 = 255.841 ms.

## 4. Why this is not a shipping default

- Untrusted inbound budget is **20_000_000 PBKDF2 iterations**. It does not bound Argon2id `m`/`t`/`p`.
- Sync remains O(packages + attachments). OWASP interactive 1+100 is on the order of **~9.4 s** sequential KDF on this host, before AEAD.
- Mixed fleet: current readers reject `ARGON2ID` before derivation (intended). Writers must not emit it until a format ADR.
- Low-end hardware was not measured.
- Opening or syncing existing ciphertext must not rewrite it.

CLI strategy id: `adr-016-isolated-argon2id-spike-not-production-default`.
