# ADR-004: стратегия KDF и совместимость конвертов

- Дата: 2026-09-10 (измерение note/sync: 2026-09-13)
- Статус: Accepted только как *ограничения совместимости*. Выбор «поднять PBKDF2» **отклонён** на текущих измерениях (см. ниже и ADR-016). Argon2id **не** production default; пакет оценки — [ADR-016](016-argon2id-evaluation.md). Локальное измерение note/sync есть (2026-09-13); production default N **не изменён**. Archive v1 остаётся на PBKDF2; shipping default N для *новых* QNAR **измерен** (B4 Accepted в ADR-002).
- Реализовано в коде: PBKDF2-HMAC-SHA256 + AES-256-GCM с версией и итерациями в конвертах заметок/sync **и** независимом конверте архива `.qnar` (явный N в header; production default только на create новых файлов). Измерение архива — ADR-002 §4; измерение note/sync — `docs/performance/kdf-cost-report-2026-09-13.md`. Изолированный Argon2id spike — Tools, ADR-016. Единый ориентир итераций на все контуры и смена shipping N — нет
- Пересмотр: до криптографических изменений note/sync и до Release, который меняет конверт; не вместе с несвязанным UI-релизом; повторно при смене эталонной машины для archive default
- Связанные документы: [ROADMAP.md](../../ROADMAP.md), [ADR-002](002-export-archive-recovery.md)

## Контекст

`ROADMAP.md` §10.2 требует версионировать KDF рядом с ciphertext, измерить стоимость, либо поднять PBKDF2, либо принять Argon2id после review, читать старые данные.

Факт кода (это не «стратегия 1.0», а текущая реализация):

| Контур | Тип | KDF в коде | Итерации по умолчанию для *новых* объектов |
|---|---|---|---|
| Заметка | `NoteCryptoService` | `KdfName` = `PBKDF2-HMAC-SHA256`, `KdfVersion = 1` | `DefaultIterationsConst` |
| Sync-пакет / blob | `SyncCryptoService` | то же имя | `SyncCryptoService.DefaultIterations` |

Число итераций **различается между контурами** (см. константы в этих файлах, не дублировать здесь). Оно хранится в полях конверта (`Note.ProtectedKdfIterations`, `SyncPackageEnvelope.Crypto.KdfIterations`) и участвует в AAD пакета (`SyncPackageEnvelope.GetAssociatedData`).

Argon2id в проекте нет. Конверт архива `.qnar` реализован как format/crypto core (`EncryptedArchiveService`); shipping default iterations для *новых* архивов задан измерением (ADR-002 B4), отдельно от note/sync.

## Решение

- **Уже в коде.** AES-256-GCM; PBKDF2-HMAC-SHA256; salt/nonce/tag; format version; итерации пишутся в конверт и читаются при decrypt (`NoteCryptoService`, `SyncCryptoService`, импорт пакета `SyncPackageImporter`).
- **Принятое направление (ещё не исполнено как проект).** Любая смена алгоритма или порядка итераций: (1) новое значение `KdfVersion` / format version в конверте; (2) чтение старых конвертов; (3) upgrade только после успешной аутентификации; (4) не в одном релизе с посторонним UI; (5) измерение на заявленном железе до смены константы.
- **Не решение этого ADR.** «Мы уже на актуальном PBKDF2-ориентире» или «Argon2id принят». Это ложь относительно кода.

## Альтернативы

- Сменить KDF без полей версии — отвергнуто: сломает чтение старых БД и пакеты.
- Унифицировать итерации заметки и sync одним коммитом без тестов чтения старых конвертов — отвергнуто.

## Последствия

Два разных default iteration count заметки и sync — сознательный текущий факт, не цель. Измерение **note/sync** на этой машине есть (2026-09-13); **существующие** константы не повышаются. Archive default измерен отдельно и не унифицирует note/sync.

### Измерение принято; смена KDF — нет

Локальные прогоны `QuickNotes.Tools.exe kdf` (не универсально; не low-end):

- **2026-09-13T05:06:03Z**, probe 50_000 → 5.175 мс: note 120_000 = **12.580 мс**; sync/envelope 100_000 = **10.183 мс**; archive 2_490_000 = **255.841 мс**.
- **2026-09-13T07:38:18Z**, probe 50_000 → 5.077 мс: note 11.763 мс; sync 10.067 мс; archive 244.271 мс (шум той же константы).

**Sync.** Каждый QNSP и каждый QNBA имеет свою соль и `DeriveKey`. Стоимость цикла **O(packages + attachments)**. При текущем 100_000 оценка с 05:06Z: 1+0 = 10.183 мс, 1+1 = 20.366 мс, 1+10 = 112.013 мс, 1+100 = 1028.483 мс. Единый sync candidate (в том числе 720_000 из неверной посылки «два KDF») **не рекомендуется**.

**Note.** 150 мс — **не** search p95 (другой product path). Экспериментальные точки одной derivation на этой машине (не default): ~50 мс / 490_000 (48.490 мс); ~100 мс / 980_000 (99.156 мс); ~150 мс / 1_470_000 (144.391 мс) и ранее 1_440_000 (148.160 мс). Перед глобальным default нужны измерения на слабом железе.

