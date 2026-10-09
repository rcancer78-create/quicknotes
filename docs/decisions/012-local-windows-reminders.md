# ADR-012: безопасные локальные Windows-напоминания по Markdown-задачам

- Дата: 2026-09-12
- Статус: Accepted
- Реализовано в коде: да для Release 1.3 §9 пункты 4 и 6
- Пересмотр: если появится поддерживаемая COM-активация scheduled toast для unpackaged WPF или отдельная task-модель
- Связанные документы: [011-virtual-tasks-index.md](011-virtual-tasks-index.md), [ROADMAP.md](../../ROADMAP.md), [INVARIANTS.md](../../INVARIANTS.md)

## Контекст

ROADMAP §9 требует локальные Windows-уведомления для незакрытых checkbox с валидным `@YYYY-MM-DD` без SMTP, облака и сущности Task. Индекс уже не сканирует защищённые заметки (ADR-011). Unpackaged WPF на Windows 11 не даёт надёжной регистрации отложенных toast и COM-активации после выхода.

## Решение

- **Источник.** Только `TaskIndexService.QueryDatedOpenTasks` / `MarkdownTaskParser`. Таблицы Task нет.
- **Срок.** Due date наступает в **локальной timezone в 09:00**. Просроченная открытая задача уведомляется при следующем запуске или Refresh, один раз на версию.
- **Идентичность.** SHA-256 (`v1` + `Note.SyncId` N-формат + fingerprint + `yyyy-MM-dd`). Ledger `reminder-ledger.json` в профиле хранит только hex id, статус `delivered` и UTC. Текст задачи и заголовок не пишутся. Повреждённый файл = пустой ledger; содержимое не логируется.
- **Планировщик.** `TaskReminderScheduler`: Start / Refresh / Stop, отмена, задержка не больше 15 минут. Refresh после commit заметки (`MainViewModel.RefreshNotes`) и при старте. Не блокирует UI.
- **Доставка.** Абстракция `ILocalToastAdapter`. Тесты и `--isolated-profile` используют `RecordingToastAdapter` (без системного toast). Production: `WindowsToastAdapter` — `ToastNotificationManager` + AUMID `QuickNotes.Desktop` через `SetCurrentProcessExplicitAppUserModelID`. Сеть не нужна. Это **немедленный** toast в срок, пока процесс жив. Scheduled toast и доставка после закрытия приложения **не гарантируются**.
- **Клик.** Пока процесс жив, `ToastNotification.Activated` передаёт opaque `qn1.{SyncId:N}` и открывает исходную заметку (без plaintext в args). После выхода клик из Центра уведомлений может только запустить/показать окно без навигации — COM-активатор для unpackaged WPF не регистрируется.
- **Защита.** Защищённые заметки не входят в индекс, поэтому не попадают в toast/ledger. Fail closed. Нет настройки «разрешить plaintext». В лог ошибок scheduler пишет только имя типа исключения.
- **Настройки.** `AppSettings.LocalRemindersEnabled` по умолчанию **true**. Пользователь может выключить. Компактный статус в Настройках и в разделе «Задачи».
- **Не является решением.** SMTP/email (`[N]`), toast после закрытия приложения, индексирование защищённых заметок.

## Альтернативы

- WinRT scheduled toast + COM activator — отвергнуто как ненадёжное для unpackaged WPF без MSIX.
- Balloon tip трея — отвергнуто: нет opaque launch args и хуже Action Center.
- Хранить текст задачи в ledger — отвергнуто (конфиденциальность и D20/C15).

## Последствия

CLI: `--reminders-smoke` → `artifacts/reminders-acceptance/`. Ledger рядом с `settings.json`.

## Проверки

См. `INVARIANTS.md` D21, C16, W13.
