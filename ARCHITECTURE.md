# ARCHITECTURE.md

> Изъявительное наклонение. Только проверяемые факты и ссылки на исходники.
> Версии схемы, пакетов и счётчики строк здесь не дублируются: они живут в коде и меняются вместе с ним.
> Нормативные «должен» — в [`INVARIANTS.md`](INVARIANTS.md). Продуктовый выбор — в [`VISION.md`](VISION.md). План работ — в [`ROADMAP.md`](ROADMAP.md).

## 1. Что это за система

QuickNotes — локальное Windows WPF-приложение (`QuickNotes.App/QuickNotes.App.csproj`: `OutputType` WinExe, `UseWPF`, `UseWindowsForms`, `TargetFramework` `net8.0-windows10.0.19041.0`).

Прямые NuGet-зависимости приложения:

- `AWSSDK.S3`
- `Microsoft.EntityFrameworkCore.Sqlite`

Тестовый проект: `QuickNotes.Tests/QuickNotes.Tests.csproj` (xUnit). Консольный измеритель PBKDF2 и изолированный Argon2id spike: `QuickNotes.Tools/QuickNotes.Tools.csproj` (`OutputType` Exe). Решение: `QuickNotes.sln`.

## 2. Процесс и состав приложения

Точка входа UI-процесса: `QuickNotes.App/App.xaml.cs`, тип `QuickNotes.App.App`. Точка входа локальных измерителей: `QuickNotes.Tools/Program.cs` (`QuickNotes.Tools.exe` без аргументов — archive PBKDF2; `kdf` / `--benchmark-kdf` — note/sync/archive-reference, `Pbkdf2CostBenchmarkHost`; `argon2id` / `--spike-argon2id` — изолированный Argon2id spike ADR-016, не production KDF; `cold-start-r2r` — isolated publish-time ReadyToRun cold start, не запуск из UI; `scroll-fps-parse` — разбор уже снятого PresentMon CSV, не `CompositionTarget.Rendering`; `encrypt-credentials` — DPAPI-шифрование plaintext JSON credentials для CI, тот же entropy что `DpapiS3CredentialsStorage`).

При `OnStartup` по порядку:

1. `SingleInstanceService` — вторичный процесс сигналит первичному и завершается.
2. `StartupProfilePreparation.Prepare` создаёт разрешённый production или isolated каталог профиля до любого integrity/recovery/SQLite доступа. Невалидный isolated path и ошибка создания/доступа — fail-closed, не поток «повреждённая БД».
3. `DatabaseRecoveryService.IsDatabaseCorrupt` / `PerformRecovery` при повреждённом файле БД. Если файл уже существовал и проба вернула «не повреждён», `DbInitializer.Initialize(..., integrityAlreadyVerified: true)` не повторяет `PRAGMA integrity_check` на том же файле (`CanReusePriorIntegrityCheck`). Отсутствующий/пустой файл и любой corrupt/recovery-путь проходят собственную проверку в `Initialize`.
4. `DbInitializer.Initialize` с callback `BackupService.CreateBackup` перед апгрейдом схемы, если есть пользовательские данные. На уже текущей схеме пропускаются `EnsureCreated` и проход `ApplySchemaUpgrades`. Пересборка FTS пропускается только если триггеры на месте, в `NotesFts` нет дубликатов `NoteId`, нет строк защищённых заметок, и двусторонний `EXCEPT` подтверждает построчное равенство `(NoteId, coalesce(Title,''), Text)` с незащищёнными `Notes`; любое расхождение вызывает полный DELETE+INSERT FTS. Диагностика пропуска — возврат `DbInitializeOutcome`, не процесс-глобальные флаги.
5. `ApplicationCompositionRoot.CreateForStartup` создаёт typed composition graph и `MainViewModel`; показ `MainWindow` (если не `StartMinimizedToTray`).
6. `MainViewModel.StartSyncScheduler()` (не в isolated CLI). `MainViewModel.StartTaskReminders()`.

