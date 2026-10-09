# QuickNotes: канонический план развития

> Состояние плана: 10 сентября 2026 года.
> Основание: `VISION.md`, проверка исходников и актуальный целевой прогон тестов.
> Этот файл — единственный действующий план работ. Он описывает порядок, границы и проверяемые критерии. `VISION.md` объясняет продуктовый выбор и не дублирует задачи.

## 1. Цель

QuickNotes развивается как надёжная локальная база текстовых знаний с самым коротким путём:

> **поймал текст → автоматически организовал → быстро нашёл → не потерял.**

Продукт не пытается заменить OneNote в рисовании, свободной вёрстке, рукописном вводе или работе с PDF. Он должен сделать OneNote ненужным для текстового знания, сохранив быстрый захват выделения, контекст источника, теги, Markdown, поиск, историю, шифрование, вложения и контролируемую синхронизацию.

## 2. Как пользоваться этим планом

Статусы:

- `[ ]` — работа не начата;
- `[~]` — есть частичное подтверждение, но критерий выхода не закрыт;
- `[x]` — выполнено и подтверждено указанной проверкой;
- `[!]` — блокер следующего релиза;
- `[D]` — решение сознательно отложено;
- `[N]` — функция отклонена и не должна появляться без нового ADR.

Правила изменения плана:

1. Задача становится `[x]` только вместе с воспроизводимым доказательством: тестом, отчётом smoke, измерением или протоколом ручной приёмки.
2. Зелёный целевой набор тестов не называется полной приёмкой.
3. Сетевые и криптографические пути принимаются не только на подделках, но и на контролируемом реальном окружении.
4. Новый функциональный этап не начинается, пока не закрыт критерий выхода предыдущего релиза.
5. На релиз допускается не более одного крупного структурного рефакторинга. Он выполняется отдельным изменением с зелёными проверками до и после.
6. Пользовательские данные, основной облачный бакет и реальные секреты не используются для разрушительных тестов.
7. Любое изменение формата данных должно читать данные предыдущих версий и иметь проверенный путь восстановления.

## 3. Подтверждённый baseline

| Область | Состояние | Вывод |
|---|---|---|
| Исходники приложения | 48 764 физические строки, 230 файлов | Площадь уже велика; расширение только после стабилизации |
| Тесты | 30 187 строк, 701 тестовый метод | Нужны CI, стабильный полный прогон и классификация тестов |
| Парольная регрессия | 29/29 `NoteProtectionRegressionTests` проходят | Текущий целевой baseline зелёный, но ещё не защищён Git/CI |
| Полный набор тестов | На текущем состоянии этим планом не подтверждён | Блокер Release 0.9 |
| Git | Репозитория нет | Первый технический шаг |
| CI и общие правила сборки | Нет `.github`, `.editorconfig`, `Directory.Build.props` | Первый технический шаг |
| Схема SQLite | `DbInitializer.CurrentSchemaVersion` | Число не дублировать в нормативных документах |
| Production composition | `ApplicationCompositionRoot` + шесть typed bundles; Sync graph в `SyncCloudServices` (ADR-013) | Composition root явен; контейнера нет |
| `MainViewModel` | Public constructor: 6 typed bundles | Дальнейшее дробление ViewModel — отдельно от composition |
| Реальный S3 smoke | Подтверждения нет | Ключевая гипотеза синхронизации не принята |
| Визуальная приёмка | Полная матрица тем/DPI/узкой ширины/всех экранов не закрыта | Обязательный release gate |

Если baseline меняется, таблица обновляется только после нового воспроизводимого прогона. Исторические дефекты не выдаются за текущие.

## 4. Сквозные инварианты

Нормативные «должен / никогда» с привязкой к тестам живут в `INVARIANTS.md`. Ниже — краткий список тем; расхождения с кодом смотреть там, включая пометки `[UNPROTECTED]`.

### 4.1. Сохранность данных

- Ни одна заметка, версия, связь, тег или вложение не исчезает без явного действия пользователя.
- Ошибка до фиксации транзакции возвращает базу и файловое хранилище в исходное состояние.
- Ошибка очистки после успешного commit не превращает успешное сохранение в ложный отказ.
- Миграция, импорт, смена пароля, конфликт и синхронизация проверяются на байтовую сохранность вложений.
- Удаление использует корзину/tombstone и имеет проверенный путь восстановления в пределах заявленного срока.

### 4.2. Конфиденциальность

- Plaintext защищённой заметки не сохраняется в SQLite, журнале, диагностике, облачном пакете или незашифрованном экспорте.
- Ключи и секреты не попадают в исходники, логи, CI-артефакты и обычные настройки.
- Замена или завершение сессии очищает доступный приложению ключевой материал.
- Потеря индивидуального пароля заметки считается необратимой, пока отдельный ADR не изменит модель.
- Recovery key не раскрывает и не сбрасывает пароль заметки; archive recovery key, UI создания `.qnar` с обязательным контрольным dry-run, офлайн-копия и атомарная ротация recovery-wrap есть (ADR-002); отдельная учётная запись Windows как ручной критерий выхода M3 — нет (`INVARIANTS.md` C10, AR-S21).

### 4.3. Совместимость

- Новая версия читает поддерживаемые старые базы, архивы и облачные конверты.
- Версии формата и параметры KDF хранятся рядом с зашифрованными данными.
- Смена KDF не требует одновременного обновления всех устройств.
- Синхронизация повторного неизменённого состояния идемпотентна.

### 4.4. Главный пользовательский сценарий

- Глобальный hotkey и захват выделенного текста не становятся медленнее или сложнее.
- `Ctrl+C` и обычное поведение сторонних приложений не ломаются.
- Быстрое сохранение не ждёт сеть.
- Сбой OCR, семантического поиска или Sync не блокирует локальную заметку.

## 5. Release 0.9 «Честный» — доказать существующий продукт

Никакая новая продуктовая подсистема до выхода 0.9 не начинается.

### 5.1. M0 — безопасная точка отсчёта

#### Репозиторий

- [x] Инициализировать Git в корне проекта. Baseline: `e697c37`.
- [x] Добавить `.gitignore`: `bin/`, `obj/`, `TestResults/`, временные publish-каталоги, пользовательские настройки, локальные базы, ключи и секреты.
- [x] Проверить, что в первый commit не попадают БД, S3 credentials, DPAPI blobs, дампы, журналы и временные агентские файлы.
- [x] Зафиксировать первый commit текущего состояния до функциональных изменений.
- [ ] Создать приватный удалённый репозиторий и проверить восстановление проекта в пустой каталог.

Критерий выхода: текущий код воспроизводимо восстанавливается из приватного удалённого репозитория, а `git status` после сборки и тестов не показывает пользовательские или генерируемые данные.

#### CI и правила сборки

- [~] Добавить Windows CI для Release build и полного тестового набора с общим таймаутом. Workflow создан и локально проверен; первый запуск на GitHub ещё не выполнен.
- [~] Разделить отчёт на unit/integration/UI-sensitive категории, не скрывая общий результат. Class-level `TestCategory` (unit по умолчанию), LiveCloud с приоритетом, summary из одного TRX. Локально 13.09.2026: 1303 = 245 Unit + 838 Integration + 218 UiSensitive + 2 LiveCloud. GitHub run не выполнен.
- [~] Сохранять TRX только как CI-артефакт ограниченного срока, не в корне проекта. Путь `artifacts/ci/test-results`, `*.trx` в `.gitignore`, upload `if: always()` retention 7 дней; фактическая загрузка на GitHub не подтверждена.
- [x] Добавить `.editorconfig` и `Directory.Build.props`.
- [x] Включить анализаторы и `TreatWarningsAsErrors` после отдельного baseline-прогона: Release-сборка проходит с 0 предупреждений без массового форматирования.
- [x] Добавить `dotnet format --verify-no-changes` либо эквивалентную проверку только после фиксации единого форматирования. Gate 14.09.2026: `dotnet format whitespace QuickNotes.sln --verify-no-changes --no-restore` (после restore, до build) exit 0. Область — пробелы/переводы строк в C# исходниках решения по `.editorconfig` (UTF-8, CRLF, 4 пробела). Не входит: `dotnet format style` / analyzers (`EnforceCodeStyleInBuild` остаётся false), XAML, Markdown, JSON-фикстуры, бинарные ресурсы, миграционные артефакты. Полный `dotnet format` без `whitespace` не используется: style трогает порядок `using` (измерение 14.09.2026: 15 файлов), а не поведение.
- [ ] Красный CI блокирует merge и выпуск. Внешняя граница: branch protection/merge blocking нельзя утвердить без remote/GitHub settings.

Критерий выхода: новый commit автоматически собирается и тестируется на чистом Windows runner; повторный прогон даёт тот же итог.

### 5.2. M1 — стабильный полный тестовый контур

- [x] Целевой прогон `NoteProtectionRegressionTests`: 29/29, 10.09.2026.
- [x] Запустить полный `dotnet test` на текущем baseline с диагностическим timeout: независимая приёмка 791/791 за 37 секунд, 10.09.2026.
- [x] Повторное зависание OCR локализовано по hang dump: синхронные ожидания DWM/Dispatcher заменены ограниченными ожиданиями; добавлены регрессионные тесты для WPF Application без message pump.
- [x] Повторить полный прогон минимум три раза на чистом процессе без ручного вмешательства: два прогона Cursor 809/809 и независимый прогон 809/809 после исправления, 10.09.2026.
- [x] Проверить Release build с нулём ошибок: 0 предупреждений, 0 ошибок в независимой приёмке.
- [x] Добавить тест миграций от каждой поддерживаемой версии 0–11 до текущей на заполненной базе: заметки, теги, история, вложения, защищённые данные и sync metadata; 12/12 версий, 10.09.2026.
- [x] Добавить сценарий частично применённой миграции и повторного запуска: v9→v10 и v5→v6/v8, безопасный повторный запуск.
- [x] Добавить проверку восстановления backup после изменения схемы: backup v4, миграция v11, восстановление v4 и повторная миграция v11.
- [x] Чистый первый запуск Windows: каталог профиля создаётся до `IsDatabaseCorrupt` (`StartupProfilePreparation`). Находка приёмки на Windows 11 VM (`%LOCALAPPDATA%\QuickNotes` отсутствует → SQLite Error 14, каталог так и не создаётся); после исправления и отката к clean snapshot обычный self-contained запуск создал рабочую БД 245 760 байт и остался запущенным. Регрессия: `StartupProfilePreparationTests`; протокол: [`docs/acceptance/windows11-clean-first-launch-2026-09-14.md`](docs/acceptance/windows11-clean-first-launch-2026-09-14.md).

