# ADR-002: открытый экспорт, переносимый зашифрованный архив и recovery key

- Дата: 2026-09-10
- Статус: Accepted как *направление* этапа M3 (`ROADMAP.md` §5.4) **и как проверяемая security-модель** зашифрованного архива/recovery. Блокеры **B1, B2 и B4 закрыты** (§12).
- Реализовано в коде: открытый snapshot-экспорт и plaintext zip незащищённых данных; **format/crypto core `.qnar` (QNAR/QNAP), dry-run, restore/publish в новый каталог и headless CLI** (`EncryptedArchiveService`, `QuickNotes.App.exe --restore-archive`); **UI создания `.qnar`, обязательное контрольное восстановление recovery key, офлайн-копия (печать/новый файл) и атомарная ротация только recovery-wrap** (`ImportExportViewModel` / вкладка «Зашифрованный архив»; last-success только после dry-run с RK; create/dry-run/rotate/offline-write уводятся с UI thread через `Task.Run`, без зачистки temp по маске). Create публикует новый `.qnar` через sibling temp + `FileStream.Flush(true)` + `File.Move`. Ротация существующего файла: sibling temp + Flush + `File.Replace` после in-memory dry-run нового ключа; парольный wrap и payload ciphertext не меняются. Restore пишет sibling staging, Flush, `PRAGMA integrity_check`, `DbInitializer`, затем `Directory.Move`. Потолок размера v1 — консервативный in-memory budget (`EncryptedArchiveConstants.InMemoryBudgetBytes`, 512 MiB), ниже `int.MaxValue`. Recovery key генерируется при create и оборачивает DEK; shipping default PBKDF2 для *новых* архивов — `EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives` (измерение §4 / B4). Isolated-profile process smoke без живого `%LocalAppData%\QuickNotes` есть; отдельная учётная запись Windows — нет.
- Пересмотр: перед критерием выхода M3 (восстановление под отдельной учётной записью Windows вручную); повторно если эталонная машина/runtime сменятся до смены shipping N
- Связанные документы: [VISION.md](../../VISION.md), [ROADMAP.md](../../ROADMAP.md), [ARCHITECTURE.md](../../ARCHITECTURE.md), [INVARIANTS.md](../../INVARIANTS.md), [ADR-004](004-kdf-envelope-compatibility.md)

## Контекст

`VISION.md` §3 принцип 1 и §4.4 требуют: незащищённые данные переживают приложение через явный открытый экспорт; защищённые — только через документированный переносимый контейнер; recovery key отделён от пароля заметки; приложение не создаёт скрытых plaintext-копий защищённого содержимого; потеря DPAPI-профиля не уничтожает единственную переносимую копию.

Факт кода на дату review (см. `ARCHITECTURE.md` §8):

| Путь | Тип | Защищённые заметки | Переносимость без DPAPI |
|---|---|---|---|
| `NoteExportService.ExportToMarkdown` | каталог `quicknotes-open-export`, staging рядом с назначением + `PublishDirectorySnapshot` | пропускаются | да (plaintext) |
| `NoteExportService.ExportToJson` / `ExportToCsv` | `SafeWriteFile` (tmp рядом с файлом) | пропускаются | да |
| `NoteArchiveService` (`FormatName` = `quicknotes-archive`) | plaintext ZIP + `manifest.json`; дерево в `%TEMP%` (`Path.GetTempPath`) | пропускаются (`skippedProtected`) | да |
| `BackupService` | SQLite Online Backup в `%LocalAppData%\QuickNotes\Backups` | ciphertext как в живой БД; незащищённый текст как в живой БД | копия привязана к этой машине/пути, не crypto-архив |
| `DatabaseRecoveryService` | `PRAGMA integrity_check`, staging `.recovery_staging.tmp`, `File.Replace` | не AEAD | нет |
| `NoteCryptoService` / `ProtectedAttachmentFile` | AES-256-GCM, AAD `QNNOTE\|…`, PBKDF2 | да | пароль заметки |
| `SyncCryptoService` / `SyncPackageEnvelope` | AES-256-GCM, AAD из полей конверта `QNSP` | облачный пакет | пароль sync + DPAPI-хранилище пароля |
| `DpapiS3CredentialsStorage` / `DpapiSyncPasswordStorage` | DPAPI текущего профиля | секреты облака | **нет** — потеря профиля ломает чтение |
| `AttachmentStorageService.GetFullPath` | канонический путь внутри корня вложений | — | не контейнер архива |

В коде есть `QuickNotes.App/Services/EncryptedArchive/` (`EncryptedArchiveService`: create + dry-run + restore + `RotateRecovery`), `RecoveryKeyOfflineCopy`, headless вход `QuickNotes.App.exe --restore-archive` и UI создания/ротации `.qnar` с обязательным dry-run recovery key до last-success. `INVARIANTS.md` AR-S1…S20, S22…S25 закрывают связанные slice. AR-S21: automated isolated-profile process есть; ручной отдельный Windows-пользователь остаётся `[UNPROTECTED]`.

Существующий plaintext zip **нельзя** принять как M3-recovery: он режет protected, пишет дерево в общий temp и проверяет пути только строковым `IsSafeArchivePath` (нет канонического `GetFullPath` при extract). `ProtectedAttachmentFile` использует `%TEMP%\QuickNotes` для **рабочего** защищённого контура заметок; M3-архив этот путь **не** наследует.

Критерий выхода M3 (восстановление под отдельной учётной записью Windows руками) этим slice **не** закрыт. Автоматизированы in-process restore, headless CLI, isolated-profile process (подмена профиля процесса, не чужой SID) и ротация recovery-wrap.

## Решение