Изолированный `--perf-startup` пишет фазы `QN_PERF_PHASE` только в этом режиме; сигнал готовности по-прежнему `SearchBox.Focus` на `DispatcherPriority.Input`. ReadyToRun не включается в обычный `dotnet build` output (`QuickNotes.App.csproj` без `PublishReadyToRun`). Сопоставимые framework-dependent `win-x64` publish кладутся только в уникальный `artifacts/publish/cold-start/<run-id>/{baseline,r2r}` (или temp); copy-back в `bin`/`obj` запрещён. Оркестрация: `scripts/Measure-ColdStartReadyToRun.ps1` и `QuickNotes.Tools.exe cold-start-r2r`. Замер 2026-09-13: R2R p95 = 1385,7 мс на 10 samples (`docs/performance/cold-start-r2r-report-2026-09-13.md`). Opt-in физический FPS списка: `scripts/Measure-ScrollFpsPresentMon.ps1` (локальный PresentMon*.exe, isolated profile, bounded timed capture); пункт ROADMAP §6.5/§6.7 без реального capture не закрыт.

При `OnExit`: wipe protection sessions, затем один teardown-владелец `ApplicationCompositionRoot.TeardownThenDispose` (`LifecycleShutdown.WaitOwned`, ADR-014) — граф не Dispose, пока shutdown ещё использует зависимости.

Локальные пути по умолчанию (`Environment.SpecialFolder.LocalApplicationData` + `QuickNotes`):

- БД: `QuickNotesDbContext.GetDefaultDbPath()` → `quicknotes.db`
- настройки: `SettingsService` → `settings.json`
- backup: один profile-scoped `BackupService` (`CreateProfileBackupService`) для recovery и UI → подкаталог `Backups`
- журнал: `ErrorLogService.LogFilePath` → `Logs/errors.log`

## 3. Composition root

Точка сборки: `QuickNotes.App/Composition/ApplicationCompositionRoot.cs` (ADR-013). DI-контейнера нет: шесть typed subsystem bundles (`CoreNotesServices`, `CaptureServices`, `SyncCloudServices`, `SecurityServices`, `TasksRemindersServices`, `UiHostServices`). Public constructor `MainViewModel` принимает только эти bundles (`MainViewModelPublicConstructorParameterBound = 6`).

`App.OnStartup` вызывает `ApplicationCompositionRoot.CreateForStartup`. Isolated CLI (`--isolated-profile`) использует тот же контракт: каталог профиля, `RecordingToastAdapter`, `UnavailableCloudTransportFactory`, без регистрации hotkey и без обращения к `%LocalAppData%\QuickNotes`.

Ленивый production Sync/cloud граф живёт в `SyncCloudServices` (не в `MainViewModel`): `ICloudTransportFactory` создаёт `S3ObjectStoreTransport` или `UnavailableCloudObjectStoreTransport`; engine, usage, cleanup, retention, rotation и connection coordinator разделяют один transport до `ResetAfterSettingsChange`. Один `ILocalMutationCoordinator` (editor / import commit / assembly / conflict / sync apply), один `ISyncScheduler`, один `ISyncClock`, один `SettingsService` и один profile path на граф. `ApplicationCompositionRoot` владеет Dispose инфраструктуры; `MainViewModel` не dispose чужие bundles.

`SettingsViewModel` получает required backup/secret stores и уже созданные sync/cloud/reminder services и `ICloudTransportFactory`; не создаёт DPAPI stores, `BackupService` или `new S3ObjectStoreTransport`.

Асинхронные границы (ADR-014): `AsyncEventBridge` для WPF/`ICommand`; `BoundedOperation` для пользовательских sync/backup/export/import/OCR (мутации: конечный grace, затем isolate/`Cancelling`, без позднего Success; `DrainCompleted` на UI context; ручной sync с lease, без позднего apply; OCR — abandon+observe); `LifecycleShutdown.WaitOwned` только на teardown. UI thread не ждёт `.Result` / `.Wait` этих операций. Захват URL — async, single-flight.

Тесты: `QuickNotes.Tests/CompositionRootTests.cs`, test-only helper `MainViewModelTestComposition.Create`.

## 4. Данные

### 4.1. SQLite

EF Core модель: `QuickNotes.App/Data/QuickNotesDbContext.cs`.

Сущности: `Note`, `Tag`, `TagSynonym`, `NoteTag`, `NoteRevision`, `NoteAttachment`, `NoteTemplate`, `NoteTemplateTag`, `SyncEntityState`, `SyncDeviceState`, `SyncLocalState`, `SyncConflictRecord`. Отдельной сущности Task нет: открытые checkbox читаются из `Note.Text` (`MarkdownTaskParser` / `TaskIndexService`, ADR-011).