Критерий выхода: три последовательных полных зелёных прогона, зелёный Release build и воспроизводимая миграция/восстановление.

### 5.3. M2 — документация, которой можно верить

- [x] Создать `ARCHITECTURE.md`: факты и ссылки на типы/файлы, без дублирования `CurrentSchemaVersion` числом.
- [x] Создать `INVARIANTS.md`: каждый инвариант с тестом или `[UNPROTECTED]`.
- [x] Создать `docs/decisions/README.md` (шаблон ADR) и индекс.
- [x] ADR-001: Markdown как источник истины (`docs/decisions/001-markdown-product-boundary.md`).
- [x] ADR-002: направление M3; зашифрованный архив и recovery key явно не реализованы.
- [x] ADR-003: WebView2 запрещён; условия spike; в коде зависимости нет.
- [x] ADR-004: PBKDF2 в коде; Argon2id и измеренный upgrade не выдаются за внедрённые.
- [x] ADR-016: оценка Argon2id как изолированный spike; не выдаётся за production default.
- [x] ADR-005: направление B принято и реализовано — смысловой поиск удалён; FTS5 остаётся единственным production path.
- [x] `README.md`: назначение, безопасный запуск, ссылки на канон, дорелизный статус. 10.09.2026.

Критерий выхода: действуют только `README.md`, `VISION.md`, `ROADMAP.md`, `ARCHITECTURE.md`, `INVARIANTS.md` и ADR; утверждение «должен» имеет тест или честную отметку отсутствия защиты.

### 5.4. M3 — переносимость и аварийное восстановление

Сначала ADR-002 и security review, затем реализация. Create/dry-run/restore-on-disk, headless CLI `.qnar`, UI создания с обязательным контрольным восстановлением recovery key, офлайн-копия RK и атомарная ротация recovery-wrap есть; shipping PBKDF2 default для новых архивов измерен (B4). Автоматический isolated-profile process smoke (подмена `%LOCALAPPDATA%`/`%USERPROFILE%`, без живого профиля) есть. Восстановление под **отдельной учётной записью Windows** (другой SID/DPAPI) по-прежнему требует внешних прав и не закрыто.

#### Security review зашифрованного архива / recovery

- [x] 10.09.2026: security review этапа M3. B1/B2 Accepted (локальный payload; не-UI вход `QuickNotes.App.exe --restore-archive`). B4 Accepted (shipping default 2_490_000 после локального benchmark, цель ~250 мс; воспроизводимая команда — `QuickNotes.Tools.exe`). UI создания `.qnar`, обязательное контрольное восстановление RK, офлайн-копия и ротация recovery-wrap закрыты ниже; ручное восстановление под отдельной учётной записью Windows остаётся незакрытым как критерий выхода M3.

#### Открытый экспорт

- [x] Экспортировать только незашифрованные заметки, заголовки, теги, ссылки, метаданные источника и доступные вложения.
- [x] Пользователь явно выбирает каталог и подтверждает предупреждение о plaintext.
- [x] Писать через staging-каталог и атомарную замену завершённого snapshot.
- [x] Создавать manifest: версия формата, дата, количество сущностей, пропуски, ошибки и контрольные суммы.
- [x] Показывать в UI путь, время и полноту последнего успешного экспорта.
- [x] Ошибка экспорта не удаляет предыдущий успешный snapshot.
- [ ] Проверить восстановление без QuickNotes обычными средствами Windows. (автотесты подтверждают UTF-8 файлы и вложения на диске; ручная проверка Проводником/Блокнотом не выполнялась и не утверждается)

#### Переносимый зашифрованный архив

- [x] Определить версионируемый формат контейнера и его модель угроз — спецификация в ADR-002; в коде QNAR/QNAP create + dry-run + restore/publish + CLI (`EncryptedArchiveService`).
- [x] Включить всю базу и вложения без появления plaintext защищённых заметок на диске. (create копирует SQLite backup + файлы как на диске; decrypt protected нет)
- [x] Не зависеть от DPAPI текущего профиля для единственной копии ключа восстановления. (decrypt `.qnar` без DPAPI; ключ не пишется в settings)
- [x] Реализовать минимальную отдельную процедуру проверки/восстановления архива. (dry-run API + CLI `--restore-archive --dry-run`; запись restore в новый каталог)
- [x] Проверять целостность до изменения локальной базы. (dry-run: AEAD, manifest, пути, размеры, checksums; restore: то же, затем staging + `PRAGMA integrity_check` до publish)
- [x] Поддержать dry run и восстановление в новый каталог/новую базу. (новый пустой/отсутствующий путь; in-place живого профиля запрещён)

#### Recovery key

- [x] Генерировать случайный высокоэнтропийный ключ, отдельный от пользовательского пароля. (create: 32 байта CSPRNG + Crockford Base32; UI показывает ключ один раз после create)
- [x] Использовать его только для оборачивания ключа данных согласно ADR; не хранить открытым в облаке или настройках. (второй wrap-слот; persist в settings/DPAPI нет)
- [x] Предлагать печатную и файловую офлайн-копию с контрольной суммой. (`RecoveryKeyOfflineCopy`: тот же payload, SHA-256[0..4) в азбуке Crockford; явный confirm; без clipboard; новый файл атомарно, не в каталог `.qnar`; печать через `PrintDialog`)
- [x] Требовать контрольное восстановление до признания recovery-настройки завершённой. (UI: dry-run созданного `.qnar` только recovery wrap; last-success только после успеха)
- [x] Поддержать отзыв и выпуск нового recovery key с атомарной ротацией конверта. (`RotateRecovery`: только recovery-wrap, тот же header/password wrap/payload; dry-run нового ключа до `File.Replace`; сбой оставляет исходный файл byte-for-byte; UI во вкладке «Зашифрованный архив»)
- [x] Явно объяснить, что пароль отдельной заметки recovery key не заменяет.

Критерий выхода: открытый экспорт читается без приложения; полный архив восстанавливается в чистом профиле Windows; утрата текущего DPAPI-профиля не уничтожает тестовые данные.

Автоматическое доказательство без живого профиля: `EncryptedArchiveIsolatedProfileSmokeTests.ArS21_IsolatedProfileProcess_RestoresWithRecoveryKey_WithoutTouchingLiveProfileOrDpapi` (headless `--restore-archive` + recovery key, redirected `USERPROFILE`/`LOCALAPPDATA`, новый каталог, чтение тестовой заметки). Не закрыто: вход под **другой учётной записью Windows** (создание пользователя/права администратора/интерактивный logon) — внешние права, не симулируется.

### 5.5. M4 — реальная приёмка Yandex Object Storage

- [ ] Создать отдельный тестовый бакет с минимальными правами, lifecycle и лимитом расходов. (Автоприёмка ходит в пользовательский бакет только под уникальным префиксом `quicknotes-live-acceptance/<UTC-date>/<guid>/` с best-effort cleanup; отдельный бакет/IAM/lifecycle/лимит расходов не создавались.)
- [ ] Подготовить два независимых устройства либо две изолированные Windows VM/учётные записи. (Доказаны два изолированных временных профиля/БД в одном процессе; отдельные VM/учётные записи Windows не закрыты.)
- [ ] Не использовать основной пользовательский бакет и реальные данные. (Предоставленный пользователем бакет используется только под строгим уникальным префиксом запуска; подтверждения, что он отдельный тестовый, нет. `%LOCALAPPDATA%\QuickNotes` и объекты вне префикса не трогаются. Доказательство ограничений очистки: `YandexObjectStorageLiveAcceptanceTests`, `LiveCloudPrefixGuardTests`.)
- [ ] Выполнить сценарии: первый обмен, повтор без изменений, изменение на каждом устройстве, конфликт, Keep Local/Remote/Both, tombstone, вложение, защищённая заметка, офлайн/возврат сети, ETag race, нехватка квоты, повреждённый объект, смена sync-пароля и recovery. (Автоматически на реальном S3 через production `SyncEngine`: первый обмен с маркером и вложением, повтор без дубликатов, последовательные правки, детерминированный конфликт + KeepBoth с сохранением SyncId, tombstone без воскрешения, защищённая заметка. Детерминированная **offline**-матрица тех же failure/recovery сценариев — [`docs/sync-offline-failure-matrix.md`](docs/sync-offline-failure-matrix.md) (`SyncFailureMatrixTests` + существующие `SyncEngineTests` / `SyncConflictServiceTests` / `CloudPasswordRotationTests` / `SyncSchedulerTests`); это не заменяет live S3. Не закрыто на реальном бакете/двух VM: Keep Local как отдельный live-кейс, Keep Remote как отдельный live-кейс, офлайн/возврат сети, ETag race, квота, повреждённый объект, смена sync-пароля и recovery, ручные failure cases.)
- [~] Проверить отсутствие plaintext в ключах, metadata, телах объектов и диагностических логах. (Офлайн, 13.09.2026: source + fake `IAmazonS3` — ключи GUID/SHA-256, PUT без `x-amz-meta-*`, ContentType только json/octet-stream; `CloudErrorSanitizer` в `QuickNotes.App.Services` (не Sync) — селективная query-redaction, bounded regex, `ErrorLogService.Write(Exception)` deep-redact inner chain; timeout/offline inner cause сохраняется, логи/`ToString` без ключей. Тесты `S3RedactionAuditTests`, `S3ObjectStoreTransportTests`. Live через публичный API `HeadObject`/`GetObject`/`List`: маркер защищённой заметки отсутствует в object key, `ContentType`/`ETag` и телах. **Не закрыто live-only:** пользовательские S3 metadata headers `x-amz-meta-*` публичной моделью не отдаются — без SDK-обхода на реальном бакете не проверялись; диагностические логи стенда целиком не аудитились. Новый header API не вводился.)
- [~] Зафиксировать версии приложения, настройки стенда, шаги, ожидаемые/фактические результаты и очистку. (Офлайн 14.09.2026: общий слой `scripts/CloudRunProtocol.ps1`; live-раннеры пишут sanitized Markdown только при явных `-ResultsDirectory` + `-SanitizedReportFileName`; GitHub allowlist — два TRX, `cloud-smoke.md` / `cloud-acceptance.md`, `category-summary.md`. Протокол не утверждает, что живой прогон уже был. **Не закрыто:** фактические значения стенда и actual results живого прогона, первый GitHub run, две Windows VM, отдельный бакет/IAM/lifecycle/лимит расходов, доказательство live cleanup.)
- [~] Автоматизировать безопасную часть как nightly/manual pipeline; не запускать облачный smoke на каждый commit. (Manual/opt-in workflow `.github/workflows/cloud-acceptance.yml` с `workflow_dispatch`, `environment: cloud-acceptance`; сценарии через существующие `scripts/Run-YandexCloudLiveSmoke.ps1` / `Run-YandexCloudLiveAcceptance.ps1`; DPAPI `encrypt-credentials`; hang protection в раннере; allowlist двух TRX + двух sanitized Markdown + category summary, без recursive glob, `if: always()`, retention 7d; cleanup credentials best-effort `if: always()`. Офлайн-валидация: `scripts/Assert-CloudAcceptanceConfig.ps1` + `CloudAcceptanceWorkflowTests` + `CloudRunProtocolTests` + `EncryptCredentialsToolTests`. Nightly/cron не подключён — unattended secret gating не доказан. Первый запуск на GitHub не выполнен. Обычный `dotnet test` остаётся offline.)

