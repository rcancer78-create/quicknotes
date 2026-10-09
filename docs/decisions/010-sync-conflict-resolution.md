# ADR-010: честное разрешение Sync-конфликтов

- Дата: 2026-09-12
- Статус: Accepted
- Реализовано в коде: да для Release 1.2 §8.3
- Пересмотр: при появлении совместного редактирования в реальном времени или смене cloud protocol
- Связанные документы: [VISION.md](../../VISION.md), [ROADMAP.md](../../ROADMAP.md), [001-markdown-product-boundary.md](001-markdown-product-boundary.md), [006-adapted-greenfield-patterns.md](006-adapted-greenfield-patterns.md)

## Контекст

ROADMAP §8.3 требует, чтобы конфликт Sync не затирался молча: пользователь видит обе версии и явно выбирает Keep Local, Keep Remote, Keep Both или ручное объединение Markdown.

## Решение

- **Уже в коде.** `SyncConflictService` выполняет каждое решение внутри `ILocalMutationCoordinator.ExecuteBulkMutationAsync` и одной SQLite-транзакции после stale/type/payload проверки. Сбой фазы (`validation` / `entity` / `history` / `sync_state` / `conflict_row` / `before_commit`) откатывает конфликт и обе версии. Keep Both создаёт заметку с детерминированным `SyncId` (`SyncConflictIdentity`) и суффиксом заголовка ` (облако)`. Повтор той же копии закрывает конфликт в той же транзакции, только если `SyncEntityState.RevisionId` совпадает с `KeepBothCopy` и снимок совместим с remote payload; совпадения `SyncId` недостаточно, stale local revision отклоняется даже при существующей копии. Keep Local/Remote/Merge пишут новую `SyncEntityState` ревизию; незащищённый Merge пишет `NoteRevision` и FTS через триггеры. Sync enqueue — только после commit, из `MainViewModel.OpenSyncConflicts`.
- **Принятое направление.** Ручное объединение меняет только Markdown; заголовок и теги — явный выбор Local или Remote, без union. Вложения не смешиваются (отдельные конфликты сущностей). Tombstone-vs-edit: Keep Both недоступен для удалённого удаления; Merge оставляет заметку живой. Защищённые версии в UI без plaintext/preview; Merge недоступен, Keep Both копирует конверт и `ProtectedOriginalSyncId`.
- **Не является решением.** Автовыбор стороны, WebView2-diff, смена cloud schema, показ ciphertext.

## Альтернативы

- Автоматический last-write-wins — отвергнуто ROADMAP как тихая потеря версии.
- Union тегов при Merge — отвергнуто как молчаливое смешивание.
- Timestamp-суффикс в теле Keep Both — отвергнут: недетерминирован и портит защищённый конверт.

## Последствия

Новой схемы БД нет. UI: `SyncConflictsWindow` / `SyncConflictsViewModel` (detail-load по generation/CancellationToken; устаревший ответ не применяется к другому конфликту). CLI: `--sync-conflict-smoke` → `artifacts/sync-conflict-acceptance/`.

## Проверки

См. `INVARIANTS.md` D19, C14, W11.
