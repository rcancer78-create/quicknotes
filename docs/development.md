# QuickNotes: разработка и приёмка

Локальная Windows-база текстовых заметок: захват выделения, теги, Markdown, поиск, история, защита паролем, вложения и опциональная S3-совместимая синхронизация.

Полный релиз 0.9 требует завершения критериев из [`ROADMAP.md`](../ROADMAP.md), включая реальную приёмку Object Storage и визуальную матрицу. Наличие исходников и CI не заменяет эти проверки.

Все команды выполняются из корня репозитория. [← README](../README.md)

## Канонические документы

Действующие тексты:

- [`VISION.md`](../VISION.md) — зачем продукт такой, а не другой
- [`ROADMAP.md`](../ROADMAP.md) — порядок работ и критерии
- [`ARCHITECTURE.md`](../ARCHITECTURE.md) — как устроено в коде
- [`INVARIANTS.md`](../INVARIANTS.md) — «должен» ↔ тест или `[UNPROTECTED]`
- [`docs/ux/workspace-product-spec.md`](ux/workspace-product-spec.md) — утверждённое ТЗ большого рабочего пространства и карточной доски
- [`docs/decisions/`](decisions/README.md) — ADR-001…ADR-005

Другие markdown в репозитории каноном не являются. Файлы вроде `ТЗ.md` / `TECHNICAL_OVERVIEW.md` / `ROADMAP_KNOWLEDGE_BASE.md` в этом дереве отсутствуют и не должны считаться действующими, если появятся без пересмотра списка выше.

## Безопасный запуск

Нужны Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download) и Windows-таргет пак (как в `QuickNotes.App.csproj`).

```powershell
dotnet restore QuickNotes.sln
dotnet format whitespace QuickNotes.sln --verify-no-changes --no-restore
dotnet build QuickNotes.sln -c Release --no-restore -warnaserror
dotnet test QuickNotes.sln -c Release --no-build --no-restore --nologo --blame-hang --blame-hang-timeout 2m
```

Форматирование: только пробелы и переводы строк в C# исходниках решения (`.editorconfig`: UTF-8, CRLF, отступ 4 пробела). Команда выше — тот же gate, что Windows CI после restore и до build. Не входят XAML, Markdown, JSON-фикстуры, бинарные ресурсы и `dotnet format style` / analyzers. Чтобы выровнять дерево: `dotnet format whitespace QuickNotes.sln --no-restore`.

Запуск UI (создаёт данные только в `%LocalAppData%\QuickNotes`, не в репозитории):

```powershell
dotnet run --project QuickNotes.App -c Release
```

Headless restore того же `QuickNotes.App.exe` (без WPF UI). Секрет только из stdin:

```powershell
# пароль архива
Get-Content -Raw password.txt | .\QuickNotes.App.exe --restore-archive C:\backup\notes.qnar --destination C:\QuickNotesRestore --password-stdin
# или recovery key; проверка без записи:
Get-Content -Raw recovery.txt | .\QuickNotes.App.exe --restore-archive C:\backup\notes.qnar --destination C:\QuickNotesRestore --recovery-key-stdin --dry-run
```

Коды выхода: `0` успех, `1` usage, `2` нет файла / не QNAR, `3` неверный секрет или порча, `4` валидация (в т.ч. непустой путь), `5` I/O, `6` прочее. Назначение — новый отсутствующий или пустой каталог, не живой `%LocalAppData%\QuickNotes`. Секрет со stdin не длиннее 4096 байт UTF-8 (завершающие CR/LF снимаются, пробелы сохраняются).

Локальный opt-in benchmark стоимости PBKDF2 — отдельный консольный `QuickNotes.Tools.exe` (dummy password/salt, без архивов, БД и секретов; процесс ждёт отчёт и код выхода). Не встроен в WPF `QuickNotes.App.exe`:

```powershell
# archive-only (историческая команда ADR-002)
& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe
# note + sync/blob + archive-reference (ROADMAP §10.2)
& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe kdf
# isolated Argon2id feasibility spike (ADR-016; not a production default)
& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe argon2id
```