Критерий выхода: двухустройственный обмен защищённой заметки с вложением проходит на реальном тестовом бакете, повтор без изменений идемпотентен, а отказ/конфликт не теряет данные. (Автоприёмка один раз полностью прошла на реальном бакете под изолированным префиксом; независимые повторы выявили нестабильность длинной сессии через текущий канал/VPN: `Offline` на immutable package PUT, затем `Timeout` на conditional pointer PUT. Базовый PUT/GET/DELETE smoke стабильно проходит. Отдельный тестовый бакет и полный M4-критерий остаются открытыми.)

### 5.6. M5 — ручная продуктовая приёмка 0.9

Синтетический offline-пакет (не закрывает эти чекбоксы): `scripts/Run-LocalAcceptancePack.ps1` / ROADMAP §6.6.

- [ ] Основное окно: обычная и узкая ширина, светлая/тёмная тема, 100/125/150/200% DPI.
- [~] Быстрый захват: разные приложения, Unicode, пустой буфер, недоступный буфер, повтор hotkey. (Windows 11 VM, 14.09.2026: реальный сквозной захват из Notepad сохранил ASCII и Unicode с кириллицей/японскими символами; пустой буфер дал понятное уведомление без пустой заметки; два быстрых повторных hotkey создали ровно одну заметку без зависания. Протокол: [`docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md`](docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md). Открыто: второе стороннее приложение и намеренно заблокированный буфер.)
- [ ] Редактор: создание, отмена, явное сохранение, аварийное закрытие, длинная заметка.
- [ ] Теги: дерево, синонимы, границы слов, ручные/автоматические/подавленные теги.
- [ ] Поиск: текст, теги, FTS fallback, защищённые заметки, пустой результат.
- [ ] Вложения, OCR, история, импорт/экспорт, корзина и восстановление.
- [ ] Sync UI: настройка, статус, конфликт, квота, очистка и ротация.
- [x] Проверить отсутствие деградации основного hotkey-сценария. (Windows 11 VM, 14.09.2026: сквозной `Ctrl+Alt+Space` из Notepad создал сохранённую inbox-заметку с исходным текстом и source context; Unicode/empty/repeat не зависли. Протокол: [`docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md`](docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md).)

### 5.7. M6 — UX-стабилизация по трём независимым ревью

Полная классификация, отвергнутые предложения и критерии пакетов: [`docs/ux/ux-review-consolidation-2026-09-15.md`](docs/ux/ux-review-consolidation-2026-09-15.md).

#### Пакет A — безопасность действий (блокирует Release 0.9)

- [~] Удалить голые `D1`–`D4` из `SyncConflictsWindow`; оставить `Alt+L/R/B/M` и проверить ввод цифр в ручном merge без выполнения команды. Автотесты: `SyncConflictsUiTests.DigitKeysInMergeEditor_DoNotResolveConflict`, XAML без `Key="D1"`–`D4`. Изолированный `--ux-package-ab-smoke` пишет кадры `merge-digits-*` в `artifacts/ux-package-ab-acceptance/` с видимыми цифрами 1–4 без resolve (`UxPackageAbSmokeTests`). Живой интерактивный прогон окна конфликтов не закрыт.
- [~] Устранить тихий commit/отбрасывание правки при смене заметки во встроенном редакторе: один контракт для непустого и пустого текста, видимая ошибка вместо `catch { }`, сохранение доступного recovery-журнала. Автотесты: `UxPackageATests.InlineEditor_*`. Ручной прогон смены заметки не закрыт.
- [~] Устранить конфликт глобального OCR `Ctrl+Shift+O` с нумерованным Markdown-списком и ввести единый проверяемый источник подсказок shortcuts. Каталог `ShortcutCatalog`; OCR по умолчанию `Ctrl+Shift+R`; приостановка OCR при фокусе в теле заметки; заголовок `Ctrl+Shift+H`. Сохранение настроек отклоняет OCR, совпадающий с командой редактора; существующий профиль с `Ctrl+Shift+O` по-прежнему регистрирует глобальные hotkey, пока каретка не в теле заметки. Автотесты: `UxPackageATests.ShortcutCatalog_*`, `GlobalHotkeyService_SuspendsOcrWhileEditorHasFocus`, `GlobalHotkeyService_Register_AllowsLegacyOcrOnNumberedListOutsideEditor`. Ручная проверка физических hotkey не закрыта.
- [~] Унифицировать `Esc`, кнопку отмены и крестик редактора: «Сохранить / Не сохранять / Вернуться» с честным объяснением судьбы черновика. Диалог `UnsavedEditorDialog`. Автотесты: `UxPackageATests.NoteEditor_EscCancelAndClose_UseSaveDiscardStayContract`, `UnsavedDialog_ShowsSaveDiscardStayButtons`. Изолированный `--ux-package-ab-smoke` пишет кадры `unsaved-editor-*` (light/dark × wide/narrow) в `artifacts/ux-package-ab-acceptance/` (`UxPackageAbSmokeTests`). Ручной прогон Esc/крестика не закрыт.

#### Пакет B — понятный первый час (блокирует Release 0.9)

- [~] Разделить пустую базу, пустой раздел и нулевой результат поиска/фильтра; дать клавиатурно доступное действие «Сбросить фильтр»; для закрытых защищённых заметок объяснить границу поиска без раскрытия данных. Автотесты: `UxPackageBTests.EmptyStates_DistinguishLibrarySectionAndSearch`. Изолированный `--ux-package-ab-smoke` пишет кадры `empty-library-*`, `empty-section-*`, `search-empty-*` в `artifacts/ux-package-ab-acceptance/` (`UxPackageAbSmokeTests`). Ручной прогон пустых состояний не закрыт.
- [~] Исправить fixed-size/обрезку первого запуска (`ResizeMode=CanResize`, прокрутка) и не отмечать onboarding завершённым при закрытии крестиком без «Начать работу». Автотесты: `UxPackageBTests.FirstRun_CloseWithoutStart_DoesNotCompleteOnboarding`, `FirstRunAndHelp_AreResizableAndMentionTray`. Изолированный `--ux-package-ab-smoke` пишет кадры `first-run-*` в `artifacts/ux-package-ab-acceptance/`. Ручной прогон первого запуска не закрыт.
- [~] При первом закрытии главного окна объяснить работу в трее и дать выбор «Скрыть / Выйти» с запоминанием; отразить в справке, первом запуске и настройках. Автотесты: `UxPackageBTests.CloseToTray_*`. Изолированный `--ux-package-ab-smoke` пишет кадры `close-to-tray-*` в `artifacts/ux-package-ab-acceptance/`. Ручной прогон закрытия в трей не закрыт.
- [~] Сделать виртуальные разделы списком с клавиатуры (`VirtualSectionsList`), вернуть «Новая заметка» и «Сбросить» в Tab, видимый focus ring на разделах; подписи «из текста заметок», «Новый тег», «Снимок». Автотесты: `UxPackageBTests.FirstRunAndHelp_AreResizableAndMentionTray`, `ScreenOcrTests` для подписи снимка. Изолированный `--ux-package-ab-smoke` пишет кадры `focus-ring-*` в `artifacts/ux-package-ab-acceptance/`. Ручной Tab-проход, физический DPI и Narrator/NVDA не закрыты.

#### Пакет C — читаемая ежедневная работа (до Release 1.0)

- [~] Гарантировать визуальный приоритет заголовка карточки; сократить даты, убрать локальный ID из постоянного вида, не показывать неуместный облачный шум. `NoteCardViewModel`: короткий `UpdatedAtShortText`, `ShowPublicationStatusOnCard` только для PendingUpload/Syncing/Conflict/Error; шаблон карточки в `MainWindow.xaml` без `IdText`/`MaxWidth=360`. Автотесты: `UxPackageC08Tests`. Изолированный `--ux-c08-smoke` пишет кадры `note-cards-*` (light/dark × wide/narrow) в `artifacts/ux-c08-acceptance/` (`UxC08SmokeTests`). Ручная визуальная/DPI-приёмка и Narrator не закрыты.
- [~] Свести первый уровень статусов к «Сохранено на этом ПК / Ожидает отправки / В облаке / Конфликт / Ошибка», оставив технические детали во втором уровне. Каталог `PublicationStatusCopy`; карточка показывает first-level только для PendingUpload/Syncing/Conflict/Error; status bar `PublicationStatusBarText` отвечает по выбранной заметке (иначе по `MainSyncStatus`). Автотесты: `UxPackageC11Tests`, `LocalCommitSyncBoundaryTests.PublicationStatus_LifecycleTransitions`, `MainViewModelSyncTests`. Изолированный `--ux-c11-smoke` пишет кадры `status-*` (пять состояний × light/dark) в `artifacts/ux-c11-acceptance/` (`UxC11SmokeTests`). Ручная приёмка понимания статусов не закрыта.
- [~] Показать происхождение ручных/автоматических тегов и понятный путь возврата подавленного автотега; убрать `All/Any` из русского UI. Каталог `TagOriginCopy`; чипы `OriginMarker` (`вручную`/`авто`) и секция «Скрытые автотеги» с «Вернуть»; `SuppressedTagIds` без изменения persistence. Автотесты: `UxPackageC12Tests`, `TagDetectionServiceTests`. Изолированный `--ux-c12-smoke` пишет кадры `editor-tags-*` (light/dark × wide/narrow) и `tag-rules-*` в `artifacts/ux-c12-acceptance/` (`UxC12SmokeTests`). Ручная приёмка чипов на физическом DPI не закрыта.
- [~] Переписать первый уровень cloud/archive/import-export языком пользовательских задач; мастер оставить основным путём, сложные параметры — под «Дополнительно». Каталог `UserTaskCopy`; настройки облака ведут с «Подключить через мастер…»; бакет/S3/endpoint под expander; окно «Перенос заметок» со вкладками «Сохранить копии / Загрузить копии / Архив с паролем»; предупреждения шифрования остаются видимы. Автотесты: `UxPackageC13Tests`. Изолированный `--ux-c13-smoke` пишет кадры `cloud-first-*`, `cloud-advanced-*`, `transfer-*` (light/dark) в `artifacts/ux-c13-acceptance/` (`UxC13SmokeTests`). Живое облако и ручной прогон мастера не закрыты.
- [~] Нормализовать открытие/раскрытие карточки: один клик выбирает, `Enter` открывает в панели, `Ctrl+E` — в отдельном окне, полный текст — явная кнопка «Развернуть» / «Свернуть». Скрытый двойной клик по шапке/телу убран. Каталог `CardGestureCopy`. Автотесты: `UxPackageC14Tests`. Изолированный `--ux-c14-smoke` пишет кадры `cards-collapsed-*` / `cards-expanded-*` (light/dark × wide/narrow) в `artifacts/ux-c14-acceptance/` (`UxC14SmokeTests`). Живые жесты мыши/клавиатуры закрываются протоколом `docs/ux/manual-acceptance-protocol.md`.

