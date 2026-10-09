# ADR-013: явный typed composition root без DI-контейнера

- Дата: 2026-09-12
- Статус: Accepted
- Реализовано в коде: да
- Пересмотр: если появится измеренная потребность в контейнере (много host-процессов или плагины с изолированными lifetime)

## Контекст

`ROADMAP.md` §10.3: production Sync/cloud собирался лениво внутри `MainViewModel.GetOrCreateSyncEngine`, конструктор ViewModel принимал десятки optional-зависимостей и сам создавал DPAPI stores, S3 transport, reminder ledger/toast. Isolated CLI частично обходил это, но credentials/backup defaults могли указывать на живой профиль. DI-контейнер не требовался: граф известен статически.

## Решение

- **уже в коде**
  - `ApplicationCompositionRoot` (`QuickNotes.App/Composition/`) собирает production и isolated графы.
  - Зависимости передаются шестью typed bundles: `CoreNotesServices`, `CaptureServices`, `SyncCloudServices`, `SecurityServices`, `TasksRemindersServices`, `UiHostServices`.
  - Public constructor `MainViewModel` принимает только эти шесть параметров (`ApplicationCompositionRoot.MainViewModelPublicConstructorParameterBound`).
  - `SyncCloudServices` владеет ленивым engine/transport/usage/cleanup/retention/rotation/coordinator; `ICloudTransportFactory` (`S3CloudTransportFactory` / `UnavailableCloudTransportFactory`) убирает `new S3ObjectStoreTransport` из view-models.
  - Один `ILocalMutationCoordinator` на граф разделяется editor / import commit (миграция HTML/Markdown и portable archive) / assembly / conflict / sync apply. Preview импорта координатор не берёт.
  - Один scheduler, clock, `SettingsService` и profile path на граф. Startup recovery и UI делят один profile-scoped `BackupService` (`ResolveProfileDirectory` / `CreateProfileBackupService`).
  - `SettingsViewModel` получает required `BackupService` и secret stores; не создаёт production fallbacks.
  - Isolated host: profile-scoped DPAPI paths, `RecordingToastAdapter`, `UnavailableCloudObjectStoreTransport`, без регистрации global hotkey и без live network.
  - Test-only helper: `MainViewModelTestComposition.Create` / `SettingsViewModelTestComposition.Create` / `ImportExportViewModelTestComposition.Create` в `QuickNotes.Tests` (не production constructor).
  - Lifecycle: `ApplicationCompositionRoot` владеет bundles; `MainViewModel` отписывается и Stop/Cancel собственных операций, но не dispose `SyncCloudServices`, scheduler, reminders, coalescers. `App.OnExit` сначала dispose VM, затем root. Dispose root идемпотентен.
- **принятое направление** — не добавлять `IServiceProvider`, static locator или dictionary-of-object, пока ADR не сменён.
- **эксперимент** — контейнер только после отдельного review и прямой NuGet-зависимости.

## Альтернативы

- Microsoft.Extensions.DependencyInjection — отвергнуто для §10.3: новая зависимость и скрытый граф без выигрыша.
- Оставить optional constructor на `MainViewModel` — отвергнуто: production defaults продолжали бы создавать инфраструктуру в UI-типе.

## Последствия

Поведение UI, данных, sync protocol, crypto, notifications и search не меняется. Isolated CLI идёт через тот же composition contract.

## Проверки

`CompositionRootTests` (включая source guard `new LocalMutationCoordinator` в `ImportExportViewModel`), `ArchiveImportMutationBoundaryTests`, `LocalCommitSyncBoundaryTests`, sync/settings/reminder/import/assembly/protection regressions.