Инициализация и миграции: `QuickNotes.App/Data/DbInitializer.cs`. Текущая схема — константа `DbInitializer.CurrentSchemaVersion`. Поддерживаемый входной `PRAGMA user_version` для апгрейда заполненной базы проверяется тестами `SchemaMigrationAndRecoveryTests` как диапазон `0` … `CurrentSchemaVersion`.

FTS5-таблица `NotesFts` создаётся в `DbInitializer.Initialize`. Триггеры не индексируют строки с `Notes.IsProtected = 1`.

### 4.2. Файлы вложений

`AttachmentStorageService` хранит файлы рядом с БД (управляемые имена, SHA-256). Для защищённых заметок на диске используются контейнеры через `ProtectedAttachmentFile` / `NoteProtectionService`.

### 4.3. Настройки и секреты

- Обычные настройки: JSON `settings.json` (`SettingsService`).
- S3 credentials: `DpapiS3CredentialsStorage` (DPAPI текущего профиля Windows).
- Пароль sync: `DpapiSyncPasswordStorage`.
- Пароли отдельных заметок в настройках и SQLite plaintext не хранятся; в процессе живут в `ProtectedNoteSessionStore`.

## 5. Пользовательские сценарии в коде

| Сценарий | Где живёт |
|---|---|
| Глобальный hotkey | `GlobalHotkeyService`, регистрация из `App` и обработка в `MainViewModel` |
| Захват выделения | `ClipboardCaptureService` + `Win32Helper.SendCtrlC` |
| OCR области экрана | `ScreenCaptureService`, `WindowsOcrService`, `ScreenOcrCoordinator`, `OcrWindowVisibilityCoordinator` |
| Список / фильтры / FTS | `SearchService`, `MainViewModel` |
| Виртуальный индекс задач | `MarkdownTaskParser`, `TaskIndexService`, `MarkdownTaskNavigator`; раздел `NavigationSection.Tasks` |
| Локальные напоминания | `TaskReminderScheduler`, `WindowsToastAdapter` / `RecordingToastAdapter`, ledger `reminder-ledger.json`; due 09:00 локально, пока процесс жив (ADR-012) |
| Редактор заметки | `NoteEditorViewModel`, `NoteEditorWindow.xaml`; текст — `Note.Text` |
| Markdown-предпросмотр | `MarkdownPreviewService.BuildFlowDocument` → WPF `FlowDocument` |
| Теги, синонимы, правила | `TagDetectionService`, `TagRuleService`, `TagMergeService`, … |
| История | `NoteHistoryService` |
| Шаблоны | `NoteTemplateService`, `TemplateExpansionService` |
| Защита паролем заметки | `NoteProtectionService`, `NoteCryptoService` |
| Импорт/экспорт открытых данных | `NoteExportService`, `NoteImportService`, `NoteArchiveService` |
| Сборка быстрых заметок | `NoteAssemblyService`, UI `NoteAssemblyWindow` / `NoteAssemblyViewModel` |
| Зашифрованный recovery-архив | `EncryptedArchiveService` (create + dry-run + restore/publish + `RotateRecovery`); `RecoveryKeyOfflineCopy`; CLI `--restore-archive`; UI создания/ротации `.qnar` + обязательный RK dry-run (`ImportExportViewModel`); измерение PBKDF2 — `QuickNotes.Tools.exe` |
| Sync | `SyncEngine` и соседние типы в `QuickNotes.App/Services/Sync/` |

WebView2 в проекте не подключён: нет package reference и нет упоминаний в `.cs`/`.xaml` приложения.

## 6. Защита заметки (факт реализации)

`NoteCryptoService`: AES-256-GCM, PBKDF2-HMAC-SHA256, константы `KdfName`, `CurrentFormatVersionConst`, `DefaultIterationsConst`, размеры salt/nonce/tag/key.

Параметры конверта заметки лежат на `Note` / `NoteRevision` / `NoteAttachment` (`ProtectedFormatVersion`, `ProtectedKdfIterations`, `ProtectedKdfDescriptor`, salt/nonce/tag/ciphertext, `ProtectedOriginalSyncId` на `Note`).