- **Уже в коде.** Открытый snapshot (`NoteExportService.ExportToMarkdown`): пользователь выбирает каталог, подтверждает plaintext, staging, атомарная замена, manifest, skip protected, доступные вложения, ошибка публикации не уничтожает предыдущий snapshot; UI показывает последний успех. Plaintext zip — `NoteArchiveService.ExportArchive` (тоже skip protected). Локальный SQLite-backup — `BackupService` / `DatabaseRecoveryService`.
- **Принятое направление и security-модель.** Версионируемый файл `.qnar`: AES-256-GCM, canonical header целиком в AAD; полезная нагрузка — снимок БД и файлы вложений **как на диске** (protected остаются `QNNOTE`/`QNAT`); отдельный пароль архива; recovery key только оборачивает DEK; dry-run аутентифицирует и валидирует до записи; restore v1 — только в новый путь. Без DPAPI как единственной опоры; без plaintext temp защищённого содержимого (A2). Пароль отдельной заметки recovery key и пароль архива не заменяют и не раскрывают.
- **Уже в коде (restore/CLI slice).** `EncryptedArchiveService.Create` / `DryRun` / `Restore`. Restore повторяет полный dry-run, затем пишет sibling staging рядом с назначением: `snapshot/quicknotes.db` → `quicknotes.db`, `attachments/` → `Attachments/` как на диске; каждый файл `Flush(true)`; `PRAGMA integrity_check` и `DbInitializer` до publish; публикация `Directory.Move` (пустой dest временно отводится). Сбой: staging удалён, destination и живая БД не меняются. Запрещены reparse (включая существующую цепочку предков destination и staging), traversal, ADS, overwrite, in-place `%LocalAppData%\QuickNotes`. CLI: `--restore-archive` + `--destination` + ровно один `--password-stdin` / `--recovery-key-stdin`; опционально `--dry-run`; секрет не в argv и не в логах, stdin ограничен `MaxStdinSecretUtf8Bytes`; без WPF UI/onboarding/sync (`Shutdown(exitCode)`, без `Environment.Exit`). Create: sibling `*.tmp`, `FileStream.Flush(true)`, `File.Move`. Shipping default PBKDF2 для новых архивов — B4 Accepted (§4, §12). Локальное измерение — консольный `QuickNotes.Tools.exe` (класс `EncryptedArchivePbkdf2Benchmark` из App; dummy password/salt; без архивных файлов). WPF `QuickNotes.App.exe` не содержит `--benchmark-archive-pbkdf2`.
- **Уже в коде (UI create + контрольный RK + офлайн-копия + ротация).** Вкладка «Зашифрованный архив» в `ImportExportWindow`: новый путь `.qnar`, пароль архива в `PasswordBox` (без binding на Text), create без явного N (shipping default). Recovery key показывается один раз с предупреждением сохранить отдельно и текстом, что он не заменяет пароль защищённой заметки. Офлайн-копия: явный confirm, печать того же payload или запись **нового** UTF-8 файла (`RecoveryKeyOfflineCopy`, sibling temp + Flush + `File.Move`, отказ если файл есть или путь в каталоге `.qnar`). Default путь — «Документы», не рядом с архивом. Автоматического clipboard нет. Ротация: существующий `.qnar`, ровно один текущий секрет (пароль или RK), confirm, `Task.Run` → `RotateRecovery`, показ нового ключа один раз, затем тот же dry-run. Last-success (путь/UTC в `AppSettings`, без пароля и без RK) пишется только после настоящего `DryRun` с `RecoveryKeyFormatted` и без пароля архива. Ошибка/отмена last-success не меняют (отмена после уже записанной ротации ключ в памяти сбрасывает, файл не откатывает). Секреты в памяти сбрасываются при отмене, успехе и закрытии окна. Компактный баннер last-success — в настройках (резервное копирование).
- **Эксперимент.** Нет. Не смешивать с WebView2, сменой KDF заметок/sync и облачным smoke M4.

Этот ADR **не утверждает**, что аварийное восстановление защищённых данных уже работает как продукт M3.

---

## 1. Модель угроз (проверяемая)

### 1.1. Активы

| ID | Актив | Где сегодня | Цель для архива |
|---|---|---|---|
| A1 | Plaintext незащищённой заметки, теги, метаданные источника | SQLite, открытый экспорт, plaintext zip | Внутри `.qnar` только под AEAD; снаружи — только если пользователь явно сделал открытый экспорт |
| A2 | Plaintext защищённой заметки и расшифрованные байты вложения | сессия процесса; не на диске после protect (`INVARIANTS` C1) | **Никогда** не появляется в архиве, temp, staging, manifest, логе |
| A3 | Ciphertext заметки/ревизии/вложения (`QNNOTE` / `QNAT`) | SQLite + файлы | Копируется **как есть**; после restore нужен пароль заметки |
| A4 | Пароль отдельной заметки | только память/ввод | Не входит в архив; не выводится из пароля архива или recovery key |
| A5 | Пароль архива | не существует | Только ввод пользователя; не единственная копия в DPAPI |
| A6 | Recovery key | не существует | Только офлайн (печать/файл по выбору пользователя); не облако plaintext, не `settings.json` |
| A7 | DEK архива | не существует | Только в RAM на время операции; на диске только в wrap-слотах |
| A8 | Пароль sync и S3 credentials | DPAPI-файлы | **Не входят в архив v1** (B1 Accepted: облачные секреты — M4) |
| A9 | Живая БД и каталог вложений профиля | `%LocalAppData%\QuickNotes` | Restore v1 их не затирает |
| A10 | Открытый snapshot-каталог | выбранный пользователем путь | Не используется как staging зашифрованного архива |

### 1.2. Акторы

| ID | Актор | Возможности | Что модель обязана выдержать |
|---|---|---|---|
| T1 | Кража/копия файла `.qnar` без пароля и без recovery key | читает байты файла | Нет plaintext A1/A2; нет DEK |
| T2 | Кража recovery key **или** пароля архива | один из двух секретов | Достаточно для DEK архива; **не** даёт пароли заметок (A4) |
| T3 | Злоумышленный файл «архива» | произвольные header/ciphertext/пути/длины | Fail-closed; лимиты KDF и размера **до** дорогого выделения памяти и **до** записи; нет traversal/symlink/zip-bomb вне назначения |
| T4 | Локальный злоумышленник с правами пользователя | диск профиля, `%TEMP%`, логи | Нет **дополнительных** plaintext-копий A2; опубликованный архив только AEAD |
| T5 | Наблюдатель облака / чужой бакет | объекты sync | Архив v1 не sync-пакет; recovery key не класть в облако |
| T6 | Потеря/сброс профиля Windows (DPAPI) | старые DPAPI blobs нечитаемы | Архив и recovery key на внешнем носителе всё ещё открываются |
| T7 | Оператор (ошибка restore) | неверный путь/пароль | Dry-run ничего не пишет; v1 не пишет поверх живой БД |
| T8 | Обрыв create/restore | частично записанные файлы | Сбой create до Move: нет final, temp удалён; существующий `.qnar` не перезаписывается. Назначение restore не остаётся полуписаным |