#### Пакет D — визуальная и accessibility-приёмка (до Release 1.0)

- [~] Логическая матрица светлая/тёмная тема, обычная/узкая ширина, длинные русские строки и контраст theme-кистей: `--ux-package-d-layout-smoke` пишет `help-*`, `names-main-*`, `contrast-report.json`, `automation-names.json` в `artifacts/ux-package-d-acceptance/` (`UxPackageDTests`, `UxPackageDSmokeTests`). Физический DPI 100/125/150/200% не закрыт.
- [~] Автоматизированы UI Automation names новых контролов (разворот карточки, «Вернуть» автотег, мастер облака) и WCAG-расчёт luminance кистей. Проход Tab-only, Narrator/NVDA, системный High Contrast и измерение контраста на физических пикселях остаются в `docs/ux/manual-acceptance-protocol.md`.

Критерий M6: пакеты A–B закрыты тестами и ручным прогоном без тихой потери данных/неявного выбора версии; пакеты C–D остаются видимыми обязательствами Release 1.0.

Автодоказательство screenshot smoke A+B (18.09.2026, только автоматическая часть): `QuickNotes.App.exe --isolated-profile <tmp> --ux-package-ab-smoke --output-dir <tmp>` завершился `QN_UX_PACKAGE_AB_SMOKE_SUCCESS` (exit 0, при ошибке — ненулевой exit); 44 PNG (11 сцен × light/dark × wide/narrow) + 44 `.visible.txt` в изолированном каталоге проверены: PNG-сигнатура, декодируемость, размеры (диалоги и merge 780×720/640×560, главное окно 1100×720/800×650), ненулевой размер, отсутствие маркера `UXAB_SECRET_MARKER_MUST_NOT_LEAK`, цифры 1–4 в merge без resolve, живой `%LOCALAPPDATA%\QuickNotes` не изменён; `UxPackageAbSmokeTests.RunIsolatedUiSmokeProcess_GeneratesValidAcceptanceScreenshots_AndExitsZero` зелёный. Ручной hotkey-прогон, физический DPI 100/125/150/200%, Narrator/NVDA, живое облако и интерактивное ручное разрешение конфликтов остаются открытыми и этим прогоном не закрываются.

Критерий Release 0.9: M0–M6 закрыты в указанной для M6 границе; неизвестные ограничения перечислены в release notes; ни одна известная потеря данных не остаётся открытой.

## 6. Release 1.0 «Захват и поиск» — ежедневная работа

Вход: принят Release 0.9.

### 6.1. Заголовок заметки

- [x] Добавить отдельное поле `Title` с версионированной миграцией. (схема v12 в `DbInitializer.cs`, `Note.Title`, `NoteRevision.Title`; тесты `SchemaMigrationAndRecoveryTests`)
- [x] Для старых заметок вывести заголовок из первой непустой строки без уничтожения исходного текста. (`NoteTitleHelper.DeriveTitleFromText`, автозаполнение при миграции v12; тесты `SchemaMigrationAndRecoveryTests`)
- [x] Включить заголовок в FTS, обычный поиск, историю, экспорт, импорт и Sync. (`SearchService`, `NoteHistoryService`, `NoteExportService`, `NoteImportService`, `SyncPackageExporter`, `SyncConflictService`; тесты `ImportExportTests`, `NoteHistoryTests`)
- [x] Защищать заголовок вместе с содержимым защищённой заметки; не оставлять plaintext-превью. (`NoteProtectionService`, `NoteProtectedRevisionPayload`, `NoteCardViewModel`; тесты `NoteProtectionTests`)
- [x] Разрешить пустой заголовок со стабильным UI fallback, не подменяя данные. (`NoteTitleHelper.FormatDisplayTitle` → «Без заголовка»; пустой заголовок сохраняется в БД как `string.Empty`)

### 6.2. Журнал черновика

- [x] Разделить редактируемый черновик, локальный commit и облачную публикацию. (`DraftJournalService`, черновик пишется в отдельный `.journal` файл без транзакций SQLite, ревизий и очереди Sync; тест `DraftJournalTests.DebounceTiming_PersistsAsynchronouslyWithinOneSecond_WithoutModifyingDatabase`)
- [x] Писать локальный журнал чаще основного автосохранения без блокировки ввода. (Debounce 500 мс в `NoteEditorViewModel`, асинхронный неблокирующий `SaveJournalAsync` с безопасной атомарной заменой файла через `IDraftJournalFileOperations` (`File.Replace` с бэкапом и неразрушающим fallback, без удаления оригинала до переноса); тесты `DraftJournalTests.DebounceTiming_PersistsAsynchronouslyWithinOneSecond_WithoutModifyingDatabase`, `DraftJournalTests.AtomicReplacement_FaultInjection_LeavesPreviousValidJournalReadableAndCleansUpDebris`)
- [x] Восстанавливать черновик после kill/crash и показывать пользователю источник восстановления. (`DraftRecoveryWindow`, `DraftDiffHelper`, `CheckAndPromptDraftRecovery`; продуктовый поток при старте `MainViewModel.CheckAndPromptNewNoteRecoveries` в `MainWindow_Loaded`; восстановление полного состояния: заголовок, текст, контекст источника, активные/подавленные теги, ссылки на вложения без дублирования файлов; тесты `DraftJournalTests.CrashRecovery_DetectsUncommittedJournal_SideBySideComparison_RestoresDraft`, `DraftJournalTests.CrashRecovery_ExplicitDiscard_DeletesJournal_PreservesCommittedData`, `DraftJournalTests.StartupFlow_DiscoversUncommittedNewNoteJournals_RestoresOrDiscardsWithoutOverwritingCommittedData`, `DraftJournalTests.DraftRecovery_RestoresCompleteDraftState_TagsSuppressionsContextAndAttachmentsWithoutDuplication`)
- [x] Удалять журнал только после подтверждённого локального commit. (`NoteEditorViewModel.CommitAndClose`, `MainViewModel.SaveNewNoteFromEditor`, `MainViewModel.EditNote` удаляют журнал только после успешного завершения транзакции коммита; при сбое транзакции/SaveChanges журнал остаётся на диске; тест `DraftJournalTests.Commit_SuccessDeletesMatchingJournal_FailurePreservesJournal`)
- [x] Для защищённых заметок журнал должен оставаться зашифрованным и не использовать общий plaintext temp. (`DraftJournalCryptoEnvelope`, `DraftJournalProtectedPayload` полностью капсулирует заголовок, текст, контекст источника, `CapturedAt`, теги и вложения; внешний конверт содержит только `NoteId`, `DraftId`, `SequenceNumber`, `SavedAtUtc`, `IsProtected`, `Crypto`; шифрование через `INoteCryptoService.EncryptWithKey` сессионным ключом с AAD `NoteProtectedObjectType.DraftJournal`; 0 байт открытого текста и метаданных на диске; честный отказ при заблокированной сессии без plaintext fallback; очистка пользовательских результатов от путей ФС и технических деталей исключений; тесты `DraftJournalTests.ProtectedJournal_ZeroPlaintextOnDisk_DecryptsOnlyInUnlockedSession`, `DraftJournalTests.ProtectedJournal_ZeroMetadataOrPlaintextLeak_UniqueSecretsTest`, `DraftJournalTests.UserFacingDraftResults_ContainNoFilesystemPathsOrTechnicalExceptionDetails`)

### 6.3. Локальное сохранение отдельно от Sync

- [x] Локальный commit не ждёт сеть и не откатывается из-за облачной ошибки. (Операции создания и изменения заметок `SaveInstantNote`, `SaveNewNoteFromEditor`, `SaveNote`, `MoveNoteToTrash`, `RestoreNote`, `PermanentDeleteNote`, `EmptyTrash` и быстрые действия выполняются исключительно через координатор локальных мутаций `LocalMutationCoordinator` и транзакции SQLite; сетевой ввод-вывод Sync полностью вынесен за пределы блокировок; тесты `LocalCommitSyncBoundaryTests.LocalSave_SucceedsOffline_AndPersistsAfterReopen`, `LocalCommitSyncBoundaryTests.SyncFailure_AfterCommit_LeavesDurablePendingState_DoesNotUndoLocalNote`)
- [x] Sync получает устойчивую очередь изменений/revisions после локального commit. (`EnqueueLocalChange` вызывается строго после подтверждённого коммита в SQLite; состояние готовности к отправке хранится в существующей схеме v12 через `SyncEntityStates` и `SyncLocalState` без дублирующих таблиц outbox; обнаружение `HasDurablePendingWork` и `GetPendingNoteSyncIds` сохраняется через crash/restart; тесты `LocalCommitSyncBoundaryTests.CrashRestart_BetweenCommitAndSync_RetainsPendingWorkAcrossRestart`, `SyncProductionWiringTests.SaveInstantNote_CallsEnqueueLocalChange_StrictlyAfterDatabaseCommit`)
- [x] Повторы идемпотентны; статус различает «сохранено локально», «ожидает отправки», «синхронизировано» и «требует решения». (Модель `LocalCommitSyncStatus`: `SavedLocally`, `PendingUpload`, `Syncing`, `Synchronized`, `Conflict`, `Error`; компактный индикатор в шапке карточки заметки и детальный tooltip; дедупликация и коалесценция вызовов очереди, строгий порядок блокировок по ADR-006 с запретом вложенных блокировок; тесты `LocalCommitSyncBoundaryTests.DuplicateEnqueueAndRetry_ProducesNoDuplicateRevisionOrPackage`, `LocalCommitSyncBoundaryTests.OrderingAndRaces_ConcurrentDistinctNotes_SerializedSameNote_ExclusiveSyncApply_NestedLockRejection`, `LocalCommitSyncBoundaryTests.PublicationStatus_LifecycleTransitions`)
- [x] Закрытие приложения не теряет подтверждённый локальный commit. (Ограниченный по времени дренаж фоновой синхронизации `DrainAndStopAsync` и `MainViewModel.ShutdownAsync` с таймаутом без зависания процесса; подтверждённые локальные коммиты остаются в сохранности в SQLite; тесты `LocalCommitSyncBoundaryTests.BoundedShutdown_DoesNotHang_PreservesConfirmedCommit`, `LocalCommitSyncBoundaryTests.ProtectionAndAttachmentMutations_UnderCoordinator_NoLocksAcrossNetwork`)

