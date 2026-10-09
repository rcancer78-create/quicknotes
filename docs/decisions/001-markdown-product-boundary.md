# ADR-001: границы продукта и Markdown как единственный источник истины

- Дата: 2026-09-10
- Статус: Accepted
- Реализовано в коде: да для хранения, предпросмотра и миграционного импорта HTML→Markdown (ADR-008); HTML не хранится
- Пересмотр: при предложении второго формата документа или HTML как источника истины
- Связанные документы: [VISION.md](../../VISION.md), [ROADMAP.md](../../ROADMAP.md)

## Контекст

`VISION.md` и `ROADMAP.md` фиксируют продукт как локальную базу *текстового* знания с захватом выделения, а не замену OneNote в вёрстке и PDF.

В коде содержимое заметки — строка `Note.Text` (`QuickNotes.App/Models/Note.cs`). Предпросмотр строится из этой строки: `MarkdownPreviewService.BuildFlowDocument` (`QuickNotes.App/Services/MarkdownPreviewService.cs`), привязка в `NoteEditorViewModel.PreviewDocument`. Отдельной HTML-сущности документа нет.

## Решение

- **Уже в коде.** Источник истины содержимого — Markdown/plain text в `Note.Text`. Предпросмотр — WPF `FlowDocument`, не браузерный документ.
- **Принятое направление.** Богатство (списки, таблицы в разметке, checkbox, ссылки на вложения) наращивается внутри этого текста. HTML не становится основным форматом (`ROADMAP.md` §11, статус `[N]`).
- **Не является решением этого ADR.** Трёхпанельный «страничный» UI Release 1.1; он ещё в плане.

## Альтернативы

- HTML/WebView2 как документ — отвергнуто ADR-003 и `[N]` в ROADMAP.
- Два параллельных формата (MD + HTML) — отвергнуто: ломает FTS, историю, sync, шифрование, которые работают с плоским текстом.

## Последствия

Поиск (`SearchService` / FTS5), история (`NoteHistoryService`), защита (`NoteProtectionService`), sync (`SyncPackageExporter`) и экспорт читают `Note.Text` (или ciphertext вместо plaintext у защищённых). Второй формат потребовал бы миграционный ADR.

## Проверки

- `MarkdownPreviewServiceTests` — разбор и `FlowDocument`.
- `NoteEditorWindow_UsesDynamicBrushesForEditingAndPreview` — редактор и preview viewer в XAML.
- FTS и защита: `NoteProtectionTests.ProtectNote_EncryptsTextAndRemovesFromFts`, `SearchService_ExcludesProtectedNotesFromFts`.