Единый контракт KDF — `Services/Crypto/KdfDescriptor.cs` (ADR-015): канонический текст `PBKDF2-HMAC-SHA256|1|{iterations}|32`, `TryParse`/`Validate` с bounds до derivation. Новые записи несут descriptor (схема v14); legacy с `NULL` читается точно со своим `ProtectedKdfIterations` и получает статус «needs migration» (`INoteProtectionService.NeedsKdfMigration` / `InspectKdfEnvelope`), а rewrite идёт только внутри аутентифицированной записи. Стоимость PBKDF2 не изменена (note 120_000, sync 100_000). Argon2id в App нет; оценка — ADR-016 / `QuickNotes.Tools.exe argon2id`.

Файлы вложений `ProtectedAttachmentFile` имеют QNAT v2 (`QNAT,0x02,algId,descriptorVersion,iterations,saltLen,salt,nonce,tag,size,ciphertext`); v1 читается без изменений. AAD заметки (`QNNOTE|fmt|syncId|objectType`) не изменён: descriptor — метаданные, не в AAD. Расшифровка QNAT v2 получает уже производный ключ, поэтому descriptor контейнера сверяется с descriptor заметки-владельца **до** AES-GCM (`DecryptContainer`): несовпадение/неизвестный descriptor — отказ до расшифровки. Sync fingerprint (`SyncFingerprintHelper`) включает descriptor для note и attachment; пустой/`NULL` descriptor сохраняет исторический hash.

Сессии разблокировки: `ProtectedNoteSessionStore` (память процесса). Смена сессии затирает предыдущий ключ (`SessionStore_Add_WipesReplacedSessionKey`).

## 7. Sync (факт реализации)

Транспорт по умолчанию: `S3ObjectStoreTransport` (AWS SDK, совместим с S3-API; UI описывает Yandex Object Storage). PUT не выставляет пользовательские `x-amz-meta-*`; публичная модель `StorageObjectMetadata` отдаёт только Key / ETag / ContentLength / LastModified / ContentType. Сообщения провайдера и URL sanitizes `CloudErrorSanitizer` (`QuickNotes.App.Services`): секретные query-параметры и фрагменты подписи, без выкидывания безопасного контекста. Timeout/offline маппинг сохраняет inner SDK/network cause; `CloudStorageException.ToString` и `ErrorLogService.Write(Exception)` (цепочка inner, без зависимости от `Services.Sync`) не пишут ключи/подписи. Офлайн-доказательство: `S3ObjectStoreTransportTests`, `S3RedactionAuditTests`. Шаблон sanitized stand-протокола — `scripts/CloudRunProtocol.ps1` (opt-in манифест раннера); он не заполняет фактический журнал живого стенда. Не закрыто живым SDK: фактические response `x-amz-meta-*` и заполненный живой журнал стенда (ROADMAP §5.5).

Пакет: `SyncPackageExporter` / `SyncPackageImporter`, конверт `SyncPackageEnvelope` с полями `Crypto.KdfAlgorithm`, `KdfVersion`, `KdfIterations` и `Crypto.KdfDescriptor` (ADR-015). `SyncCryptoService.DefaultIterations` задаёт значение для *новых* sync-конвертов. AAD пакета (`SyncPackageEnvelope.GetAssociatedData`) не включает descriptor — byte-for-byte legacy-совместимость.

Входящие недоверенные QNSP/QNBA одного `RunSyncCycleAsync` делят кумулятивный KDF-бюджет **20_000_000** заявленных итераций (`UntrustedInboundKdfWorkBudget`), резерв до PBKDF2. Per-envelope max остаётся 5_000_000. Локальный encrypt/push в бюджет не входит. Исчерпание — `SyncCycleResult.IsKdfWorkBudgetExceeded`, не offline/auth. `CloudQuotaException` (квота/переполнение store) — `SyncCycleResult.IsQuotaExceeded`, не offline; `SyncScheduler` не ставит network backoff.

Вложения в облаке: `SyncAttachmentBlobService` (отдельные blob + AEAD). QNBA v2 несёт descriptor («magic + version + algId + descriptorVersion + iterations + saltLen + salt + nonce + tag + size + ciphertext»); v1 читается с N сервиса как раньше. AAD blob (`QNBA|1|sha256`) не изменён. Для inbound-пакета claimed KDF вложений резервируется до apply метаданных.