Сравнение холодного старта двух isolated `win-x64` publish (без ReadyToRun и с `PublishReadyToRun=true`) не трогает `%LocalAppData%\QuickNotes` и не копирует publish в `bin`/`obj`:

```powershell
pwsh -NoProfile -File .\scripts\Measure-ColdStartReadyToRun.ps1
```

Прокрутка списка ≥ 50 fps (ROADMAP §6.5 / §6.7) остаётся **открытой**. Opt-in PresentMon harness не качает инструменты и не подменяет замер `CompositionTarget.Rendering`. Нужен уже установленный `PresentMon*.exe`. Во время записи **вручную прокрутите список заметок**; harness не эмулирует ввод. Живой `%LocalAppData%\QuickNotes` не используется как профиль:

```powershell
pwsh -NoProfile -File .\scripts\Measure-ScrollFpsPresentMon.ps1 -PresentMonPath C:\Path\To\PresentMon.exe -OperatorConfirmsScroll
```

Без установленного PresentMon runner завершается с явным сообщением (exit ≠ 0). PASS только после реального displayed-frame capture с подтверждённой ручной прокруткой.

Локальная приёмка продуктовых проверок M5: уникальный temp-профиль (не `%LocalAppData%\QuickNotes`), без облачных credentials. Живой профиль не используется как профиль и не пишется; runner читает только metadata snapshot (путь/размер/mtime) для детекта мутаций, не content hash. `dotnet test` идёт с `--no-restore --no-build` после локальной сборки (без обращения к NuGet в этом шаге):

```powershell
dotnet build QuickNotes.sln -c Release
pwsh -NoProfile -File .\scripts\Run-LocalAcceptancePack.ps1 -Mode Lightweight
pwsh -NoProfile -File .\scripts\Run-LocalAcceptancePack.ps1 -Mode Focused
```

Не направляйте тесты и отладку на основной пользовательский бакет, живые пароли, DPAPI-снимки чужого профиля и продакшен-базу. Секреты S3 — только тестовый бакет, когда дойдёте до M4.

Опциональный sanitized протокол живого облачного прогона (версии, host endpoint, fingerprint бакета, фильтр, cleanup limitations) не создаётся при обычном `scripts/Run-YandexCloudManualNoVpn.ps1`. Чтобы записать манифест, явно передайте каталог результатов и имя Markdown:

```powershell
pwsh -NoProfile -File .\scripts\Run-YandexCloudLiveSmoke.ps1 `
  -ResultsDirectory D:\tmp\qn-cloud-results `
  -SanitizedReportFileName cloud-smoke.md `
  -TrxLogFileName cloud-smoke.trx
```

Манифест не содержит ключей, путей credentials, сырого бакета, query/userinfo endpoint, дампов исключений или текста заметок. Он не заменяет живой GitHub run, две Windows VM и отдельный тестовый бакет.

Семантический поиск удалён (ADR-005, направление B). Поиск — FTS5 и структурные операторы.

## Статус относительно плана

| Этап | Состояние |
|---|---|
| M0 Git baseline | восстановление личного профиля из независимой копии требует отдельной проверки |
| M0 CI/сборка | workflow, `TreatWarningsAsErrors` и whitespace `dotnet format --verify-no-changes` в дереве; результат конкретного запуска см. в Actions |
| M1 тесты/миграции | закрыт локальными прогонами и тестами схемы |
| M2 документация | этот набор файлов |
| M3 recovery/export | открытый snapshot-экспорт есть; `.qnar` create+dry-run+restore/publish, headless `--restore-archive`, UI создания с обязательным RK dry-run, офлайн-копия RK и ротация recovery-wrap есть; shipping PBKDF2 default 2_490_000 (B4); isolated-profile process smoke есть; отдельная учётная запись Windows — нет ([ADR-002](decisions/002-export-archive-recovery.md)) |