### 6.4. Поиск

- [x] Искать по заголовку и тексту одновременно. (`SearchService.BuildFtsQuery` форматирует слова как префиксные токены с `AND`, обеспечивая одновременный поиск в `Title` и `Text`, сохраняет точные фразы в кавычках, префиксы и кириллицу; защищённые заметки не попадают в FTS; тесты `SearchVerticalSliceTests.Search_TitleOnly_MatchesFreeTextQuery`, `Search_BodyOnly_MatchesFreeTextQuery`, `Search_CrossColumn_MatchesWhenTermsAreInDifferentColumns`, `Search_PhraseQuery_MatchesExactPhraseOnly`, `Search_PrefixQuery_MatchesRussianAndEnglishPrefix`, `Search_ProtectedNote_ExcludedFromFtsAndQuery`)
- [x] Фильтровать по ветке дерева тегов и пересечению нескольких тегов. (`SearchService.ApplySectionAndTagFilters` разворачивает поддерево выбранного в навигации тега и детерминированно вычисляет пересечение, объединение и исключение тегов из поисковой строки с синтаксисом `AND`, `OR`, `WITHOUT`; тесты `SearchVerticalSliceTests.TagFilter_SubtreeExpansion_MatchesChildTagWhenParentSelected`, `TagFilter_MultiTagIntersection_AndCondition`, `TagFilter_MultiTagUnion_OrCondition`, `TagFilter_TagExclusion_WithoutCondition`, `TagFilter_SelectedTagAndQueryCondition_IntersectsCorrectly`)
- [x] Подсвечивать совпадения в списке и открытой заметке. (Подсветка в карточках списка для `DisplayTitle` и `PreviewText` через `HighlightText`, подсветка в редакторе Markdown через `MarkdownPreviewService` на `FlowDocument`; `SearchPreview` парсит только свободный текст, исключая операторы `tag:`, `source:` и экранируя спецсимволы regex; тесты `SearchVerticalSliceTests.Highlight_ExcludesStructuredOperators`, `Highlight_RegexMetacharacters_MatchLiterallyWithoutException`, `MarkdownPreviewServiceTests`)
- [x] Добавить переход к следующему/предыдущему совпадению и поиск внутри текущей заметки. (Компактная панель поиска в `NoteEditorWindow` и команды `NoteEditorViewModel` по Ctrl+F, F3, Shift+F3, Esc; циклическая навигация, отображение счётчика `X/Y`, выделение найденного текста без мутации содержимого заметки; тесты `SearchVerticalSliceTests.NoteEditor_FindInCurrentNote_NextPreviousWrapAndCount`, `NoteEditor_FindSelectionCallback_FiresWithCorrectOffsets`)
- [x] Не материализовывать весь подходящий набор до пагинации без измеренной необходимости. (`SearchService.QueryNotes` и `CountNotes` работают на стороне базы через SQL `LIMIT`/`OFFSET` и `COUNT(*)`; для обычного и объединённого FTS+unlocked поиска детерминированная страница идентификаторов выбирается до гидратации сущностей по первичному ключу, исключая сбои проекции коллекций над `UNION`; тесты `SearchVerticalSliceTests.BoundedPaging_10000Notes_ObservableBoundedMaterializationAndContinuation`, `BoundedPaging_LargeUnlockedProtectedHits_MergesIntoPagedStream`)
- [x] Не создавать `NoteCardViewModel` для всей базы одновременно. (`MainViewModel` запрашивает первую страницу размером 50 заметок и инстанцирует `NoteCardViewModel` только для видимой выборки; кнопка «Загрузить ещё» осуществляет порционную дозагрузку с сохранением детерминированного порядка сортировки без дубликатов; быстрый ввод отменяет устаревшие запросы через `CancellationToken`; тесты `SearchVerticalSliceTests.BoundedPaging_10000Notes_ObservableBoundedMaterializationAndContinuation`, `SearchVerticalSliceTests.SearchCancellation_StaleQueryCancellation_CancelsPriorSearch`)

### 6.5. Измеримая производительность

- [x] Зафиксировать эталонную машину и методику измерения. (`docs/performance/baseline-report-2026-09-11.md`: 12th Gen Intel Core i5-12400, 12 ядер, 32 ГБ RAM, NVMe SSD, Windows 10.0.26200, .NET 8.0 Release x64; изоляция в temp-профиле, прогревочный прогон + 5 серий замеров, медиана и p95)
- [x] Создать синтетический набор: 10 000 заметок, реалистичное дерево тегов, история и 500 МБ вложений. (`SyntheticDatasetGenerator` в `QuickNotes.Tools`: детерминированная генерация по seed 1337 за 7,2 с; 10 000 заметок, 48 тегов в 3-уровневом дереве с синонимами, 5 001 ревизия истории, 45 файлов вложений на 500,0 МБ, SQLite 25,6 МБ; тесты `PerformanceBaselineSmokeTests.DeterministicGeneration_SameSeedYieldsIdenticalData`, `DeterministicGeneration_DifferentSeedYieldsDifferentData`)
- [x] Холодный старт до готовности ввода: целевой p95 ≤ 1,5 с. (Закрыто **только** для framework-dependent `win-x64` publish с `PublishReadyToRun=true`, isolated `--perf-startup`, 2 warmup + 10 measured, ABBA, 2026-09-13 UTC: R2R median = 1360,5 мс, p95 = 1385,7 мс. Same-run publish без R2R: median = 1758,0 мс, p95 = 1768,3 мс — FAIL. Обычный `dotnet build` output R2R не получает. `docs/performance/cold-start-r2r-report-2026-09-13.md`, runner `scripts/Measure-ColdStartReadyToRun.ps1`.)
- [x] Поиск по мере ввода: целевой p95 ≤ 150 мс. (Зафиксирован фактический замер на 10 000 заметок: overall median = 19,6 мс, p95 = 24,2 мс для заголовка, тела, пересечения тегов и запросов без совпадений; пагинация строго ограничена 50 карточками; `docs/performance/baseline-report-2026-09-11.md`)
- [x] Открытие редактора: целевой p95 ≤ 300 мс. (Зафиксирован фактический замер на базе 10 000 заметок: median = 36,1 мс, p95 = 44,0 мс включая инициализацию `NoteEditorViewModel`, историю, вложения и компоновку окна; `docs/performance/baseline-report-2026-09-11.md`)
- [ ] Прокрутка списка: не ниже 50 fps на эталонном наборе. (По-прежнему UNVERIFIED: `CompositionTarget.Rendering` не принимается как FPS. Opt-in harness `scripts/Measure-ScrollFpsPresentMon.ps1` пишет PresentMon-отчёт только при уже установленном локальном PresentMon.exe, без загрузки инструментов и без живого `%LOCALAPPDATA%\QuickNotes`. Реального физического capture в репозитории нет; пункт не закрыт.)
- [x] Память в покое: целевой предел ≤ 400 МБ. (Зафиксирован фактический замер физического рабочего набора после инициализации UI и списка заметок: median = 197,2 МБ, p95 = 201,1 МБ при лимите 400 МБ; `docs/performance/baseline-report-2026-09-11.md`)
- [x] Если цель недостижима на эталонной машине, изменить число через ADR с измерениями, а не молча. (Число 1,5 с не меняли. Gate закрыт publish-time ReadyToRun, а не ослаблением цели; same-run publish без R2R по-прежнему выше 1,5 с.)

Критерий Release 1.0: пользователь неделю ведёт новое текстовое знание только в QuickNotes; захват не деградировал; поиск и восстановление черновика укладываются в измеренные границы.

### 6.6. Консолидированная локальная приёмка M5 (без облака)

Единая точка входа для уже существующих не-облачных UI/продуктовых проверок из §5.6. Не пересобирает UI и не выдаёт физическую/пользовательскую приёмку за синтетические тесты. Чекбоксы §5.6 остаются открытыми, пока нет живого доказательства.

- [x] Инвентарь автопокрытия строк §5.6 (окно/тема/DPI, захват, редактор, теги, поиск, вложения/OCR/история/импорт/экспорт/корзина, Sync UI, hotkey) зафиксирован в манифесте `scripts/Run-LocalAcceptancePack.ps1`.
- [x] Один PowerShell-раннер: уникальный isolated profile + `artifacts/local-acceptance/<run-id>/`, hang protection `--blame-hang --blame-hang-timeout 2m`, Markdown-манифест. Живой `%LOCALAPPDATA%\QuickNotes` не используется как профиль и не пишется; metadata-only snapshot (путь/размер/LastWriteTimeUtc) читается для детекта мутаций, без content hash. LiveCloud исключён. В процессе раннера (и только там; child `dotnet test` наследует) сбрасываются `QUICKNOTES_LIVE_S3_*`, `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` / `AWS_SESSION_TOKEN`, `AWS_PROFILE`, `AWS_REGION`, `AWS_DEFAULT_REGION`, `AWS_SHARED_CREDENTIALS_FILE`. Вызывающий shell и прочие процессы хоста не санитизируются. Тестовый шаг: `--no-restore --no-build` после локального `dotnet build` (без NuGet в этом шаге; отсутствие DLL — явный отказ). Режимы `Lightweight` / `Focused`. Не утверждается отсутствие любой сетевой активности хоста.
- [x] Guards: отказ live-профиля/предков/потомков; рекурсивное удаление только внутри уникально принадлежащего temp; сбой тестов даёт ненулевой exit; валидный TRX с нулём executed tests — ненулевой exit (`SelfTestZeroExecuted`). Тесты `LocalAcceptancePackTests`.
- [ ] Окно: обычная и узкая ширина, светлая/тёмная тема, 100/125/150/200% DPI. Частично: layout/theme и синтетический DPI в `ThreePaneWorkspaceSmokeTests`, `ThemeAndPaletteGuardTests`. Физический монитор и системный scaling — ручные.
- [~] Быстрый захват: разные приложения, Unicode, пустой/недоступный буфер, повтор hotkey. Автотесты: `SourceContextAndHotkeyTests`, `ClipboardCaptureService` diagnostics, `RegressionTests.Hotkey_*`. Windows 11 VM, 14.09.2026: живой Notepad, Unicode, пустой буфер и быстрый повтор — PASS; второе приложение и принудительно заблокированный буфер открыты. Протокол: [`docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md`](docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md).
- [ ] Редактор: создание, отмена, явное сохранение, аварийное закрытие, длинная заметка. Частично: `DraftJournalTests`, `NoteTemplateTests` (лимит длинного текста), cancel/unsaved. Диалог восстановления после kill UI — ручной.
- [ ] Теги: дерево, синонимы, границы слов, ручные/автоматические/подавленные. Частично: `TagRuleTests`, `TagTreeImprovementTests`, `TagRescanPreviewTests`. Визуальные chip/tree — ручные.
- [ ] Поиск: текст, теги, FTS fallback, защищённые заметки, пустой результат. Частично: `SearchVerticalSliceTests`, `SmartSearchTests`. Набор в живом окне — ручной.
- [ ] Вложения, OCR, история, импорт/экспорт, корзина. Частично: соответствующие focused-тесты и UI smoke. File picker / crop OCR / wizard — ручные.
- [ ] Sync UI: настройка, статус, конфликт, квота, очистка и ротация. Частично: fake-store ViewModel/smoke. Живой бакет этим пакетом не используется.
- [x] Отсутствие деградации основного hotkey-сценария. Регистрация/конфликт hotkey и instant save покрыты автотестами; сквозной живой захват из Notepad с сохранением inbox-заметки подтверждён на Windows 11 VM 14.09.2026. Протокол: [`docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md`](docs/acceptance/windows11-interactive-product-acceptance-2026-09-14.md).

