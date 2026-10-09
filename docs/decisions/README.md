# Архитектурные решения (ADR)

Канонические решения живут только здесь. Промпты агентов и каталог `artifacts/` решениями не являются.

## Шаблон

Копировать в `NNNN-short-title.md`.

```markdown
# ADR-NNN: заголовок

- Дата: ГГГГ-ММ-ДД
- Статус: Proposed | Accepted | Superseded | Rejected
- Реализовано в коде: да / частично / нет
- Пересмотр: дата или событие (например «до входа в Release 1.0»)

## Контекст

Зачем решение нужно сейчас. Ссылки на код, тесты и канонические документы:

- [VISION.md](../../VISION.md)
- [ROADMAP.md](../../ROADMAP.md)
- [ARCHITECTURE.md](../../ARCHITECTURE.md)
- [INVARIANTS.md](../../INVARIANTS.md)

## Решение

Что принимаем. Явно разделить:

- **уже в коде** — проверяемые факты;
- **принятое направление** — делать так, пока ADR не сменён;
- **эксперимент** — условия, без которых в продукт не входит.

## Альтернативы

Что отвергли и почему.

## Последствия

Совместимость данных, тесты, операционные ограничения.

## Проверки

Тесты, ручные протоколы или пометка, что проверки ещё нет.
```

## Индекс

| ADR | Тема | Статус |
|---|---|---|
| [ADR-001](001-markdown-product-boundary.md) | Границы продукта и Markdown как источник истины | Accepted; в коде для формата заметки |
| [ADR-002](002-export-archive-recovery.md) | Открытый экспорт, переносимый архив, recovery key | Accepted: B1/B2/B4 закрыты; `.qnar` create+dry-run+restore/publish, CLI `--restore-archive`, shipping PBKDF2 default 2_490_000; UI create + обязательный RK dry-run; измерение — `QuickNotes.Tools.exe`; ротация RK нет |
| [ADR-003](003-webview2-default-forbidden.md) | WebView2 по умолчанию запрещён | Accepted (`[N]` в ROADMAP); в коде WebView2 нет |
| [ADR-004](004-kdf-envelope-compatibility.md) | KDF и совместимость конвертов | Частично в коде (PBKDF2 note/sync/archive); archive shipping измерен (B4); note/sync измерены 2026-09-13; raise PBKDF2 не принят; inbound cycle KDF budget 20_000_000; Argon2id не production default (ADR-016 spike) |
| [ADR-005](005-semantic-search.md) | Семантический поиск: комплектовать или удалить | Accepted/Implemented: направление B, подсистема удалена; FTS5 — единственный production search path |
| [ADR-006](006-adapted-greenfield-patterns.md) | Адаптация проверенных greenfield-паттернов | Accepted; координатор мутаций, allowlist логов, UI-smoke и рабочее пространство |
| [ADR-008](008-html-markdown-migration-import.md) | Миграционный импорт HTML/Markdown | Accepted; конвертация в Markdown, транзакционный commit, без Word/OneNote parser |
| [ADR-009](009-quick-note-assembly.md) | Сборка быстрых заметок | Accepted; preview+атомарный commit, исходники по умолчанию сохраняются |
| [ADR-010](010-sync-conflict-resolution.md) | Честное разрешение Sync-конфликтов | Accepted; Keep Local/Remote/Both и ручной Markdown merge, без автозатирания |
| [ADR-011](011-virtual-tasks-index.md) | Виртуальный индекс открытых Markdown-задач | Accepted; parser/index/navigation по `Note.Text`, без сущности Task |
| [ADR-012](012-local-windows-reminders.md) | Локальные Windows-напоминания по задачам | Accepted; in-process scheduler + toast, ledger без plaintext |
| [ADR-013](013-explicit-composition-root.md) | Явный typed composition root без DI-контейнера | Accepted; `ApplicationCompositionRoot` + шесть bundles; контейнера нет |
| [ADR-014](014-explicit-async-boundaries.md) | Явные асинхронные границы | Accepted; `AsyncEventBridge` / `BoundedOperation` / `LifecycleShutdown`; без DI-контейнера |
| [ADR-015](015-kdf-envelope-versioning.md) | Версионный KDF descriptor в конвертах note/sync/blob | Accepted; единый `KdfDescriptor` + schema v14, legacy читается, миграция только на аутентифицированной записи; стоимость KDF не менялась, Argon2id не принят |
| [ADR-016](016-argon2id-evaluation.md) | Оценка Argon2id | Accepted как spike/оценка; **не** production default; raise PBKDF2 не принят; CLI `QuickNotes.Tools.exe argon2id` |