Вне скоупа v1 (не «разрешено», а «этот контейнер это не закрывает»): malware с правом читать память разблокированного процесса; cold-boot; принуждение отдать пароль; уже украденные **старые** файлы архива после ротации recovery key.

### 1.3. Границы доверия

1. **Процесс QuickNotes** — единственное место, где DEK и введённые секреты существуют в plaintext.
2. **Живой профиль Windows** — доверен для локальной работы; **не** доверен как единственное хранилище ключа восстановления.
3. **Выбранный пользователем путь `.qnar`** (USB, сеть, чужая папка) — враждебный носитель: файл могут подменить.
4. **Каталог restore (новый путь)** — создаётся приложением; существующее содержимое не перезаписывается молча.
5. **Офлайн-копия recovery key** — вне приложения; компрометация = компрометация всех архивов с этим wrap.
6. **Облако S3** — не хранилище recovery key и не контейнер M3 v1.
7. **Открытый экспорт** — другая граница: сознательный plaintext только незащищённых данных.

Каждая граница проверяется строкой матрицы §11 (актор + актив + ожидаемый отказ).

---

## 2. Версионируемый конверт v1

Имя формата: `quicknotes-encrypted-archive`. Расширение: `.qnar`.
Magic (ASCII, 4 байта): `QNAR`. `FormatVersion = 1`.

Не путать с plaintext `quicknotes-archive` (`NoteArchiveService`). Детектор: magic; файл без `QNAR` **отвергается** зашифрованным импортером без попытки «угадать» ZIP/JSON/`QNSP`.

### 2.1. Слои (логический)

```
[ файл .qnar ]
  framing + canonical header (не секрет; целиком в AAD)
  wrap-слот пароля архива (AES-256-GCM)
  wrap-слот recovery key (AES-256-GCM)
  nonce/tag + ciphertext полезной нагрузки (AES-256-GCM, ключ = DEK)
[ plaintext нагрузки после AEAD ]
  таблица записей QNAP (не ZIP): snapshot БД, вложения, payload-manifest
```

Полезная нагрузка **не** перешифровывает заметки. Create **не** требует разблокированных сессий и **не** вызывает decrypt заметки/вложения.

Не входят в v1: `semantic_index.db`, `Logs/`, каталоги открытого экспорта, DPAPI-файлы credentials/sync-пароля, пути вне корня `AttachmentStorageService`.

### 2.2. Canonical header (детерминированный, не JSON)

Порядок полей фиксирован; UTF-8; разделитель `|`; GUID формат `D`. Любой другой порядок/пробел — другой AAD.

```
QNAR|{formatVersion}|{archiveId:D}|{createdAtUnixUtc}|{kdfAlg}|{kdfVersion}|{kdfIterations}|{wrapCount}|{payloadContentType}
```

- `archiveId` — случайный Guid при создании (не SyncId заметки).
- `payloadContentType` для v1: `snapshot-v1`.
- `wrapCount` для v1: `2` (password + recovery). Несовпадение с фактическим числом слотов во framing — отказ до KDF.
- Числа — десятичная запись без ведущих нулей.
- `kdfAlg` / `kdfVersion` / `kdfIterations` относятся **только к парольному wrap** (PBKDF2). Recovery wrap — HKDF (§3.1), не эти поля.
- JSON/XML для AAD **запрещён**. JSON допустим только внутри уже расшифрованной нагрузки (`payload-manifest`).

### 2.3. Binary framing (little-endian)

Поля длины читаются **до** выделения буфера заявленного размера; при выходе за in-memory budget §8 — отказ.

| Смещение (логическое) | Поле | Размер |
|---|---|---|
| 0 | magic `QNAR` | 4 |
| 4 | `headerLength` | u16 |
| 6 | UTF-8 canonical header | `headerLength` |
| далее | wrap `password` | 32 salt + 12 nonce + 32 ct(DEK) + 16 tag = 92 |
| далее | wrap `recovery` | 92 байта в том же порядке (salt HKDF + nonce + ct + tag) |
| далее | `payloadLength` | u64 |
| далее | payload nonce | 12 |
| далее | payload ciphertext | `payloadLength` |
| далее | payload tag | 16 |

Иных TLV и «гибкого JSON вокруг ciphertext» в v1 нет. После tag EOF: лишние байты — отказ.

### 2.4. Нагрузка после AEAD: `QNAP`, не ZIP

Вложенный ZIP **запрещён** в v1: нет `ZipFile.ExtractToDirectory`, нет коэффициента сжатия как вектора. После decrypt — таблица файлов:

```
magic "QNAP" (4)
u32 entryCount
повторить entryCount раз:
  u16 pathLength
  UTF-8 relative path
  u64 fileSize
  32 байта SHA-256 содержимого
  fileSize байт содержимого
```

Разрешённые `relative path` только:

- `snapshot/quicknotes.db` — ровно один; содержимое = SQLite Online Backup (как `BackupService`);
- `attachments/` + относительный путь внутри корня вложений;
- `payload-manifest.json` — ровно один; счётчики, те же относительные пути, SHA-256, `user_version` снимка.

Дубликат пути, путь вне этих префиксов, `entryCount = 0` без snapshot — отказ. SHA-256 каждой записи сверяется с байтами **до** копирования в назначение.

---

## 3. Иерархия ключей и AES-GCM (аутентифицированные метаданные)