Критерий пакета: один воспроизводимый локальный прогон с манифестом; ручные строки явно открыты; живой профиль не используется как профиль и не пишется; облачные credentials не задаются.

### 6.7. Опциональный физический замер FPS прокрутки (PresentMon)

- [x] Opt-in runner и парсер: `scripts/Measure-ScrollFpsPresentMon.ps1`, `QuickNotes.Tools.exe scroll-fps-parse`, фикстуры CSV и тесты `PresentMonCsvParserTests` / `ScrollFpsPresentMonHarnessTests`. Живой профиль не используется; PresentMon только если exe уже установлен; нет download/network fetch; нет подмены `CompositionTarget.Rendering`.
- [ ] Физический PresentMon/ETW capture на мониторе с ручной прокруткой списка и результатом PASS (≥ 50 fps displayed frames). Не закрыто: в этом дереве нет реального capture.

## 7. Release 1.1 «Страницы» — структурированные заметки без WebView2

Вход: принят Release 1.0; ADR-003 действует.

- [x] Трёхпанельная компоновка: навигация, список, редактор/просмотр. (В `MainWindow.xaml` реализованы 5 колонок: навигация `NavBorder`, сплиттер `NavSplitter`, список `NotesListGrid`, сплиттер `DetailSplitter`, детальная панель `DetailPaneBorder`; встроенный `NoteEditorViewModel` с редактированием, разметкой Markdown, переключением режима редактор/предпросмотр, адаптивным тулбаром с компактными кнопками, WrapPanel и overflow-меню «•••» без горизонтальной обрезки при 1100 DIP, адаптивной статусной строкой с гарантией видимости статуса заметок/синхронизации и безопасным отображением подсказок при 800 DIP; в узком режиме < 900 DIP включается детерминированный master/detail переход с кнопкой «Назад» `BackToMasterButton`; CLI `--three-pane-smoke` генерирует проверенные скриншоты в `artifacts/three-pane-acceptance/`; тесты `ThreePaneWorkspaceSmokeTests.PanePresence_WideMode_HasNavListAndDetailPanes`, `SelectingListItem_UpdatesDetailPane_WithoutModalWindow`, `NarrowMode_MasterDetailTransitions_AndBackCommandPreservesDraft`, `RunIsolatedUiSmokeProcess_GeneratesValidAcceptanceScreenshots_AndExitsZero`, `GenerateAcceptanceScreenshots_WideAndNarrow_LightAndDark`, `LayoutBounds_ToolbarAndStatusContent_NeverOverflowClientBounds`)
- [x] Сохранять компактное отдельное окно быстрого захвата. (`NoteEditorWindow` полностью сохранён для глобального hotkey, захвата буфера/OCR и модального сценария через `ShowEditorWindow`; координатор и сохранение черновиков интактны)
- [x] Запоминать размеры и состояние панелей без потери рабочего пространства. (`WorkspaceLayoutHelper.ClampNavWidth`, `ClampListWidth`, `ClampWindowBounds` с безопасным центрированием и ограничением рабочих областей экрана при null/NaN/некорректных координатах; `AppSettings.NavigationPanelWidth`, `NoteListPanelWidth`, `WindowLeft`, `WindowTop`, `WindowWidth`, `WindowHeight`, `WindowState`; сохранение в `SettingsService` и восстановление в `MainWindow_Loaded`; тесты `ThreePaneWorkspaceSmokeTests.WorkspaceLayoutHelper_ClampingAndCalculations_RecoversFromCorruptOrExtremeInputs`, `SettingsPersistence_CorruptOrOutOfRangeSplitter_SafelyClamped`)
- [x] Поддержать клавиатурную навигацию, screen reader, focus order и DPI. (Циклическая навигация по F6 между Навигацией -> Списком -> Редактором; быстрые клавиши Ctrl+S, Ctrl+P, Esc / Alt+Left для возврата в узком режиме; установлены `AutomationProperties.Name` и всплывающие подсказки для сплиттеров `NavSplitter`, `DetailSplitter`, кнопки возврата `BackToMasterButton`, полей ввода `DetailTitleTextBox`, `DetailBodyBox` и `SearchBox`; проверен рендеринг при 100%, 125% native, 150%, 200% DPI; тесты `ThreePaneWorkspaceSmokeTests.KeyboardNavigation_And_AccessibleNames`, `MultiDpiRendering_InspectsLayoutsAtSupportedDpi_NoOverlapOrClipping`)

### 7.2. Markdown-редактор и живой предпросмотр

- [x] Простой текст Markdown остаётся единственным источником истины. (`Note.Text` хранит чистый UTF-8 текст; AST, FlowDocument, оглавление и сворачивание секций являются read-only проекциями без сохранения в БД или файл; тесты `MarkdownEditorOperationsTests`, `MarkdownOutlineAndPreviewTests`, `MarkdownTransactionalPasteTests`)
- [x] Разделённый режим редактирование/предпросмотр на существующем WPF/FlowDocument-рендерере. (`MarkdownViewMode`: `Edit`, `Split`, `Preview`; паритет между детальной панелью `MainWindow` и отдельным окном `NoteEditorWindow`; адаптивное распределение колонок GridSplitter; дебаунс предпросмотра 90 мс без блокировки UI; тесты `MarkdownOutlineAndPreviewTests.PreviewDebounce_DebouncesRapidTyping_AndCancelsPrevious`, `ThreePaneWorkspaceSmokeTests.GenerateAcceptanceScreenshots_WideAndNarrow_LightAndDark`, `ThreePaneWorkspaceSmokeTests.RunIsolatedUiSmokeProcess_GeneratesValidAcceptanceScreenshots_AndExitsZero`)
- [x] Горячие клавиши: заголовок, emphasis, strong, ссылка, код, список и checkbox. (Кнопки тулбара и шорткаты Ctrl+B, Ctrl+I, Ctrl+K, Ctrl+H, Ctrl+Shift+C, Ctrl+Shift+U, Ctrl+Shift+O, Ctrl+Shift+X, Ctrl+Shift+T, Ctrl+Shift+O; детерминированное оборачивание выделения и расчёт каретки без прыжков; тесты `MarkdownEditorOperationsTests.ToggleBold_*`, `ToggleItalic_*`, `ToggleCode_*`, `ToggleHeading_*`, `CycleHeading_*`, `ToggleBulletList_*`, `ToggleNumberedList_*`, `ToggleCheckbox_*`, `InsertLink_*`)
- [x] Автопродолжение списков без неявной порчи текста. (`MarkdownEditorOperations.HandleEnter` поддерживает `- `, `* `, `1. `, `- [ ] `, `- [x] ` с сохранением отступа; пустой элемент завершает список удалением префикса; замена через `SelectedText` сохраняет нативный стек WPF Undo/Redo; тесты `MarkdownEditorOperationsTests.HandleEnter_*`)
- [x] Вставка изображения из буфера создаёт вложение и Markdown-ссылку одной транзакцией. (`TryPasteImageBytes` и `TryPasteImageFromClipboard` создают вложение `Attachment`, копируют файл и вставляют `![name](relative_path)` в `Note.Text`; при любом сбое или заблокированной защищённой заметке выполняется полный откат: файл удаляется с диска, запись из `Attachments` удаляется, текст восстанавливается, zero metadata leak; тесты `MarkdownTransactionalPasteTests.PasteImage_Success_CreatesAttachmentAndInsertsMarkdown`, `PasteImage_RollsBackFileAndAttachmentOnFailure`, `PasteImage_ProtectedLockedNote_RejectedWithoutAttachmentOrDiskArtifacts`, `PasteImage_ParityBetweenMainWindowAndNoteEditorWindow`)
- [x] Помощник таблиц вставляет обычную Markdown-разметку. (`MarkdownEditorOperations.GenerateTable` и `InsertTable` с валидацией строк/колонок генерируют стандартные pipe-таблицы с заголовком и разделителями; рендерер `MarkdownPreviewService` строит нативную `Table` с выравниваниями `:---`, `:---:`, `---:`; тесты `MarkdownEditorOperationsTests.GenerateTable_*`, `InsertTable_*`, `MarkdownOutlineAndPreviewTests.ParseBlocks_PipeTableWithAlignments_RendersCorrectColumnsAndCells`)
- [x] Добавить сворачиваемые разделы и оглавление, вычисляемые из заголовков. (`HeadingAnchorHelper.CreateSlug` сохраняет кириллицу и устраняет коллизии `-1`, `-2`; интерактивное сворачивание секций FlowDocument `▼`/`▶` без мутации заметки; боковая панель оглавления с бейджами уровней и навигацией каретки; тесты `MarkdownOutlineAndPreviewTests.HeadingAnchorHelper_RussianSlugAndCollisionResolution`, `ExtractOutline_ExtractsHierarchyAndAnchors`, `CollapsibleHeadings_ToggleHidesChildContent_PreservesMarkdownText`)
- [N] Не добавлять WebView2, HTML как источник истины, JS-бандл или браузерное хранение защищённого текста. (Соблюдено: `QuickNotes.App.csproj` не содержит WebView2; предпросмотр работает на WPF `FlowDocument`; `Note.Text` остаётся единственным форматом; ADR-003 и ADR-007)