Конфликты: `SyncConflictService` (разрешение внутри `ILocalMutationCoordinator.ExecuteBulkMutationAsync` и одной SQLite-транзакции после stale/type/payload проверки), UI `SyncConflictsWindow` / `SyncConflictsViewModel`. Keep Local / Keep Remote / Keep Both / ручной Markdown-merge; заголовок и теги merge выбираются явно (Local или Remote), вложения не объединяются. Keep Both — детерминированный `SyncId` и заголовок ` (облако)`; идемпотентный повтор принимается только при `KeepBothCopy` revision и совместимом remote snapshot, иначе конфликт остаётся открытым. Защищённые версии без фиктивного plaintext. Смена выбранного конфликта отменяет предыдущую загрузку details; устаревший success/error не применяется к текущему элементу. CLI `--sync-conflict-smoke` → `artifacts/sync-conflict-acceptance/`. ADR-010.

Автозапуск циклов: `SyncScheduler`.

Автоматические тесты Sync идут против in-memory/fault-injecting store (`FaultInjectingCloudObjectStoreTransport`, `SyncEngineTests`, `SyncFailureMatrixTests`, `S3ObjectStoreTransportTests` и др.). Карта сценариев: [`docs/sync-offline-failure-matrix.md`](docs/sync-offline-failure-matrix.md). Прогона на реальном бакете и двух Windows VM в репозитории нет; ROADMAP §5.5 остаётся открытым.

## 8. Экспорт и архив (факт реализации)