### 3.1. Ключи

| Ключ | Источник | Назначение |
|---|---|---|
| DEK | `RandomNumberGenerator`, 32 байта | AEAD полезной нагрузки |
| KEK_password | PBKDF2-HMAC-SHA256(UTF-8 пароль архива, salt_pwd 32, iterations из header) → 32 байта | AEAD wrap DEK |
| Recovery key material | `RandomNumberGenerator`, 32 байта | секрет пользователя |
| KEK_recovery | HKDF-SHA256(ikm=recovery key material, salt=salt_rk 32, info=`QNAR-wrap-recovery-v1`) → 32 байта | AEAD wrap DEK |

Пароль архива **протокольно** не является паролем заметки: другие salt, AAD, wrap. Тесты обязаны показать: успешный archive-decrypt не возвращает note password и не делает note ciphertext читаемым без note password — даже если пользователь ввёл ту же строку.

Два wrap-слота — независимые nonce. Успеха **одного** слота достаточно для DEK. Оба слота обязательны при создании: архив без recovery wrap в v1 некондиционен.

Алгоритм AEAD везде: AES-256-GCM, nonce 12, tag 16 — те же размеры, что `NoteCryptoService` / `SyncCryptoService`. Новый код архива **не** вызывает `NoteCryptoService.BuildAssociatedData` (префикс `QNNOTE`) и **не** подставляет `SyncPackageEnvelope.GetAssociatedData`.

Секреты в RAM после операции: `CryptographicOperations.ZeroMemory` для DEK/KEK (как sync crypto).

### 3.2. AAD = аутентифицированные метаданные

GCM tag покрывает ciphertext **и** AAD. Метаданные версии/KDF/типа нагрузки не «подписываются отдельно»: они входят в AAD. Подмена header при том же ciphertext/tag — отказ.

1. **Wrap DEK (оба слота):** AAD = UTF-8 canonical header + `|wrap|{slotId}` где `slotId` это `password` или `recovery`.
2. **Полезная нагрузка:** AAD = UTF-8 canonical header + `|payload|{contentType}|{payloadPlaintextLength}` где `payloadPlaintextLength` — десятичная длина **plaintext QNAP** (не ciphertext).

Длина plaintext в AAD отсекает усечение/расширение после подмены `payloadLength`.

Параметры KDF в header **не секретны** и **не секретны-аутентифицированы до** первого успешного wrap. DoS закрывается лимитами §4 **до** `Pbkdf2`, не MAC без ключа.

### 3.3. Ошибки

Неверный пароль, неверный recovery key, порча header/ciphertext/tag, подмена поля header → один класс отказа (аналог `NoteProtectionSecurityException`): без частичного plaintext, без записи в назначение, без различия «какой байт не совпал» в сообщении пользователю. Неверный *magic/formatVersion/framing* может быть отдельным сообщением **до** KDF, чтобы не гонять миллионы итераций на случайный ZIP.

---

## 4. KDF: независимый контур и границы

Контур архива **не** использует `NoteCryptoService.DefaultIterationsConst` (120_000) и `SyncCryptoService.DefaultIterations` (100_000) как fallback и **не копирует** их в shipping default. Отсутствие `kdfAlg`/`kdfVersion`/`kdfIterations` в header = невалидный v1. Согласовано с [ADR-004](004-kdf-envelope-compatibility.md).

Принято для archive v1:

| Правило | Значение |
|---|---|
| Алгоритм парольного wrap | только `PBKDF2-HMAC-SHA256` |
| `kdfVersion` v1 | `1` |
| Salt парольного wrap | ровно 32 байта |
| Итерации при **чтении недоверенного** файла | 10_000 ≤ N ≤ 5_000_000; иначе отказ **до** `Pbkdf2` и до аллокации payload по заявленной длине |
| Неизвестный `kdfAlg` / `kdfVersion` | отказ без перебора алгоритмов |
| Argon2id | **не** в M3; новый `kdfAlg` + новый review |
| Production default N для *новых* архивов | **B4 Accepted:** `DefaultPbkdf2IterationsForNewArchives` = **2_490_000**. Только create, если `EncryptedArchiveCreateRequest.Pbkdf2Iterations` не задан. Чтение всегда берёт явное N из header. |
| Цель стоимости | интерактивное desktop-приложение: ориентир **~250 мс** одной PBKDF2-HMAC-SHA256 (dkLen 32); допустимый измеренный диапазон **150–400 мс** на подтверждающем прогоне |
| Test hook | инъекция низкого N только в тестах (`EncryptedArchiveKdfLimits.ForTests`); не подменяет shipping default |

**Обоснование цели 250 мс.** Create и последующий password-unlock архива — редкие интерактивные операции, не горячий путь ввода заметки. ~250 мс даёт заметную стоимость offline-подбора пароля на той же реализации, не превращая открытие файла в многосекундный «логин». Диапазон 150–400 мс допускает шум измерения и не гонится за OWASP ~600k «на глаз»: на эталонной машине 600k было бы существенно дешевле цели. Note/sync константы (120k/100k) не переносятся.

**Измерение (не универсально).** Дата UTC **2026-09-10T09:21:15Z**. Машина: Windows 10.0.26200, 12 логических процессоров, `Intel64 Family 6 Model 151 Stepping 2, GenuineIntel`. Runtime: **.NET 8.0.23**. Метод: консольный `QuickNotes.Tools.exe` (тот же `EncryptedArchivePbkdf2Benchmark`, что используется сейчас; исторически измерение запускалось как скрытый switch WPF WinExe и не печатало отчёт в PowerShell) — dummy ASCII-пароль и фиксированная 32-байтная соль, без `.qnar` и без пользовательских секретов; `Rfc2898DeriveBytes.Pbkdf2` / SHA-256 / dkLen 32; 3 прогрева + 5 повторов; probe 50_000 итераций (медиана **5.015 мс**) → оценка N с округлением вниз до 10_000 и clamp в [10_000, 5_000_000]; подтверждение на **2_490_000**: медиана **255.167 мс**, min **243.591**, max **260.255**, внутри 150–400 мс. Это стоимость **этой** машины/runtime, не норматив для чужого железа. Повтор: `& .\QuickNotes.Tools\bin\Release\net8.0-windows10.0.19041.0\QuickNotes.Tools.exe`.

