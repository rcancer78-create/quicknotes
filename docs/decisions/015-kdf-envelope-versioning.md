# ADR-015: версионный KDF descriptor в конвертах note/sync/blob

- Дата: 2026-09-12
- Статус: Accepted (формат/версионирование). **Не** решение о смене стоимости KDF и **не** принятие Argon2id.
- Реализовано в коде: да (новые записи несут descriptor; legacy читается; автоматический rewrite — только на аутентифицированной записи)
- Пересмотр: до первого изменения эффективной стоимости PBKDF2 или до принятия Argon2id как production default (ADR-016 — только spike)
- Связанные документы: [ROADMAP.md](../../ROADMAP.md) §10.2, [ARCHITECTURE.md](../../ARCHITECTURE.md) §6-§8, [INVARIANTS.md](../../INVARIANTS.md) §3/§7, [ADR-004](004-kdf-envelope-compatibility.md), [ADR-002](002-export-archive-recovery.md)

## Контекст

ROADMAP §10.2 требует версионировать KDF в каждом note/sync/archive envelope и сохранить чтение
старых данных. ADR-004 зафиксировал ограничения совместимости и запретил менять стоимость «на глаз».
Фактически конверты уже несли поля KDF, но **без единого контракта и без версии дескриптора**:

| Контур | Формат | Legacy default N | Файл |
|---|---|---|---|
| Заметка (текст/источник/title) | JSON-колонки `Note.Protected*` | `120_000` | `NoteCryptoService.cs` |
| Ревизия / метаданные вложения | те же JSON-колонки на `NoteRevision` / `NoteAttachment` | наследует N заметки | `NoteProtectionService.cs` |
| QNAT (файл вложения защищённой заметки) | бинарный v1 | наследует N заметки | `ProtectedAttachmentFile.cs` |
| QNSP (sync-пакет) | JSON `SyncPackageEnvelope` | `100_000` | `SyncCryptoService.cs` |
| QNBA (облачный blob вложения) | бинарный v1 | N сервиса (`100_000` в prod) | `SyncAttachmentBlobService.cs`, `SyncPasswordRotationService.cs` |
| `.qnar` (recovery-архив) | canonical header `QNAR\|...\|kdfAlg\|kdfVersion\|kdfIterations` | `2_490_000` | ADR-002 / `EncryptedArchiveService.cs` |
| Recovery key / offline copy | Crockford Base32 + SHA-256 checksum (не PBKDF2) | — | `RecoveryKeyEncoding.cs` |

Архив уже имел явные `kdfAlg`/`kdfVersion`/`kdfIterations` **внутри canonical header, который входит в AAD
wrap и payload** (ADR-002 §3.2), то есть уже удовлетворял требованию §10.2. Остальные контуры несли N
без версии дескриптора, что мешало будущей смене KDF без ломки чтения.

## Решение

**Уже в коде (проверяемые факты):**

- Единый контракт: `QuickNotes.App/Services/Crypto/KdfDescriptor.cs` — `KdfDescriptor` (algorithm id,
  descriptor version, work factor, salt length), `KdfDescriptorLimits` (ArchiveUntrusted / LocalEnvelope /
  CloudEnvelope), `KdfDescriptor.TryParse` и `KdfAlgorithmIds`.
- Канонический текст дескриптора: `PBKDF2-HMAC-SHA256|1|{iterations}|32`. Строгий парсинг: ровно 4 части,
  известный алгоритм, известная версия дескриптора, канонические целые (без `0123`, `+5`, пробелов) и
  bounds по work factor/salt.
- Границы: min 1 (local/cloud), **10_000** (archive, как было), max **5_000_000** для всех контуров.
  Проверка выполняется **до** любого `Rfc2898DeriveBytes.Pbkdf2`.
- Новые записи:
  - `Note.ProtectedKdfDescriptor` / `NoteRevision.*` / `NoteAttachment.*` (SQLite, schema v14, `TEXT NULL`);
  - QNAT v2: `QNAT,0x02,algId(u8),descriptorVersion(u16 LE),iterations(i32 LE),saltLen,salt,nonce,tag,size,ciphertext`;
  - `SyncCryptoHeader.KdfDescriptor` в QNSP-конверте;
  - QNBA v2 в blob-конвертах (7 байт descriptor header после version byte).
- Legacy: NULL/пустой descriptor ⇒ конверт читается с **точным историческим** N из своего поля
  (note row / QNSP header / N сервиса для QNBA v1) и помечается `KdfEnvelopeState.LegacyMissingDescriptor`.
  QNAT/QNBA v1 читаются без изменений.
