# ADR-014: явные асинхронные границы

- Дата: 2026-09-12
- Статус: Accepted
- Реализовано в коде: да
- Пересмотр: если появится UI-поток, который снова ждёт сеть/OCR/архив синхронно, или потребуется контейнер задач с очередью

## Контекст

`ROADMAP.md` §10.4: UI и прикладной код смешивали `async void`, `.Wait()` / `.Result` / `GetAwaiter().GetResult()` и операции без внешнего тайм-аута. Это блокировало UI thread, скрывало сбои (unobserved task) и не отличало отмену, тайм-аут и ошибку для пользователя. Composition root (§10.3, ADR-013) уже явный; DI-контейнер не добавляется.

## Решение

- **уже в коде**
  - `async void` только в `AsyncEventBridge` (WPF/`ICommand`/hotkey/event). Мост логирует исключение, глотает отмену, восстанавливает безопасное UI-состояние.
  - Пользовательские переходы sync / backup / open export / import / encrypted archive / OCR / cloud test идут через `BoundedOperation` с теми же исходами: success, cancelled, timeout, failed. Ручной sync — `WaitForOwnedWork` и тот же lease: после timeout+grace UI не Success и не делает поздний local apply (`ReloadAll`/Ready) с orphaned cycle; `SyncScheduler` не Stop.
  - Мутации (`WaitForOwnedWork`): после дедлайна ждут конечное состояние не дольше `OwnedTerminalGrace`, затем изолируют orphan. Поздний результат никогда не Success. `BoundedOwnership` в `Cancelling` до терминального orphan, повторная мутация на том же lease — Overlap. `DrainCompleted`/`StateChanged` маршалируются на captured UI context и no-op после `SuppressCallbacks`. Закрытие Settings во время backup Running/Cancelling глушит поздние `_showMessage` и WPF-мутации. Ручной sync не применяет `UnresolvedConflictsCount` и прочее scheduler UI, пока lease held/ignoring; refresh после drain. OCR и read-only remote — `AbandonAndObserve`, поздний локальный apply структурно невозможен.
  - Encrypted archive `ICommand` restore не `Release` и не снимает busy, пока lease `Cancelling`; снять может только terminal drain.
  - Импорт (обычный и portable) проверяет cancellation сразу перед `Commit()`. Directory export: отмена до commit point не публикует; после — короткая атомарная секция swap. Portable ZIP удаляет plaintext `tempZip` при отмене/сбое.
  - URL probe: один in-flight UI Automation, coalescing и cooldown; поздний результат не перезаписывает более новый захват.
  - Сообщения пользователю — `UserFacingOperationError` (без сырого `Exception.Message`, кроме уже пользовательских `EncryptedArchiveException` / `SyncValidationException`).
  - Завершение процесса: один владелец `ApplicationCompositionRoot.TeardownThenDispose` — `LifecycleShutdown.WaitOwned` отменяет teardown и ждёт терминального состояния **до** `Dispose` графа. `.Wait` допустим только на teardown, не на интерактивном UI.
  - Фоновые циклы (`SyncScheduler`, `TaskReminderScheduler`) владеют `Task` и наблюдают исключения; fire-and-forget без владельца запрещён.
- **принятое направление** — не блокировать UI ожиданием OCR/Sync/архива; cancellation token проводить по реальной цепочке; для мутаций тайм-аут отменяет токен и **сохраняет ownership**, пока работа не дойдёт до безопасной точки (publish только если токен ещё жив); OCR — bounded await + observe orphan, поздний результат не открывает заметку.
- **эксперимент** — нет.

## Альтернативы

- Глобальный `SynchronizationContext` + `async void` без моста — отвергнуто: нет единого restore/log.
- `GetAwaiter().GetResult()` на UI «для простоты» — отвергнуто: deadlock и зависание.
- DI-hosted `IHostedService` для drain — отвергнуто, пока действует ADR-013.

## Последствия

Поведение данных, crypto, sync protocol и local-first не меняется. После тайм-аута мутация не публикует без живого ownership-токена и не мапится в Success. OCR после timeout может доиграть без UI (`ObserveOrphan`); late result игнорируется. Некооперативная мутация после grace остаётся в `Cancelling`, пока orphan не завершится.

Остаточные синхронные ожидания: isolated CLI smoke (`SyncConflictUiSmokeRunner`) синхронно гоняет VM. Захват URL браузера — `TryGetBrowserUrlAsync` с WaitAsync 250 ms, single-flight и observe orphan, без `.Wait` на UI thread.

## Проверки

`AsyncBoundaryTests`, `ScreenOcrTests` (timeout vs cancel), sync/backup/export timeout и source guards (`async void`, `GetAwaiter().GetResult()`). Pre-commit cancel import, ZIP temp cleanup, directory-export commit point, owned non-cooperative grace/overlap, URL single-flight.
