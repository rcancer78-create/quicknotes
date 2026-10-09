# ADR-008: миграционный импорт HTML/Markdown без второго канонического формата

- Дата: 2026-09-11
- Статус: Accepted
- Реализовано в коде: да для Release 1.2 §8.1
- Пересмотр: при появлении запроса на прямой парсер Word/OneNote или хранение HTML
- Связанные документы: [VISION.md](../../VISION.md), [ROADMAP.md](../../ROADMAP.md), [001-markdown-product-boundary.md](001-markdown-product-boundary.md), [003-webview2-default-forbidden.md](003-webview2-default-forbidden.md), [docs/import-from-word-and-html.md](../import-from-word-and-html.md)

## Контекст

ROADMAP §8.1 требует перенос старых материалов: HTML и Markdown, Word только через промежуточный экспорт. ADR-001 и ADR-003 запрещают HTML как источник истины и WebView2.

## Решение

- **Уже в коде.** `HtmlToMarkdownConverter` — офлайн ограниченный токенизатор без сети, без JS и без браузерного движка. Результат пишется в `Note.Text` как Markdown. `NoteImportService` строит неизменяемый plan (`PlanHash` + `ImportSourceFingerprint`), показывает сопоставление блокнот→корневой тег / раздел→дочерний тег, потери и дубликаты, затем выполняет одну транзакцию со staging. Отпечаток источника = нормализованный относительный путь + SHA-256 содержимого; `LastWriteTime` только для повторной проверки свежести между preview и commit. Rollback удаляет только вложения с `WasCreated`. Replace для того же логического пути полностью согласует поля, теги, вложения, ревизию и FTS; при сбое откатывается весь граф.
- **Принятое направление.** Word/OneNote не парсятся и в диагностике предпросмотра указываются как потери с путём через фильтрованный HTML или Markdown. Замена существующей заметки разрешена только при том же логическом источнике и незащищённой заметке. Разные заметки с одним заголовком не сливаются.
- **Не является решением.** HTML-превью в WebView2; хранение исходного HTML рядом с Markdown.

## Альтернативы

- HtmlAgilityPack/AngleSharp — отвергнуто как лишняя поверхность и зависимость для bounded offline parser.
- Тихий flatten дерева папок — отвергнуто ROADMAP («не сглаживать неоднозначные структуры»).

## Последствия

Схема `DbInitializer.CurrentSchemaVersion` добавляет `ImportSourceFingerprint` / `ImportSourceRelativePath`. Тесты: `HtmlToMarkdownConverterTests`, `ImportFolderMappingTests`, `ImportMigrationDedupAndTransactionTests`, `ImportMigrationUiTests`, `ImportMigrationSmokeTests`.

## Проверки

См. `INVARIANTS.md` D16, I8, W9.
