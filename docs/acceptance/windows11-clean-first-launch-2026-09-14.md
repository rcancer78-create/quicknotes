# Windows 11 clean first-launch acceptance — 2026-09-14

## Scope

Host-driven acceptance in the VirtualBox VM `Windows 11`, using the local non-administrator test account. No cloud tests, live QuickNotes host profile, production bucket, or user notes were used. Credentials are intentionally absent from this report.

## Stand

- Guest: Windows 11 `10.0.26200.9445`, x64.
- VM: 3 vCPU, 8 GiB RAM, NAT, VirtualBox Guest Additions 7.0.26.
- Restore point: offline snapshot `Clean-Win11-Before-QuickNotes-Acceptance` (`b229fbbb-f4dc-4b4b-b1e8-596aad6ae428`).
- Guest had no installed `dotnet` runtime; deployment was self-contained `win-x64` Release.
- Host source commit under retest: `28889ae` (`Fix clean Windows first launch`).

## Reproduction before the fix

With `%LOCALAPPDATA%\QuickNotes` absent, a normal launch of the prior self-contained build showed `SQLite Error 14: unable to open database file`, exited, and left the profile directory absent. Source inspection confirmed that the recovery probe ran before profile-directory creation.

## Verification after the fix

1. Restored the offline clean snapshot.
2. Confirmed `%LOCALAPPDATA%\QuickNotes` was absent.
3. Published and copied a new self-contained Release build to a guest-only test directory.
4. Started `QuickNotes.App.exe` without `--isolated-profile` or a pre-created production profile directory.
5. Confirmed the process remained running and `%LOCALAPPDATA%\QuickNotes\quicknotes.db` was created with size 245,760 bytes.
6. A second launch exited as the secondary instance while the primary remained owned by the Guest Control session, consistent with the single-instance contract.

Result: **PASS** for clean production-profile directory creation before recovery/SQLite access. Guest Control does not provide a normal interactive window ownership model, so visual acceptance is recorded separately through isolated smoke output rather than claimed from this normal-launch step.

## Isolated UI smoke evidence

On the same clean Windows installation, the self-contained build returned success markers for:

- three-pane workspace;
- import migration;
- quick-note assembly;
- tasks index;
- reminders;
- sync conflict resolution.

The runs used separate guest profile directories and produced 84 PNG screenshots. They were copied to the ignored host evidence directory `artifacts/vm-acceptance/collected`; samples from light/dark and wide/narrow variants were visually inspected. No obvious clipping or missing content was found in the sampled three-pane, import mapping, destructive assembly, and conflict comparison screens.

## Boundaries still open

- Physical DPI/scaling and real keyboard/mouse interaction were not proven by Guest Control.
- Interactive global-hotkey capture from Notepad, including Unicode, empty clipboard, and rapid repeat, was subsequently proven in [`windows11-interactive-product-acceptance-2026-09-14.md`](windows11-interactive-product-acceptance-2026-09-14.md).
- PresentMon/ETW physical scrolling measurement remains open.
- Live Yandex Object Storage and two-device/VM sync acceptance were not run.