**DoS недоверенных cloud envelopes.** Потолок **5_000_000 на каждый** blob/пакет сохраняется. Кумулятивный CPU пачки ограничен per-sync-cycle inbound бюджетом **20_000_000** заявленных PBKDF2-итераций (`UntrustedInboundKdfWorkBudget`, проверяется до derivation). Документированный нормальный сценарий 1 пакет + 100 вложений при production 100_000 = 10_100_000 и помещается; лимит не выведен из экспериментальных 720k/1.44m. Production N не изменён. Budget exhaustion — отдельная классификация, не offline/auth, без сетевого ретрая.

**Консервативная рекомендация этого этапа:** измерение текущих N принято как локальное наблюдение. **Raise PBKDF2 не принят** (experimental 490_000 не default; 1+100 при 490_000 = 49_490_000 > budget 20_000_000; low-end нет; archive уже ~250 мс). **Argon2id не принят как default.** Пакет оценки: ADR-016 + `QuickNotes.Tools.exe argon2id`. Константы 120_000 / 100_000 / 2_490_000 не меняются. Inbound cycle budget — защита от кумулятивного PBKDF2 CPU, не смена стоимости KDF и не модель Argon2id.

### Конверт зашифрованного архива (M3, format/crypto core)

Задано ADR-002; здесь — только KDF/agility. Код: `EncryptedArchiveService` / `EncryptedArchiveKdfLimits`. Production default N для новых QNAR: `EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives` (**2_490_000**). Create без явного N берёт эту константу и всё равно пишет её в header. Явный N по-прежнему валидируется. Тесты могут инъецировать низкий N через `EncryptedArchiveKdfLimits.ForTests`.

Принято для archive v1:

- Независимые поля рядом с ciphertext wrap/payload: `kdfAlg`, `kdfVersion`, `kdfIterations` (будущий Argon2id — отдельные m/t/p, новый `kdfAlg`/`kdfVersion`).
- Алгоритм v1: `PBKDF2-HMAC-SHA256` (как в коде заметок/sync). Argon2id **не** внедряется этим этапом.
- Canonical header целиком входит в AAD wrap и payload (ADR-002 §3.2). Подмена алгоритма/итераций без нового tag не проходит.
- Параметры KDF в файле **не аутентифицированы до** первого успешного wrap-decrypt. Защита от DoS (актор T3): отвергать до `Pbkdf2` значения вне лимитов. Для v1:
  - минимум итераций на **чтение недоверенного файла** в production limits: ≥ 10_000 (ниже — отказ); тесты передают `EncryptedArchiveKdfLimits.ForTests` с низким минимумом;
  - максимум итераций на чтение: ≤ 5_000_000;
  - salt ровно 32 байта; иное — отказ;
  - неизвестный `kdfAlg` — отказ без перебора алгоритмов.
- Production **default** iterations для *новых* архивов — **B4 Accepted** (ADR-002 §4): **2_490_000**, цель ~250 мс (допуск 150–400 мс) на измеренной машине 2026-09-10. Не копировать 100_000/120_000 из sync/note. Не объявлять OWASP 600_000 без измерения. Unit-тесты могут инъецировать низкий N. Reader **никогда** не подставляет default вместо поля header.
- Archive reader не использует `NoteCryptoService.DefaultIterationsConst` / `SyncCryptoService.DefaultIterations` как fallback, если поле в header отсутствует: отсутствие поля = невалидный v1.
- Смена KDF архива не в одном релизе со сменой note/sync конверта и не в одном релизе с посторонним UI.

Не решение: «архив сразу на Argon2id» или «единый iteration count на все контуры».

## Проверки

Есть:

- `NoteProtectionTests.CryptoService_RoundTrip_EncryptsAndDecrypts` и соседние tamper/wrong password
- `NoteProtectionRegressionTests` (смена пароля, конверт вложения, sync ciphertext)
- `SyncTests` утверждает `envelope.Crypto.KdfAlgorithm`
- `BlobAttachmentSyncTests.Encrypt_Decrypt_Roundtrip_ProducesIdenticalPlaintext`

Нет:

- внедрения новых note/sync defaults;
- единого sync candidate;
- Argon2id в production App;
- единого iteration count на все контуры;
- повышения production N. Inbound cycle KDF budget для недоверенных cloud envelopes есть (20_000_000).
- Есть изолированный spike Argon2id (не default): ADR-016, `QuickNotes.Tools.exe argon2id`.

Есть (note/sync/архив, локально, не универсально):

- `QuickNotes.Tools.exe kdf` (dummy password/salt, 3 прогрева / 5 повторов, experimental points + sync scale 1+0/1/10/100);
- `docs/performance/kdf-cost-report-2026-09-13.md`: текущие N; экспериментальные ~50/100/150 мс; стратегия `measurement-accepted-no-kdf-change`.

Есть (архив, локально, не универсально):

- `QuickNotes.Tools.exe` (3 прогрева / 5 повторов, dummy password/salt; консольный процесс, не WinExe);
- ADR-002 §4: 2026-09-10, Windows 10.0.26200 / .NET 8.0.23 / 12 CPU, probe 50_000 → 5.015 мс, shipping 2_490_000 → медиана 255.167 мс (243.591–260.255);
- `EncryptedArchivePbkdf2DefaultTests`, `EncryptedArchivePbkdf2BenchmarkTests`, `Pbkdf2CostBenchmarkTests`, `Pbkdf2CostBenchmarkHostTests`.
