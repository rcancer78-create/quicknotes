# ADR-006: Адаптация проверенных greenfield-паттернов

- Статус: Accepted
- Дата: 2026-09-11
- Источник сравнения: `D:\work\QuickNotes_2_grok` (отдельная реализация без Git-истории)

## Контекст

Отдельная greenfield-реализация успешно собирается в Release без предупреждений и проходит 33 автоматических теста. Она полезна как компактный архитектурный и визуальный прототип, но не является совместимой заменой текущего приложения: её Draft journal хранит plaintext в SQLite, note protection удерживает пароль в managed `string`, а Sync не переносит теги, вложения и tombstone.

Текущий QuickNotes имеет более зрелые контракты защиты, миграций, recovery и Sync. Поэтому код из greenfield-проекта не переносится пакетно и не становится новой базовой архитектурой.

## Решение

### 1. Координация локальных мутаций

Перед реализацией `ROADMAP.md` §6.3 вводится явный coordinator для операций, которые меняют подтверждённое локальное состояние. Он должен:

- задавать один документированный порядок блокировок для note commit, revisions, attachment publish, import, migration/restore и Sync apply;
- не удерживать локальный lock во время сетевого ожидания;
- не превращаться в один бесконтекстный глобальный `SemaphoreSlim`;
- запрещать вложенные захваты, способные привести к deadlock;
- позволять deterministic failure/race tests и отмену до точки durable commit;
- обеспечивать enqueue Sync только после успешного локального commit.

**Статус реализации (§6.3, 2026-09-11):**
Реализован `LocalMutationCoordinator` (`ILocalMutationCoordinator`):
- Поключевая гранулярная синхронизация заметок по `noteId` (параллельные мутации разных заметок, строгая сериализация одной заметки).
- Эксклюзивная bulk-блокировка для `ExecuteSyncApplyAsync` (PULL) и `ExecuteBulkMutationAsync` (очистка корзины).
- Защита от дедлоков: обнаружение и запрет вложенных захватов (`AsyncLocal<bool>` вызывает `InvalidOperationException`).
- Синхронный и асинхронный контракты (`Monitor.Pulse/Wait` и `SemaphoreSlim`) без взаимоблокировок с WPF Dispatcher.
- Строгий порядок: `Coordinator Lock` -> `SQLite Transaction` -> `SQLite Commit` -> `Coordinator Unlock` -> `EnqueueLocalChange`. Сетевые вызовы S3/Yandex Cloud выполняются строго вне блокировок.
- Использование существующей схемы v12 (`SyncEntityStates` + `SyncLocalState`) как надёжного outbox (`HasDurablePendingWork`, `GetPendingNoteSyncIds`) без дублирующих таблиц.
- Честная компактная модель статуса публикации (`LocalCommitSyncStatus`: `SavedLocally`, `PendingUpload`, `Syncing`, `Synchronized`, `Conflict`, `Error`) с индикацией в шапке карточки `NoteCardViewModel`.
- Ограниченный по времени дренаж завершения работы (`DrainAndStopAsync`).
- Все 8 инвариантов проверены и подтверждены в `LocalCommitSyncBoundaryTests`. Полный тестовый набор проекта: 934 пройденных теста.

### 2. Структурный allowlist журналов

Диагностика переходит от свободного текста к разрешённым техническим полям: `event`, `level`, `correlationId`, технический `noteId`, `durationMs`, `errorCode`, `operationStage`.

- Тело/заголовок заметки, clipboard, пароль, ключ, recovery key, URL query и полный пользовательский путь запрещены.
- `Exception.Message` не записывается по умолчанию: тип исключения отображается как `errorCode`, а дополнительные данные проходят явное поле и sanitizer.
- Ротация и отказ самого logger не должны падать в пользовательскую операцию.
- Инвариант проверяется тестами с canary-секретами для каждого security-sensitive пути.

### 3. Изолированный portable UI-smoke

Создаётся воспроизводимый UI-smoke профиль, не использующий реальную базу, settings, clipboard, глобальный hotkey, Sync credentials или облако.

- Тестовый профиль располагается в уникальном каталоге одного запуска и удаляется только по проверенному абсолютному пути.
- UI управляется через Automation patterns; ввод не отправляется в неизвестное активное окно.
- Скриншот ограничен окном QuickNotes, а не рабочим столом.
- Минимальный маршрут: onboarding, empty state, create/edit/save, restart, search hit/miss, filters, editor modes, overflow, settings, light/dark.
- Отдельная матрица проверяет 100/125/150/200% DPI, узкую ширину, focus order и protected lock-state.
- Такой smoke остаётся focused UI evidence и не подменяет полную ручную продуктовую приёмку.

### 4. Визуальный референс рабочего пространства

Для `ROADMAP.md` §7.1 используется проверенный паттерн `Навигация | Список | Редактор`, но XAML не копируется буквально.

- Начальные ориентиры ширины: навигация 200–280 DIP, список 280–420 DIP, редактор занимает остаток.
- Редкие действия находятся в сгруппированном overflow; основная панель оставляет Save, favorite и режим просмотра.
- Карточка имеет стабильную высоту, 2–3 строки preview и компактные indicators.
- При ширине менее 900 DIP навигация становится flyout, а список/редактор — master/detail.
- Текущее отдельное компактное окно быстрого захвата сохраняется.

## Не принимается из greenfield-реализации

- SQLite Draft table с plaintext и восстановлением только существующих заметок.
- Хранение пароля защищённой заметки в managed `string`.
- Упрощённый Sync без тегов, вложений и передачи удалений.
- Regex-конвертация произвольного HTML в Markdown.
- Упрощённые Markdown renderer, snippet builder и task parser вместо уже более полных реализаций QuickNotes.
- Полная перестройка solution на слои без отдельного измеренного основания.

## Приёмка

Каждый из четырёх принятых паттернов реализуется отдельным ограниченным инкрементом. Перед commit требуются независимый diff review, Release build, focused regression tests и полный Release suite; UI-инкремент дополнительно требует просмотра полученных скриншотов.