Критерий Release 1.1: длинная структурированная заметка редактируется, ищется, шифруется, версионируется, экспортируется и синхронизируется без второго формата документа.

## 8. Release 1.2 «Перенос» — старые материалы и честные конфликты

### 8.1. Импорт

- [x] Поддержать HTML и Markdown; Word — только через документированный промежуточный экспорт. (`HtmlToMarkdownConverter`, `NoteImportService.TryParseHtmlFile`; Word/OneNote отклоняются с отсылкой к `docs/import-from-word-and-html.md`; HTML не хранится; тесты `HtmlToMarkdownConverterTests`, `ImportMigrationDedupAndTransactionTests.HtmlIsNotStoredAsCanonicalFormat`)
- [N] Не писать прямой парсер внутреннего формата OneNote без нового измеренного основания.
- [x] Предпросмотр сопоставления: блокнот → корневой тег, раздел → дочерний тег. (`ImportFolderMapper`, редактируемые `FolderMappings` в `ImportExportWindow`; глубже одного раздела — неоднозначность, без тихого flatten; 16 скриншотов mapping/diagnostics/duplicates/summary × light/dark × wide/narrow с различимым содержимым; тесты `ImportFolderMappingTests`, `ImportMigrationSmokeTests.GenerateAcceptanceScreenshots_MappingDiagnosticsDuplicatesSummary_LightDark_WideNarrow`)
- [x] Показывать неподдержанные элементы и потери до commit. (`ImportItemPreview.LostElements`, обход дерева сообщает `.doc`/`.docx`/OneNote и прочие неподдерживаемые файлы без разбора бинарников; блокирующая диагностика отключает commit, неблокирующие потери требуют `LossesAcknowledged`; тёмная тема ComboBox разрешения дубликата использует `InputBgBrush`/`InputFgBrush`; узкий 640×560 прокручивает каждый конфликт к фокусу; после успешного commit показывается итог imported/replaced/skipped/tags без предпросмотра; тесты `ImportMigrationUiTests`, `ImportMigrationDedupAndTransactionTests.DirectoryDiscovery_ReportsNestedWordAndUnsupported_WithoutParsingBinary`)
- [x] Импорт выполнять транзакционно, с полной отменой и повторяемым результатом. (staging + DB transaction; откат удаляет только файлы с `WasCreated`; отпечаток = нормализованный относительный путь + SHA-256 содержимого, без timestamp; тесты `FailureInjection_RollsBackDbAndFiles`, `Rollback_DoesNotDeleteSharedPreexistingAttachmentFile`, `ExactRetry_DoesNotDuplicate`, `TimestampTouch_ExactRetry_DoesNotCreateNewNote`, `Fingerprint_IgnoresTimestamp_ChangesWithContentOrRelativePath`, `RepeatedExecute_IsIdempotent`)
- [x] Дедупликация не объединяет разные заметки молча. (Skip / Import Separate / Replace; Replace для того же логического источника и незащищённой заметки полностью согласует теги, вложения, историю и FTS; тесты `SameTitleDifferentContent_DefaultSkip_ExplicitSeparateKeepsBoth`, `Replace_ReconcilesStaleTagsAndAttachments_AndWritesHistory`, `Replace_InjectedFailure_RestoresOldGraphAndKeepsSharedFiles`)

### 8.2. Сборка быстрых заметок

- [x] Пользователь выбирает порядок, разделители, итоговый заголовок и судьбу исходников. (`NoteAssemblyService` / `NoteAssemblyViewModel`; Ctrl+↑/↓; разделители пустая строка / `---` / `***` / custom; тесты `NoteAssemblyServiceTests`, `NoteAssemblyUiTests`)
- [x] До commit показывается полный preview. (детерминированный Markdown + `PlanHash`; preview не пишет БД; расхождение snapshot после preview блокирует commit)
- [x] Исходники по умолчанию сохраняются; удаление — отдельное подтверждаемое действие. (корзина только при `MoveSourcesToTrash` + `TrashAcknowledged`; одна транзакция; каталог исходников — поиск и страницы по 50; 16 скриншотов setup/preview/destructive/summary × light/dark × wide/narrow в `artifacts/quick-note-assembly-acceptance/` с видимым Markdown-preview и unchecked acknowledgement)

### 8.3. Конфликты Sync

- [x] Экран сравнения показывает локальную/удалённую версии, устройство, время и причину конфликта. (`SyncConflictsWindow` / `SyncConflictDetail`: заголовок, Markdown, `LocalDeviceDisplay`/`RemoteDeviceDisplay`, время версии, `ReasonDisplay`; адаптивные 780×720 и 640×560; смена выбора захватывает item/id и отменяет предыдущую detail-load, stale success/error/cancellation не пишут в текущий конфликт; 16 скриншотов compare/merge/protected/success × light/dark × wide/narrow в `artifacts/sync-conflict-acceptance/`; тесты `SyncConflictServiceTests.GetConflictDetail_ShowsDeviceTimeReason_AndProtectedHidesPlaintext`, `SyncConflictsViewModelTests.Detail_ExposesDeviceTimeReason_AndDoesNotSelectDestructiveDefault`, `SyncConflictsViewModelTests.DetailLoad_StaleSuccess_AfterAThenB_DoesNotOverwriteB_WhenBCompletesFirst`, `SyncConflictsViewModelTests.DetailLoad_StaleCancellation_DoesNotChangeCurrentB`, `SyncConflictUiTests`, `SyncConflictSmokeTests`)
- [x] Поддержать Keep Local, Keep Remote, Keep Both и ручное объединение. (`SyncConflictService.ResolveKeepLocalAsync` / `ResolveAcceptRemoteAsync` / `ResolveKeepBothAsync` / `ResolveMergeNoteAsync`; Merge — только Markdown, заголовок и теги явным Local/Remote без union, вложения не смешиваются; нет автовыбора стороны; тесты `SyncConflictServiceTests.ResolveKeepLocal_*`, `ResolveAcceptRemote_*`, `ResolveKeepBoth_*`, `ResolveMerge_*`, `SyncConflictsViewModelTests`)
- [x] Для защищённых заметок не создавать фиктивный plaintext. (UI показывает `LocalProtectionNotice`/`RemoteProtectionNotice`; Merge недоступен; Keep Both копирует ciphertext и `ProtectedOriginalSyncId`; zero plaintext в FTS/логах/скринах; тесты `GetConflictDetail_ShowsDeviceTimeReason_AndProtectedHidesPlaintext`, `ResolveMerge_ProtectedNote_RejectedWithoutPlaintext`, `NoteProtectionRegressionTests.SyncConflict_ProtectedNote_KeepBoth_PreservesCiphertextAndNeverCreatesFakePlaintextNote`, `SyncConflictSmokeTests`)
- [x] Решение конфликта создаёт новую ревизию и синхронизируется идемпотентно. (новая `SyncEntityState`/`NoteRevision`, детерминированный Keep Both `SyncId`, повтор resolved — no-op, enqueue Sync после commit; координатор `ExecuteBulkMutationAsync` и одна транзакция после stale/type/payload; идемпотентная копия только при `KeepBothCopy` revision; тесты `ResolveKeepBoth_IsDeterministic_AndDoesNotDuplicateOnRetryOfExistingCopy`, `ResolveKeepBoth_ExistingCopy_StaleConflict_RemainsUnresolved`, `ResolveKeepBoth_IdempotentExistingCopy_FailureBeforeCommit_RollsBackConflict`, `FailureInjection_OnEachPhase_RollsBackConflictAndNotes`, `NestedMutationCoordinator_ThrowsWithoutPartialResolution`, `Resolve_AllActions_IdempotentOnAlreadyResolvedConflict`)

Критерий Release 1.2: тестовый архив старых материалов импортируется с отчётом; конфликт разрешается пользователем и не приводит к тихой потере версии.

## 9. Release 1.3 «Ритм» — лёгкие задачи без второго продукта

- [x] Виртуальный раздел «Задачи» индексирует существующие Markdown-checkbox `- [ ]`. (`NavigationSection.Tasks`, `TaskIndexService`, список в центральной колонке; тесты `TaskIndexServiceTests`, `TasksIndexUiTests`, `TasksIndexSmokeTests`)
- [x] Не создавать отдельную сущность Task и хрупкие якоря диапазонов. (ADR-011; locator производим при чтении `Note.Text`; тесты `TaskIndexServiceTests.Query_PagingSearchCancellationAndDeletedNotes`, `MarkdownTaskNavigatorTests`)
- [x] Поддержать дату в тексте, например `@2026-10-01`, с однозначным parser contract. (`MarkdownTaskParser`: token `@YYYY-MM-DD`, invariant Gregorian, первая валидная дата; тесты `MarkdownTaskParserTests.Dates_*`)
- [x] Локальные уведомления Windows работают без SMTP и облачного сервиса. (ADR-012: `TaskReminderScheduler` + `WindowsToastAdapter` / `RecordingToastAdapter`; due 09:00 локально; ledger без plaintext; тесты `TaskReminderSchedulerTests`, `RemindersSmokeTests`)
- [x] Переход из задачи открывает исходную заметку и соответствующий checkbox. (`MarkdownTaskNavigator` + `MainViewModel.OpenTask`; дубликаты — exact line только при том же snapshot proof `Note.Text`, иначе fallback без чужой строки; тесты `MarkdownTaskNavigatorTests`, `MarkdownTaskNavigationUiTests`)
- [x] Защищённая заметка не раскрывает текст задачи в уведомлении без явного разрешения. (индекс не сканирует protected; toast/ledger/логи без секрета; тесты `ReminderProtectionTests`, `TaskIndexServiceTests.Query_ProtectedNote_ZeroPlaintextInIndex`)
- [N] Email-напоминания не входят в продукт.
- [D] Повторяющиеся задачи и отдельная task-модель — только после наблюдаемого спроса.

Критерий Release 1.3: пользователь видит незакрытые checkbox по базе и получает безопасные локальные напоминания без дублирования данных.

## 9.1. Release 1.4 «Пространство» — полноэкранная карточная работа

Нормативное продуктовое ТЗ: [`docs/ux/workspace-product-spec.md`](docs/ux/workspace-product-spec.md). Релиз меняет оболочку главного окна, но не создаёт второй формат заметки и не ослабляет существующие гарантии.