HKDF recovery: не имеет поля iterations в header; salt_rk ровно 32 байта; иное — отказ.

---

## 5. Пароли и recovery key

### 5.1. Три секрета

| Секрет | Открывает | Не открывает |
|---|---|---|
| Пароль заметки | конверт этой заметки (и её вложения) | архив, другие заметки, sync |
| Пароль архива | DEK → snapshot БД + файлы как на диске | plaintext защищённых заметок |
| Recovery key | тот же DEK | пароль заметки, строку пароля архива, пароль sync |

Подсказка пароля заметки остаётся UX живой БД; в архив копируются поля БД как есть. Recovery **не** reset пароля заметки (`INVARIANTS.md` C11).

### 5.2. Recovery key: выпуск, контроль, ротация, отзыв

- Генерация: 32 байта CSPRNG.
- Кодирование v1: Crockford Base32 без неоднозначных символов, группы по 4, плюс контрольная сумма SHA-256(key)[`0..3`] в той же азбуке. Печать и файл — один payload.
- Контрольное восстановление: dry-run с **только** recovery key (пароль архива не вводится) должен пройти, иначе настройка не завершена. Симметрично для пароля архива отдельно.
- Хранение приложением: **запрещено** (нет поля в `settings.json`, нет DPAPI-blob как единственной копии). Локальный файл — только если пользователь явно выбрал путь и подтвердил запись; это его офлайн-копия (`RecoveryKeyOfflineCopy`). Печать — тот же payload.
- Облако: запрет класть key или DEK в объект/metadata.
- Ротация **в коде v1**: новый recovery material → новый `salt_rk` / nonce → новый wrap-слот **того же** `.qnar`; парольный wrap, canonical header и ciphertext нагрузки **не меняются** (AAD wrap/payload не включает байты recovery-слота). Публикация атомарна (`File.Replace` после проверки нового ключа in-memory dry-run). Старый key **не** открывает обновлённый файл. Старые **копии** файла со старым wrap остаются открываемыми старым ключом — это неустранимо. UI это говорит. Сбой до/во время replace: исходные байты файла сохраняются.
- Отзыв без нового wrap на этом файле невозможен и не имитируется.

### 5.3. DPAPI

- Расшифровка `.qnar` **не** вызывает DPAPI.
- Restore в чистый профиль / другую учётную запись проходит с паролем архива **или** recovery key.
- Запрещено: единственная копия DEK или recovery key только в DPAPI.
- Позже (не v1): дополнительный третий wrap ключом профиля — никогда вместо password/recovery.

---

## 6. Запрет plaintext temp, staging, атомарная публикация

Следовать идее `NoteExportService.PublishDirectorySnapshot` / `SafeWriteFile` и `DatabaseRecoveryService` (проверка до replace). **Не** следовать `NoteArchiveService` (`Path.GetTempPath()` + дерево plaintext) и **не** использовать `%TEMP%\QuickNotes` из `ProtectedAttachmentFile`.

1. Защищённый plaintext (A2) не писать ни в `%TEMP%`, ни в staging, ни в `.tmp` рядом с назначением.
2. Нешифрованный SQLite snapshot и копии вложений (как в живом профиле: plaintext незащищённых + ciphertext защищённых) живут только во **внутреннем** staging на том же томе, что конечный `.qnar` или конечный restore-каталог; имя случайное; ACL не шире, чем у назначения.
3. После успешного AEAD внутренний snapshot удаляется; сбой — best-effort delete. Публикация нового `.qnar` = запись sibling `.{name}.{guid}.tmp` на том же томе + `FileStream.Flush(true)` + атомарный `File.Move` (без overwrite). Существующий final не заменяется: Create отказывает, если путь уже есть. Сбой до успешного Move: final отсутствует, temp удаляется. Replace предыдущего успешного файла **не** входит в этот slice.
4. Restore: AEAD, лимиты, SHA-256 и payload-manifest **до** появления файлов в назначении, кроме собственного staging под назначением. Назначение — пустой новый каталог; если путь существует и непустой — отказ.
5. Запрещены: расшифровать в `%TEMP%\*.zip` и `ExtractToDirectory`; следовать symlink/reparse при копировании вложений (только обычные файлы внутри корня `AttachmentStorageService`; иначе skip + ошибка, create неуспешен если обязательное вложение не упаковано).
6. Логи: нет паролей, ключей, содержимого recovery file, plaintext заметок.

---

## 7. Dry-run и restore в новый путь

| Режим | Пишет живую БД профиля | Пишет назначение | Требует AEAD OK |
|---|---|---|---|
| Dry-run | нет | нет | да |
| Restore v1 | нет | только новый пустой каталог (БД + attachments + отчёт) | да |

Dry-run отчёт: format version, archiveId, UTC, `user_version` снимка, число заметок/protected/вложений, пропуски. Логи не уточняют, какой слот сработал, если оба пробовались автоматически. UX может назвать введённый секрет.

Встроенный in-place `File.Replace` живого `quicknotes.db` **не входит в v1**. Переключение профиля на восстановленный каталог — отдельное явное действие вне этого ADR.

После restore защищённые заметки остаются locked; нужен прежний пароль заметки.

---

## 8. Злоупотребления (обязательная семантика отказа)

Лимиты v1 (отказ без записи в назначение; процесс остаётся жив). Реализация — целиком in-memory (`byte[]`); потолок **честно ниже** `int.MaxValue`. Поля формата остаются u64, но значения выше budget отвергаются до allocation и до `File.ReadAllBytes`.

| Лимит | Потолок v1 |
|---|---|
| Размер файла `.qnar` до чтения байт | 512 MiB (`InMemoryBudgetBytes`) |
| `headerLength` | 4 KiB |
| `payloadLength` / plaintext QNAP | 512 MiB |
| `entryCount` | 100_000 |
| длина одного relative path | 1 024 байт UTF-8 |
| один файл нагрузки | 512 MiB |
| вложенный ZIP / второй слой сжатия | глубина 0 (формат QNAP) |
| глубина каталогов в path | 32 сегмента |