- Sync fingerprint (`SyncFingerprintHelper.ComputeNoteFingerprint` / `ComputeAttachmentFingerprint`) включает descriptor: пустой/`NULL` descriptor не добавляет ни строки и сохраняет исторический hash (legacy sync state не «просыпается»), а появление или смена descriptor меняет hash и корректно детектится как local modification.
- `KdfEnvelopeReading.NeedsMigration` — «needs migration», не автоматический rewrite. `INoteProtectionService` отдаёт `NeedsKdfMigration` / `InspectKdfEnvelope` как read-only результат.
- QNAT v2: `DecryptContainer` принимает опциональный descriptor заметки-владельца и обязан сверить его с descriptor контейнера **до** AES-GCM (`ProtectedAttachmentFile` получает уже производный ключ, поэтому descriptor сам ключ не меняет). Несовпадение/неизвестный/невалидный descriptor — отказ до расшифровки; v1 читается как раньше.
- Новые nullable поля sync-конверта (`SyncCryptoHeader.KdfDescriptor`, `SyncNoteDto`/`SyncAttachmentDto.ProtectedKdfDescriptor`) помечены `JsonIgnore(WhenWritingNull)`, чтобы legacy-форма JSON не менялась; новые записи всегда пишут non-null descriptor.
- Нумерация ADR: 015 занят этим решением (черновик ранее назывался 016); отдельного ADR-016 нет.
- Постепенная миграция только там, где транзакция гарантирует отсутствие потери данных:
  `NoteProtectionService.ApplyUnlockedEdits` вызывается **внутри** save-транзакции редактора и пишет
  descriptor вместе с перешифровкой того же конверта. Для QNSP/QNBA/`.qnar` автоматический rewrite
  **не** делается: внешние артефакты принадлежат пользователю/облаку и уже несут свои поля.

**Принятое направление (ещё не исполнено):**

- Descriptor — **метаданные**, намеренно **не входит ни в один AAD**. Добавление в AAD сломало бы
  byte-for-byte совместимость legacy (AAD заметки — `QNNOTE|fmt|syncId|objectType`, AAD пакета —
  `QNSP|...|KdfIterations`, AAD blob — `QNBA|1|sha256`). Подмена descriptor меняет производный ключ,
  поэтому AEAD-аутентификация падает fail-closed. Это свойство закреплено тестом
  `SyncPackage_DescriptorIsMetadataOnly_AndNotPartOfAad`.
- Любая будущая смена алгоритма/стоимости: новый `descriptorVersion` (или новый alg id) + чтение старых +
  benchmark на целевом железе. Archive остаётся на своём canonical header и своих границах.

## Альтернативы

- **Добавить descriptor в AAD** — отвергнуто: ломает byte-for-byte legacy AAD и делает старые конверты нечитаемыми.
- **Один общий iteration count на все контуры** — отвергнуто (ADR-004): note/sync не измерены, archive default
  измерен отдельно; унификация «на глаз» недопустима.
- **Молчаливый rewrite legacy-конвертов при чтении** — отвергнуто: чтение не должно писать; для QNSP/QNBA/.qnar
  это ещё и внешние объекты.
- **Сохранять N только в «своём» поле без версии дескриптора** — отвергнуто: ровно это и мешало отличать
  legacy default от нового контракта.
- **Argon2id сейчас** — отвергнуто в этом этапе: нет независимого review и benchmark.

## Последствия

- Старые профили читаются без миграции данных: schema v14 только добавляет nullable-колонки.
  «Needs migration» остаётся честным статусом, пока пользователь не сохранит разблокированную заметку
  или не сменит пароль.
- QNAT v2 несёт 32-байтовую salt-заглушку: ключ выводится из соли **владeльца-заметки**, а не из контейнера.
  Поле существует для claim-check и совместимости формы, а не как вход KDF.
- Effective PBKDF2 cost **не изменился**: note 120_000, sync/blob 100_000, archive 2_490_000 (create).
  Ни одна константа стоимости этим ADR не повышена и не понижена. Локальный benchmark 2026-09-13 (`QuickNotes.Tools.exe kdf`, `docs/performance/kdf-cost-report-2026-09-13.md`) измерил текущие N и экспериментальные точки; **ни raise PBKDF2, ни Argon2id как default не приняты**. Оценка Argon2id — ADR-016 (Tools spike).
- В логах/метаданных нет секретов: descriptor содержит только algorithm id, версию, N и длину соли.

## Проверки

- `KdfDescriptorEnvelopeTests` (58 тестов): канонический формат и матрица отказов; legacy note/QNAT/QNBA/QNSP чтение;
  новый round trip; подмена work factor/версии дескриптора/alg id **до** derivation (`KdfDiagnostics.Pbkdf2Invocations == 0`);
  sync-fingerprint legacy/null и descriptor-change матрица; QNAT v2 owner-binding (`DecryptContainer` отклоняет несовпадение до AES-GCM);
  неверный пароль; QNSP/QNBA protected paths и отсутствие plaintext в пакете; миграция на аутентифицированной записи
  и rollback при прерванной транзакции; schema v14 идемпотентна и не переписывает ciphertext.
- Существующие `NoteProtectionTests`, `NoteProtectionRegressionTests`, `SyncTests`, `BlobAttachmentSyncTests`,
  `CloudPasswordRotationTests`, `EncryptedArchive*` остаются зелёными (archive-формат не тронут).
- Нет: внедрения новой стоимости KDF; Argon2id в App; единого N на все контуры.
  Есть локальный отчёт измерения: `docs/performance/kdf-cost-report-2026-09-13.md`.
  Есть per-sync-cycle inbound KDF budget 20_000_000 заявленных итераций (не смена N).
  Есть ADR-016 / `QuickNotes.Tools.exe argon2id` (spike, не default).