- [x] W0: утверждены продуктовая модель, границы, rollback и acceptance-сценарии.
- [x] W1: оболочка и обратимый переключатель «Доска / Список» без изменения Note/схемы/Sync. (`WorkspaceViewMode`, `WorkspaceShellTests`; focused 26/26; Codex `docs/work/REVIEW_W1_CODEX.md`)
- [x] W2: read-only адаптивная доска на существующих карточках, поиске, тегах, пагинации и сортировке. (`WorkspaceLayoutHelper` columns, shared `NoteCardItemTemplate`, `WorkspaceBoardTests`; focused 40/40; Codex `docs/work/REVIEW_W2_FIX2_CODEX.md`)
- [x] W3: фокусный режим на существующем `NoteEditorViewModel` с единым draft/commit/navigation contract. (`WorkspaceViewMode.Focus`, `EnterFocus`/`LeaveFocus`, `WorkspaceFocusTests`; focused 53/53; Codex `docs/work/REVIEW_W3_CODEX.md`)
- [x] W4: полноэкранный `F11`, сворачиваемая навигация, восстановление окна и tray/close contract. (`ToggleFullscreen`, `NavigationPanelState`, `WorkspaceFullscreenTests`; focused 67/67; Codex `docs/work/REVIEW_W4_FIX_CODEX.md`)
- [x] W5: локальные сохранённые рабочие виды как фильтры без копирования заметок и Sync. (`SavedWorkspaceView`, authoritative `IncludedTagIds`/`ExcludedTagIds` apply; `WorkspaceViewsTests`; Codex `docs/work/REVIEW_W5_FIX_CODEX.md`)
- [ ] W6: полная visual/accessibility/performance матрица и решение о default только после реального использования. Автоматизированная часть принята (`WorkspacePolishTests`, paging 50/1000-cap, default остаётся List; focused 89/89; Codex `docs/work/REVIEW_W6_CODEX.md`). Физическая 10k/DPI, неделя реального использования и решение о default — leftover.

Порядок строгий: один этап на одного внешнего исполнителя; следующий начинается после независимой приёмки предыдущего. Режим «Список» остаётся rollback-путём как минимум до W6. Свободный canvas, координаты карточек, WebView2/WYSIWYG и новый тип Page в Release 1.4 не входят.

Критерий Release 1.4: пользователь работает с существующими заметками на полноэкранной доске, раскрывает карточку без потери draft, получает тот же результат поиска/фильтрации, не видит plaintext protected notes и может безопасно вернуться в режим «Список».

## 10. Обязательные архитектурные решения

### 10.1. Семантический поиск

Закрыто ADR-005 (направление B, 12.09.2026): подсистема удалена. FTS5 и структурный поиск — единственный production path. Внешний файл модели не является поддерживаемым состоянием.

### 10.2. KDF и криптографическая совместимость

Закрыто ADR-015 (формат/версионирование, 12.09.2026). Единый контракт `QuickNotes.App/Services/Crypto/KdfDescriptor.cs`:
канонический текст `PBKDF2-HMAC-SHA256|1|{iterations}|32`, строгий parse, bounds до derivation (min 1 local/cloud,
**10_000** archive как было, max **5_000_000**). Новые записи несут descriptor: `Note`/`NoteRevision`/`NoteAttachment`
(схема v14, nullable TEXT), QNAT v2, `SyncCryptoHeader.KdfDescriptor` (QNSP), QNBA v2. Legacy без descriptor читается
ровно со своими историческими N (заметка 120_000, sync/blob 100_000, архив 2_490_000 на create) и получает честный
статус «needs migration». Descriptor — метаданные, намеренно **не в AAD** (иначе сломалась бы byte-for-byte
совместимость); подмена меняет ключ и AEAD падает fail-closed. Архивный canonical header не менялся — он и раньше
нёс `kdfAlg`/`kdfVersion`/`kdfIterations` внутри AAD. Миграция выполняется только там, где аутентифицированная запись
идёт в одной транзакции (`ApplyUnlockedEdits`, change-password, rotation); внешние QNSP/QNBA/`.qnar` не переписываются
молча. Sync fingerprint (`SyncFingerprintHelper`) включает descriptor для note и attachment: legacy `NULL` даёт
исторический hash, а появление/смена descriptor — обнаружимое local modification (sync state остаётся честным при
миграции). QNBA v2 и QNAT v2 несут свой descriptor; QNAT v2 сверяется с descriptor заметки-владельца до AES-GCM.
Тесты: `KdfDescriptorEnvelopeTests`.

- [x] Версионировать параметры KDF в каждом note/sync/archive envelope. (ADR-015; `KdfDescriptor` + schema v14; QNAT/QNBA v2; QNSP header field; archive header уже был версионным; sync fingerprint включает descriptor)
- [x] Измерить безопасную стоимость на целевом оборудовании. (`docs/performance/kdf-cost-report-2026-09-13.md`: `QuickNotes.Tools.exe kdf`; текущие note 120_000 = 12.580 мс, sync 100_000 = 10.183 мс/envelope, archive 2_490_000 = 255.841 мс; experimental ~50/100/150 мс; sync scale 1+0/1/10/100; production N не менялся)
- [x] Ограничить кумулятивный KDF CPU недоверенных inbound cloud envelopes per-sync-cycle бюджетом. (`UntrustedInboundKdfWorkBudget` 20_000_000 заявленных итераций до PBKDF2; per-envelope max 5_000_000 и production N не менялись; не offline/auth)
- [x] Закрыть выбор KDF без небезопасной миграции. **Не принято:** raise PBKDF2 (experimental ~50 мс / 490_000 не default; low-end нет; 1+100 при 490_000 = 49_490_000 против budget 20_000_000; archive 2_490_000 уже ~250 мс). **Не принято:** Argon2id как production default. **Принято:** ADR-016 + изолированный spike `QuickNotes.Tools.exe argon2id` (не App). Константы 120_000 / 100_000 / 2_490_000 не менялись.
- [x] Сохранить чтение старых данных и реализовать постепенную миграцию при успешной аутентификации. (legacy read + `NeedsKdfMigration`; rewrite только на аутентифицированной записи внутри транзакции, прерывание откатывается — `NoteProtection_InterruptedMigration_RollbackLeavesLegacyEnvelopeByteForByte`)
- [x] Не менять криптографический формат одновременно с несвязанным UI-релизом. (только format/versioning этап; UI/DI/semantic не тронуты)

### 10.3. Composition root

Закрыто ADR-013 (12.09.2026): явный typed composition root без DI-контейнера. Production и isolated CLI собираются через `ApplicationCompositionRoot`. `MainViewModel` не создаёт Sync/S3/DPAPI/reminder infrastructure и не dispose чужие bundles. Import commit, editor, assembly, conflict и sync apply делят один `ILocalMutationCoordinator`. Recovery и UI делят один profile-scoped `BackupService`.

- DI-контейнер не добавлять, пока не сменён ADR-013.
- При использовании DI добавить прямую зависимость и тест composition graph.

### 10.4. Асинхронные границы

Закрыто ADR-014 (12.09.2026): `async void` только в `AsyncEventBridge`; пользовательские sync/backup/export/import/OCR имеют cancellation, timeout и различимый исход без сырого exception. Мутации после timeout не публикуют без ownership, не ждут вечно после дедлайна (`OwnedTerminalGrace` + `Cancelling`) и не становятся Success. OCR bounded even if implementation ignores cancel. Shutdown: один `TeardownThenDispose`. UI thread не блокируется ожиданием OCR, Sync или URL capture (URL probe — single-flight).

- Не возвращать `.Wait()`/`.Result` на интерактивный UI thread.
- Не добавлять fire-and-forget production work без владельца и наблюдения.
- DI-контейнер не добавлять (ADR-013).

## 11. Сознательно исключённое и отложенное

| Возможность | Решение | Условие пересмотра |
|---|---|---|
| WebView2 WYSIWYG | `[N]` | Двухнедельный spike после измеренного пользовательского ограничения и security review |
| HTML как основной формат | `[N]` | Только новая продуктовая стратегия и миграционный ADR |
| Email-напоминания | `[N]` | Не пересматривать без отдельного продукта/сервиса |
| Прямой импорт OneNote | `[N]` | Только если HTML/Markdown реально не покрывает перенос |
| Полноценная модель задач | `[D]` | Checkbox-подход доказан недостаточным на реальном использовании |
| Совместная работа в реальном времени | `[D]` | После стабильного single-user Sync и подтверждённого спроса |
| Мобильный клиент | `[D]` | После открытого формата, стабильного API данных и Release 1.3 |
| Плагины/скрипты | `[D]` | После формальной модели разрешений и стабильного формата данных |

## 12. Матрица обязательной проверки релиза

Перед каждым релизом:

1. Чистый Release build.
2. Полный тестовый набор на чистом процессе с timeout.
3. `git diff --check` и отсутствие секретов/пользовательских данных в diff.
4. Миграция поддерживаемых баз и контрольное восстановление backup.
5. Целевые тесты защиты, вложений, истории, импорта и Sync.
6. Ручная визуальная матрица затронутых экранов: темы, ширина, DPI, клавиатура.
7. Проверка быстрого захвата из нескольких реальных приложений.
8. Для изменения Sync/crypto/archive — реальный стенд и обновлённый протокол.
9. Проверка производительности на фиксированном наборе, если затронуты загрузка, список, поиск или редактор.
10. Release notes: реализовано, не реализовано, известные ограничения, формат/миграции и путь отката.

Релиз запрещён, если известен сценарий тихой потери данных, невозможно восстановить поддерживаемый backup или документация обещает непроверенную гарантию.

## 13. Ближайший план реализации

1. Закрыть и зафиксировать отдельно все незавершённые несвязанные изменения; начать workspace с чистого worktree.
2. W1: ввести оболочку и переключение Доска/Список, сохранив нынешний режим без функциональной регрессии.
3. W2: подключить read-only доску к существующим `SearchService`, paging и `NoteCardViewModel`.
4. W3: подключить существующий `NoteEditorViewModel` и доказать отсутствие потери draft/двойного commit.
5. W4: добавить полноэкранный режим только после устойчивой Board ↔ Focus навигации.
6. W5: сохранённые виды оставить локальными до отдельного решения о переносе между устройствами.
7. W6: после полной матрицы и реального использования решить, становится ли Доска режимом по умолчанию.

Каждый этап выполняется внешней моделью отдельным bounded brief и принимается Codex независимо. Несвязанные cleanup, crypto, sync и data-format изменения в workspace-инкременты не смешиваются.

## 14. Следующее действие

Автоматизированный W6 принят; чекбокс W6 остаётся открытым. Следующее — ручной leftover: неделя реального использования List/Board, физическая 10k/DPI-матрица, затем человеческое решение, становится ли Доска default. Не делать Доску default и не удалять Список. Не менять Note/схему/Sync/crypto.