- `NoteExportService.ExportToMarkdown`: открытый snapshot в выбранный каталог (staging + атомарная замена). Только незащищённые, не удалённые заметки; YAML с заголовком из первой строки, тегами, ссылками, источником; копируются доступные вложения; `manifest.json` формата `quicknotes-open-export` (версия, UTC, счётчики, пропуски, ошибки, SHA-256 файлов). Ошибка публикации не удаляет предыдущий snapshot.
- `NoteExportService.ExportToJson` / `ExportToCsv`: незащищённые, не удалённые заметки, без бинарных вложений.
- `NoteImportService`: Markdown/txt/JSON как раньше; HTML/HTM конвертируются офлайн в Markdown (`HtmlToMarkdownConverter`) и не сохраняются как HTML. Предпросмотр строит plan (`PlanHash`) без записи и без mutation coordinator. Сопоставление каталога: корень → тег, непосредственный подкаталог → дочерний тег (`ImportFolderMapper`). Неподдерживаемые файлы (включая Word/OneNote) попадают в диагностику без разбора. Commit идёт через тот же `ILocalMutationCoordinator.ExecuteBulkMutation`, что editor/assembly/conflict/sync apply, и внутри него — одна SQLite-транзакция с private staging; откат удаляет только вложения с `AttachmentSaveResult.WasCreated`. Идемпотентность по `Notes.ImportSourceFingerprint` (путь + SHA-256 содержимого, без timestamp; схема v13). Дубликаты: Skip / Import Separate / Replace (replace только тот же логический источник, незащищённая заметка, полное согласование тегов/вложений).
- `NoteAssemblyService`: сборка существующих заметок в одну новую Markdown-заметку. Preview (`PlanHash` + snapshot `UpdatedAt`/SHA-256 + канонический union тегов результата) не пишет БД. Итоговый текст: ATX H1 заголовка, тела с `\n`, разделитель пустая строка / `---` / `***` / custom. Теги — union неподавленных тегов исходников без автодетекта; расхождение TagId/Origin/имени после preview отклоняет commit. Финальная revalidation и SQLite-транзакция идут внутри существующего `ILocalMutationCoordinator.ExecuteBulkMutation` (тот же coordinator, что у редактора/Sync). Enqueue Sync и reload — только после успешного commit. По умолчанию исходники не меняются; корзина исходников только с отдельным acknowledgement. Защищённые исходники блокируют preview и commit без plaintext. Повтор всегда создаёт новую заметку. Каталог исходников — поиск по title/text (FTS + exact) и страницы по 50, без загрузки всего корпуса в ComboBox; защищённые только нейтральной подписью. UI: `NoteAssemblyWindow`; CLI `--quick-note-assembly-smoke` → `artifacts/quick-note-assembly-acceptance/`.
- UI: `ImportExportViewModel` / `ImportExportWindow.xaml` — выбор каталога, предупреждение о plaintext, показ пути/UTC/полноты последнего успешного открытого экспорта (`AppSettings.LastOpenExport*`). Вкладка импорта: сопоставление папок, потери, подтверждение потерь, разрешение дубликатов, клавиатурные `AutomationProperties`. CLI `--import-migration-smoke` пишет `artifacts/import-migration-acceptance/`. Вкладка «Зашифрованный архив»: новый `.qnar`, отдельный пароль в `PasswordBox`, create с shipping default N **на фоне (не UI thread)**, показ recovery key один раз, офлайн-копия (печать / новый файл с checksum, confirm, не в каталог `.qnar`, без clipboard), ротация recovery-wrap существующего `.qnar` (confirm, async), last-success (`AppSettings.LastEncryptedArchivePath` / `Utc`) только после dry-run с RK. Close/cancel недоступны, пока операция busy. UI не зачищает temp/staging по маске — это делает только `EncryptedArchiveService` / `RecoveryKeyOfflineCopy` для своих файлов. Компактный баннер того же last-success — в `SettingsWindow` (секция резервных копий). Пароль архива и RK в настройки не пишутся.
- `NoteArchiveService`: zip-формат `quicknotes-archive`, `ManifestSchemaVersion`; в экспорт не входят `IsProtected` заметки (`skippedProtected` в сервисе). Это plaintext ZIP, не зашифрованный recovery. Preview архива не пишет и не берёт coordinator. Commit (`ImportArchive`) идёт через тот же `ILocalMutationCoordinator.ExecuteBulkMutation`, что editor/миграция/assembly/conflict/sync apply, внутри — одна SQLite-транзакция; сбой или отмена не оставляют частичных notes/tags/attachments/history.
- `EncryptedArchiveService` (каталог `Services/EncryptedArchive/`): контейнер `quicknotes-encrypted-archive` (`.qnar`, magic `QNAR`/`QNAP`), AES-256-GCM, два wrap-слота (пароль архива + recovery key), PBKDF2 с явным N в header и shipping default `DefaultPbkdf2IterationsForNewArchives` (2_490_000) только для create без явного N. Create публикует новый файл через sibling temp (`FileStream.Flush(true)` + `File.Move`). Restore: тот же dry-run (AEAD/manifest/checksum/path), затем запись только в новый отсутствующий или пустой каталог через sibling staging; `snapshot/quicknotes.db` → `quicknotes.db`, `attachments/` как на диске; Flush + `PRAGMA integrity_check` + `DbInitializer` до publish; `Directory.Move` staging в назначение. Сбой удаляет staging и не меняет destination/живую БД. Перед любой записью запрещены reparse на destination, staging и всей существующей цепочкой их предков; также запрещены traversal, ADS, overwrite и in-place живого профиля `%LocalAppData%\QuickNotes`. Headless restore CLI: тот же `QuickNotes.App.exe --restore-archive <file> --destination <dir>` и ровно один из `--password-stdin` / `--recovery-key-stdin` (секрет из stdin, не длиннее `MaxStdinSecretUtf8Bytes`, без `Trim` пробелов); `--dry-run` без записи; `App.OnStartup` вызывает `Shutdown(exitCode)` без `Environment.Exit` и без WPF UI/onboarding/sync. Локальный PBKDF2 benchmark — отдельный консольный `QuickNotes.Tools.exe` (`EncryptedArchivePbkdf2Benchmark` из App, dummy password/salt, без архивных данных); WPF App не принимает `--benchmark-archive-pbkdf2`. UI создания/ротации архива: вкладка импорта/экспорта + обязательный RK dry-run до last-success; офлайн-копия `RecoveryKeyOfflineCopy`; `RotateRecovery` меняет только recovery-wrap существующего `.qnar` (`File.Replace` после in-memory проверки нового ключа). Cloud credentials в payload v1 не входят. In-memory budget 512 MiB (`InMemoryBudgetBytes`). Canonical header архива уже несёт `kdfAlg`/`kdfVersion`/`kdfIterations` и не изменён этим этапом (ADR-015).

