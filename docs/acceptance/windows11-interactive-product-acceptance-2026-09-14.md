# Windows 11 interactive product acceptance — 2026-09-14

## Scope

Host-driven keyboard acceptance in the clean VirtualBox VM `Windows 11`, under the local non-administrator test account. The deployed artifact was a self-contained `win-x64` Release build. The host live QuickNotes profile, cloud credentials, production bucket, and user notes were not used.

## Baseline

- Offline restore point: `Clean-Win11-Before-QuickNotes-Acceptance`.
- Normal first launch created `%LOCALAPPDATA%\QuickNotes\quicknotes.db` and kept the primary process running.
- Six isolated UI smoke families passed on the same guest and produced 84 screenshots: three-pane workspace, import migration, quick-note assembly, tasks index, reminders, and sync conflicts.

## Interactive global-hotkey results

The guest received real keyboard input while Windows Notepad was the foreground application. QuickNotes was reopened normally to inspect the resulting inbox.

| Scenario | Actual result | Evidence |
| --- | --- | --- |
| Selected ASCII text | One inbox note was created with the selected text and `Notepad` source context | `artifacts/vm-acceptance/interactive-hotkey-capture.png` |
| Unicode text | One inbox note preserved Cyrillic, Japanese characters, an em dash, and a check mark | `artifacts/vm-acceptance/interactive-hotkey-unicode-guestclipboard.png` |
| Empty clipboard | No empty note was created; QuickNotes stayed responsive and showed a user-facing notification asking to select text and retry | `artifacts/vm-acceptance/interactive-hotkey-empty-clipboard.png` |
| Immediate repeated hotkey | Two rapid hotkey sequences produced one durable note, no duplicate, no hang, and no visible corruption | `artifacts/vm-acceptance/interactive-hotkey-repeat.png` |

Result: **PASS** for the primary end-to-end Notepad capture path, Unicode preservation, empty-clipboard failure handling, and re-entrant hotkey protection.

## Boundaries still open

- Capture from a second unrelated application and a clipboard deliberately locked by another process.
- Full editor workflow: cancel, explicit save, crash recovery prompt, and a long note through the live window.
- Physical Windows scaling at 125%, 150%, and 200%; existing evidence for those scales is deterministic rendered UI smoke, not a physical-display claim.
- File-picker-driven open export and reading the produced Markdown/attachments in Explorer/Notepad.
- PresentMon/ETW scrolling capture, live cloud acceptance without VPN, and true two-device sync.

No open item above is represented as completed by this report.