| Класс | Правило |
|---|---|
| Path traversal | После нормализации `/` путь только под разрешёнными префиксами §2.4. `GetFullPath(dest)` строго внутри корня extract (суффикс `\` как в `AttachmentStorageService.GetFullPath`). Запрет `..`, абсолютных путей, UNC, `\\?\`, NTFS ADS (`:` в имени), ведущего `/`, `\\`. Одного `NoteArchiveService.IsSafeArchivePath` недостаточно. |
| Symlink / reparse | Pack: не следовать ссылке наружу из корня вложений; обнаружение reparse = ошибка create, если файл обязателен. Unpack: не создавать reparse; только обычные файлы; не следовать существующим symlink/junction в назначении и ни в одном существующем предке destination или staging. |
| Zip bomb / compression bomb | В v1 нет ZIP. Эквивалент: отказ, если заявленный `fileSize`/`payloadLength`/`entryCount` больше лимита **до** `new byte[]` и до записи. Не доверять сумме «как в манифесте» без сверки с framing. |
| Oversized input | Потолок файла `.qnar` **до** `ReadAllBytes` и **до** KDF. Не выделять буфер по враждебному u64 (в том числе `int.MaxValue` и 8 GiB). |
| KDF DoS | §4. |
| Tamper | Любое изменение magic/header/wrap/payload/tag/лишние байты → отказ; назначение не тронуто. |
| Wrong secret | Как tamper на уровне данных. UI: «неверный пароль/ключ или файл повреждён». |
| Rollback | Сбой create до Move: нет final, temp удалён; существующий `.qnar` не перезаписывается. Сбой restore удаляет staging; живая БД теста/профиля не меняется. |
| Confused deputy | plaintext `quicknotes-archive` ZIP, `QNSP`, Markdown-каталог экспорта — отказ `.qnar`-парсера без чужого KDF. |

---

## 9. Совместимость и crypto agility

- Читатель v1 понимает только `FormatVersion = 1`, `kdfAlg` v1 архива, framing §2.3, нагрузку `QNAP`. Больший `formatVersion` или неизвестный `kdfAlg` — отказ «нужна новая версия приложения», без skip AEAD.
- Смена AEAD/KDF/framing архива = новый `formatVersion` и/или `kdfVersion`; чтение старых файлов; upgrade только после успешной аутентификации; **не** в одном релизе с посторонним UI (`ROADMAP.md` §10.2).
- M3 **не** меняет конверт заметки и sync.
- Полезную нагрузку с `user_version` < текущей после restore поднимает существующий `DbInitializer` на **новом** пути; тест: снимок старой схемы внутри `.qnar` → restore → init → данные на месте.

---

## 10. Что сознательно не решает v1

- Обёртка пароля sync / S3 credentials recovery key (`VISION.md` §4.4 «облачная копия») — **M4**, не M3 v1 (B1 Accepted).
- In-place restore живого профиля (отложено; не блокирует v1 new-path).
- Отдельный крошечный `.exe` вне `QuickNotes.App` — **не в v1** (B2 Accepted: тот же `QuickNotes.App.exe --restore-archive`).
- Argon2id (**не** внедрять в M3; ADR-004).
- Несколько recovery key одновременно; escrow; threshold.
- Перешифровка заметок паролем архива.
- Вложенный ZIP как формат нагрузки.

---

## 11. Матрица обязательных security-тестов

Каждая `AR-S*` — метод в `QuickNotes.Tests.EncryptedArchiveRecoveryTests` либо `[UNPROTECTED]`, пока нет кода. Открытый экспорт (`OpenExportTests` / `ImportExportTests` / `PortabilityAndDailyTests.Archive_*`) **не** закрывает эти строки.

### 11.1. Покрытие тем review (тема → тесты)

| Тема | Норма | Тесты |
|---|---|---|
| Threat model / акторы T1–T8 | §1 | AR-S1, S2, S6, S7, S12–S18, S21 |
| Trust boundaries | §1.3 | AR-S3, S15, S16, S21, S22, S25 |
| Versioned envelope `.qnar` / QNAR / QNAP | §2 | AR-S10, S22, S24 |
| AES-GCM + AAD (аутентифицированные метаданные) | §3.2 | AR-S7, S11 |
| KDF bounds / независимый контур | §4 | AR-S8, S9, S10 |
| Password separation (note ≠ archive ≠ RK) | §5.1 | AR-S4, S5 |
| Recovery key rotation / отзыв | §5.2 | AR-S19, S20 |
| No plaintext temp (A2) | §6 | AR-S2, S3, S23 |
| Atomic publication | §6 п.3 | AR-S17 |
| Dry-run / restore в новый путь | §7 | AR-S15, S16, S18 |
| Path traversal | §8 | AR-S12 |
| Symlinks / reparse | §8 | AR-S13 |
| Zip bombs / oversized | §8 | AR-S14 |
| Tampering / wrong secret | §3.3, §8 | AR-S6, S7 |
| Compatibility / agility | §9 | AR-S9, S10, S24 |
| DPAPI не единственная опора | §5.3 | AR-S21, S25 |

### 11.2. Утверждения AR-S*

| ID | Утверждение | Актор/актив | Минимальная проверка |
|---|---|---|---|
| AR-S1 | Файл `.qnar` без секретов не содержит plaintext незащищённой заметки, DEK, паролей; имена вложений не встречаются вне ciphertext нагрузки | T1 / A1,A7 | `ArS1_QnarFile_ContainsNoPlaintextNoteDekPasswordsOrAttachmentNames` |
| AR-S2 | Create не требует session и не вызывает decrypt protected; ciphertext в payload совпадает с диском | T4 / A2 | `ArS2_Create_DoesNotDecryptProtectedContent_AndCopiesCiphertextAsOnDisk` |
| AR-S3 | После create нет leftover snapshot в `%TEMP%` и рядом с `.qnar`, кроме самого `.qnar` | T4 / A2 | `ArS3_Create_LeavesNoStagingSnapshotBesideArchiveOrInTemp` |
| AR-S4 | Пароль архива открывает payload; пароль заметки — нет (и наоборот) | T2 / A4,A5 | `ArS4_ArchivePasswordOpensPayload_NotePasswordDoesNot` |
| AR-S5 | Recovery key открывает тот же payload; не равен паролю архива; не расшифровывает note envelope | T2 / A4,A6 | `ArS5_RecoveryKeyOpensSamePayload_AndDoesNotDecryptNoteEnvelope` |
| AR-S6 | Неверный пароль и неверный RK: отказ одного класса, 0 байт в назначении | T3,T7 | `ArS6_WrongPasswordAndWrongRecoveryKey_SameFailureClass_DestinationUntouched` |
| AR-S7 | Порча magic, header (AAD), wrap, ciphertext, tag, хвост файла — отказ | T3 | `ArS7_MutationsOfMagicHeaderWrapCiphertextTagAndTail_FailClosed` |
| AR-S8 | Подмена `kdfIterations` выше max или ниже min — отказ **до** долгого KDF (timeout) | T3 | `ArS8_KdfIterationsAboveMax_RejectedBeforePbkdf2`, `EncryptedArchivePbkdf2DefaultTests.Read_KdfIterationsBelowMin_RejectedBeforePbkdf2`, `Create_OutOfRangeIterations_RejectedBeforePbkdf2` |
| AR-S9 | Итерации N из header читаются (матрица M≠N); shipping default не подменяет header | agility | `ArS9_HeaderIterationsN_AreReadByClientWithDifferentPreferredM`, `EncryptedArchivePbkdf2DefaultTests` (default / explicit / backward) |
| AR-S10 | Неизвестный `kdfAlg` / `formatVersion` — fail-closed без KDF-перебора | T3 | `ArS10_UnknownKdfAlgorithmAndFormatVersion_FailClosedWithoutPbkdf2` |
| AR-S11 | Перестановка полей header без нового tag не проходит | T3 | `ArS11_HeaderFieldPermutationWithoutNewTag_Fails` |
| AR-S12 | Path traversal в QNAP (`../`, абсолютный, UNC, `attachments/..\\x`, ADS) не пишет вне корня | T3 | `ArS12_TraversalAbsoluteUncAndAdsPaths_AreRejectedWithoutWrites` |
| AR-S13 | Symlink/reparse наружу при pack — ошибка create, если файл обязателен; unpack не создаёт reparse; restore отказывает, если reparse есть в существующей цепочке предков destination/staging | T3,T4 | `ArS13_ReparsePointOnRequiredAttachment_FailsCreate_AndDryRunDoesNotCreateReparse`, `Restore_ReparseInExistingAncestor_IsRejectedWithoutWrites` |
| AR-S14 | Oversized `payloadLength`/`fileSize`/`entryCount`/длина файла: отказ по in-memory budget, процесс жив, диск назначения пуст, без выделения буфера по заявленному u64 | T3 | `ArS14_OversizedDeclarations_RejectedWithoutAllocatingDestination` |
| AR-S15 | Dry-run успех и неуспех: 0 новых файлов | T7 | `ArS15_DryRunSuccessAndFailure_WriteNoNewFiles` |
| AR-S16 | Restore только в пустой новый путь; существующий непустой — отказ без изменений | T7 / A9 | `ArS16_NonEmptyRestoreDestination_IsRejectedWithoutChanges`, `Restore_NonEmptyDestination_IsRejectedWithoutChanges` |
| AR-S17 | Сбой записи/до publish: final отсутствует, temp удалён; существующий `.qnar` не перезаписывается | T8 | `ArS17_Create_DoesNotOverwriteExistingArchive`, `ArS17_InjectedWriteAndBeforePublishFailures_LeaveFinalMissing_AndDeleteTemp` |
| AR-S18 | Сбой restore после staging не оставляет полукаталог и не трогает живую БД теста | T8 / A9 | `ArS18_InjectedFailuresBeforeAndAfterStaging_LeaveDestinationAndLiveDbUntouched` |
| AR-S19 | Ротация RK: обновлённый файл не открывается старым RK; копия старых байт — старым, не новым; password wrap и payload без изменений | T2 | `ArS19_Rotation_RevokesOldRecoveryKey_PreservesPasswordWrapAndPayload`, `ArS19_Rotation_WithArchivePassword_AndInjectedPublishFailure_LeavesOriginalBytes` |
| AR-S20 | Настройка RK не «завершена» без успешного dry-run с RK | T7 | `EncryptedArchiveRecoveryTests.ArS20_SetupIsNotCompleteUntilRecoveryDryRunSucceeds`, `EncryptedArchiveSetupViewModelTests` |
| AR-S21 | Restore без DPAPI: отдельный каталог, пустые credentials storage | T6 | process: `EncryptedArchiveIsolatedProfileSmokeTests.ArS21_IsolatedProfileProcess_RestoresWithRecoveryKey_WithoutTouchingLiveProfileOrDpapi`; in-process — AR-S25; **отдельный Windows-пользователь `[UNPROTECTED]`** |
| AR-S22 | Plaintext zip `quicknotes-archive` и пакет `QNSP` не принимаются как `.qnar` | T3 | `ArS22_PlaintextZipAndQnsp_AreRejectedAsQnar` |
| AR-S23 | Логи операции не содержат секрет и `Note.Text` protected | T4 / C8 | `ArS23_ExceptionMessagesAndLogs_DoNotContainSecretsOrProtectedText` |
| AR-S24 | Snapshot старой `user_version` внутри архива: restore + `DbInitializer` сохраняет сущности | совместимость | `ArS24_OldUserVersionSnapshot_SurvivesDbInitializerAfterValidatedDryRun`, `Restore_OldUserVersion_IsMigratedByDbInitializer` |
| AR-S25 | In-process API восстановления работает без WPF-окна и без чтения DPAPI | T6 | `ArS25_InProcessRestore_DoesNotUseWpfOrDpapi` |

Ручной протокол (не заменяет AR-S*): контрольное восстановление на чистой Windows-учётной записи — критерий выхода M3.

---

## 12. Открытые решения, блокирующие реализацию

Пока строка `BLOCKING`, связанный с ней production-код **запрещён**. Закрытие — явное редактирование этой таблицы (Accepted) или новый ADR.

| ID | Вопрос | Статус | Решение |
|---|---|---|---|
| **B1** | Входит ли в контейнер/recovery **облачный** секрет (пароль sync, S3 keys), чтобы RK возвращал доступ к тестовой облачной копии (`VISION.md` §4.4)? | **Accepted** | Архив v1 содержит только локальные данные (SQLite backup + вложения как на диске). Cloud credentials, пароль sync и S3 keys **исключены** и остаются этапом **M4**. Молча класть DPAPI-blobs в snapshot запрещено. |
| **B2** | Какой **не-UI** вход восстановления в v1? | **Accepted** | Тот же `QuickNotes.App.exe --restore-archive` (без отдельного `QuickNotes.Recover`, без DPAPI, без обязательного UI). Wiring CLI в этом slice. |
| **B4** | Число **итераций PBKDF2 по умолчанию** для новых архивов после измерения на эталонной машине (ADR-004). | **Accepted** | `EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives` = **2_490_000** только для create новых QNAR, если N не передан. Поле формата по-прежнему хранит явное N; reader не подставляет default. Жёсткие 10_000…5_000_000 до PBKDF2 и до payload-аллокации по declared length. Note/sync KDF не менялись. Argon2id не внедрён. Измерение §4 (2026-09-10, эта машина, не универсально). |

Закрыто ранее (не блокеры):

| Тема | Решение |
|---|---|
| KDF v1 архива | PBKDF2-HMAC-SHA256, независимые поля и лимиты §4; Argon2id не в M3 |
| Restore target v1 | только новый пустой путь + dry-run |
| Состав payload v1 | SQLite backup + attachments как на диске; формат QNAP, не ZIP; без semantic DB, логов, открытого экспорта, без обязательных cloud secrets |
| Третий секрет | пароль архива ≠ пароль заметки ≠ recovery key |
| DPAPI | не единственная опора; не нужна для decrypt `.qnar` |
| Extract path | запрещён `ExtractToDirectory` в temp; лимиты §8 |
| In-place живой БД | отложен; не блокирует v1 |

B4 закрыт shipping default для новых архивов. Явный N на create и чтение старых header N остаются обязательными. Критерий VISION Phase 0 про «RK возвращает облачную копию» не является частью M3.

---

## Альтернативы

- Считать текущий `NoteArchiveService` recovery — отвергнуто: plaintext, skip protected, `%TEMP%`.
- Полагаться только на DPAPI-бэкап профиля — отвергнуто ROADMAP для единственной копии ключа.
- Использовать пользовательский бакет для разрушительных тестов — запрещено планом.
- Перешифровать все заметки паролем архива при бэкапе — отвергнуто: нужны все note password; ломает C11; смешивает секреты.
- Хранить DEK только под recovery key (без пароля архива) — отвергнуто: провоцирует хранить RK на диске профиля.
- JSON-header в AAD — отвергнуто: нестабильная каноникализация.
- ZIP внутри AEAD как нагрузка — отвергнуто: zip-bomb и `ExtractToDirectory` как готовый класс ошибок.
- Начать код до закрытия B1/B2 — отвергнуто этим review.

## Последствия

Пока M3 не закрыт критерием выхода, потеря DPAPI-профиля не покрыта переносимым recovery. Документация и UI не должны обещать обратное. Открытый экспорт остаётся сознательным plaintext только незащищённых данных.

AR-S1…S20 и S22…S25 внесены в `INVARIANTS.md`. AR-S21 закрыт automated isolated-profile process; ручной отдельный Windows-пользователь остаётся `[UNPROTECTED]`. Create по-прежнему не делает overwrite существующего `.qnar`; ротация — единственный путь `File.Replace` для `.qnar`.

## Проверки

Реализованный открытый путь:

- `OpenExportTests` (protected omission, manifest/links/вложения, отказ до/после публикации, UI confirm, zip skip protected)
- `ImportExportTests` (JSON/Markdown, open snapshot, checksums, injected publish failure, UI confirm + last success)
- `PortabilityAndDailyTests.Archive_Roundtrip_IncludesBinaryAttachment_WithoutOverwrite`
- `PortabilityAndDailyTests.Archive_RestoresTagParentAndSynonym`
- `PortabilityAndDailyTests.Archive_RejectsPathTraversal` (только строковый helper plaintext zip — **не** покрывает AR-S12)

Зашифрованный контейнер (этот slice):

- `EncryptedArchiveRecoveryTests` AR-S1…S20, S22…S25 (create/dry-run/restore/publish/rotation; UI last-success только после RK dry-run)
- `EncryptedArchiveSetupViewModelTests` / `EncryptedArchiveXamlGuardTests` / `EncryptedArchiveOfflineCopyTests` (async create/verify/rotate, offline copy/print confirm, re-entry, busy close, UI не чистит temp по маске, неверный ключ, отмена, roundtrip protected, нет persist секретов, last-success после verification)
- `EncryptedArchivePbkdf2DefaultTests` (shipping default, явный N, чтение старого N, out-of-range до PBKDF2)
- `EncryptedArchivePbkdf2BenchmarkTests` (оценка N, host без измерения при лишних args, отчёт без dummy-пароля, process-level `QuickNotes.Tools.exe`)
- `EncryptedArchiveRestoreCliTests` (parse, exit codes, no-UI process, секрет не в argv/логах)
- `EncryptedArchiveIsolatedProfileSmokeTests` (headless restore RK в redirected profile, живой `%LocalAppData%\QuickNotes` не тронут)
- восстановление архива под отдельной учётной записью Windows (другой SID/DPAPI) вручную — нет (критерий выхода M3)