## 9. Поиск (факт реализации)

`SearchService` — FTS5 (`NotesFts`) плюс структурные операторы (`tag:`, `source:` и др.) и in-memory hits разблокированных защищённых заметок. Отдельного смыслового индекса нет (ADR-005, направление B). При старте `LegacyUnsupportedIndexCleanup` best-effort удаляет только известный leftover `semantic_index.db` в корне профиля.

`CountNotes` / `QueryNotes` выбирают страницу идентификаторов через SQL `LIMIT`/`OFFSET` / `COUNT(*)` и гидратируют сущности только для запрошенной страницы.

## 10. Тесты и CI

- xUnit, `QuickNotes.Tests/xunit.runner.json`: параллелизм сборок/коллекций выключен; `AssemblyInfo.cs` — `DisableTestParallelization`.
- Windows workflow: `.github/workflows/ci.yml` (restore → `dotnet format whitespace QuickNotes.sln --verify-no-changes --no-restore` → Release build `-warnaserror` → один полный `dotnet test` без category filter, hang timeout, TRX и category summary в `artifacts/ci/test-results`, artifact `if: always()`). Format gate — только C# whitespace по `.editorconfig`; style/analyzers, XAML, Markdown, JSON-фикстуры и артефакты не входят. LiveCloud — opt-in, обычный CI не задаёт `QUICKNOTES_LIVE_S3_*`. Локальная проверка workflow: `scripts/Invoke-CiReportingLocal.ps1`. Структурные правила CI: `WindowsCiWorkflowRules` + `WindowsCiWorkflowTests`. Локальный pack `scripts/Run-LocalAcceptancePack.ps1` сбрасывает `QUICKNOTES_LIVE_S3_*` и AWS key/profile/region/shared-credentials env только в процессе раннера (наследует child `dotnet test`); вызывающий shell не очищается. Валидный TRX с нулём executed tests — отказ.
- Cloud acceptance workflow: `.github/workflows/cloud-acceptance.yml` (`workflow_dispatch` only, не push/PR/schedule; `environment: cloud-acceptance`; secrets `S3_ACCESS_KEY_ID` / `S3_SECRET_ACCESS_KEY`, variables `S3_ENDPOINT` / `S3_BUCKET`; fail closed при отсутствии). Сценарии Smoke / Acceptance / All вызывают `scripts/Run-YandexCloudLiveSmoke.ps1` и `Run-YandexCloudLiveAcceptance.ps1` (не `dotnet test` в YAML); All — два вызова с `cloud-smoke.trx` / `cloud-smoke.md` и `cloud-acceptance.trx` / `cloud-acceptance.md`. Sanitized протокол — `scripts/CloudRunProtocol.ps1`; локальные ручные прогоны без `-SanitizedReportFileName` файлов не пишут. Credentials DPAPI-шифруются в `%RUNNER_TEMP%\qn-cloud-creds` через `QuickNotes.Tools.exe encrypt-credentials` и не входят в артефакты. Upload allowlist: только два TRX, два Markdown-отчёта и `category-summary.md` (без recursive glob), `if: always()`, retention 7d. Cleanup credentials `if: always()` — best-effort: hard cancellation/потеря runner может его пропустить; это же ограничение записано в протоколе. Офлайн-валидация: `scripts/Assert-CloudAcceptanceConfig.ps1`, `CloudAcceptanceWorkflowTests`, `CloudRunProtocolTests`, `EncryptCredentialsToolTests`. Первый запуск на GitHub не выполнен.
- Асинхронные границы: `BoundedOperation`, `AsyncEventBridge`, `LifecycleShutdown` (ADR-014).
- Общие свойства сборки: `Directory.Build.props` (`TreatWarningsAsErrors`, analyzers, `EnforceCodeStyleInBuild=false`). Стиль: `.editorconfig`. Единственный CI format-gate: `dotnet format whitespace QuickNotes.sln --verify-no-changes --no-restore`.

## 11. Что этим файлом не утверждается

Документ не обещает, что sync проверен на облаке, что восстановление `.qnar` проверено под отдельной учётной записью Windows, или что KDF архива измерен на целевом железе пользователя. Composition root описан в §3 и ADR-013. Это фиксируется в ADR и `INVARIANTS.md`.
