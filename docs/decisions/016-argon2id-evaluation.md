# ADR-016: оценка Argon2id (spike, не production default)

- Дата: 2026-09-13
- Статус: Accepted **только как пакет оценки**. Argon2id **не** принят как production KDF и **не** пишется в новые конверты. Raise PBKDF2 **не** принят.
- Реализовано в коде: изолированный CLI-spike `QuickNotes.Tools.exe argon2id` (пакет `Konscious.Security.Cryptography.Argon2` только у `QuickNotes.Tools`). Production `QuickNotes.App` по-прежнему только PBKDF2-HMAC-SHA256.
- Пересмотр: до любого релиза, который меняет `kdfAlg` / descriptor version / shipping N; не вместе с несвязанным UI; требуется независимый security review и измерение на слабом железе.
- Связанные документы: [ADR-004](004-kdf-envelope-compatibility.md), [ADR-015](015-kdf-envelope-versioning.md), [ADR-002](002-export-archive-recovery.md), [docs/performance/kdf-cost-report-2026-09-13.md](../performance/kdf-cost-report-2026-09-13.md), [docs/performance/argon2id-feasibility-2026-09-13.md](../performance/argon2id-feasibility-2026-09-13.md)

## Контекст

ROADMAP §10.2 требовал либо поднять PBKDF2, либо принять Argon2id после review. Измерение 2026-09-13 (`QuickNotes.Tools.exe kdf`) уже есть. Production N **не** менялся:

| Контур | Shipping N новых объектов | Каноническая медиана 2026-09-13T05:06:03Z |
|---|---|---|
| Note | 120_000 | 12.580 мс (одно derivation на unlock) |
| Sync / blob | 100_000 | 10.183 мс **на конверт**; цикл O(packages + attachments) |
| Archive `.qnar` | 2_490_000 | 255.841 мс (ADR-002 B4, полоса 150–400 мс) |

Почему raise PBKDF2 **нельзя честно принять на этой машине**:

1. Security review уже запретил выдавать experimental точки (~50/100/150 мс; note 490_000 = 48.490 мс) за глобальный default. Low-end SKU не измерялся.
2. Единый N note+sync невозможен: 1 пакет + 100 вложений при experimental 490_000 заявляет **49_490_000** итераций при cycle budget **20_000_000**. Даже ~2× headroom для документированного 1+100 держит sync около 100_000.
3. Archive уже калиброван ~250 мс; поднимать его нельзя без новой B4-процедуры.
4. Существующие ciphertext нельзя переписывать при open/sync.

Предпочтительный следующий алгоритм — memory-hard Argon2id, но **не** как тихая подмена PBKDF2.

## Решение

### Уже в коде (проверяемые факты)

- Production readers принимают только `PBKDF2-HMAC-SHA256` / `algId 0x01`. `ARGON2ID` и любой неизвестный alg — отказ **до** `Pbkdf2` (`KdfDescriptor.TryParse` / `Validate`, QNSP/QNBA/QNAT/archive). Salt 32, key 32, AES-256-GCM/AAD, max 5_000_000, archive floor 10_000, recovery и парольная семантика не ослаблены.
- Новые PBKDF2-конверты по-прежнему 120_000 / 100_000 / 2_490_000. Legacy читается со своим N. Rewrite только на аутентифицированной записи (ADR-015).
- Inbound cycle budget остаётся **20_000_000 заявленных PBKDF2-итераций**. Он **не** описывает Argon2id.
- Изолированный spike: `QuickNotes.Tools.exe argon2id` / `--spike-argon2id`. Dummy password/salt, без БД/DPAPI/профиля/облака. Профили (не defaults): `owasp-interactive-2023` (m=19456 KiB, t=2, p=1), `owasp-high-memory-2023` (m=47104, t=1, p=1), `rfc9106-second-recommended` (m=65536, t=3, p=4), плюс лабораторный `tiny` только для тестов. Потолок spike: m≤131072 KiB, t≤10, p≤16. Стратегия: `adr-016-isolated-argon2id-spike-not-production-default`.
- Локальный прогон 2026-09-13T09:34:15Z (warmups 1 / repeats 3): OWASP interactive медиана **92.827 мс** (1+100 ≈ 9.4 с); OWASP high-memory **51.476 мс**; RFC 9106 second **80.614 мс**. Не defaults.
- `QuickNotes.App.csproj` не ссылается на Argon2.

### Принятое направление (пока ADR не сменён)

Любая **будущая** миграция на Argon2id обязана:

1. Новый algorithm id (кандидат бинарного поля: `0x02`, не путать с QNAT container version `0x02`) и новый канонический descriptor, например `ARGON2ID|{descriptorVersion}|{m}|{t}|{p}|32`. Текущий четырёхпольный PBKDF2-текст не расширять «на глаз».
2. Читать все существующие PBKDF2-дескрипторы без изменения ciphertext. Upgrade — только после успешной AEAD-аутентификации, в той же транзакции, что и сейчас для descriptor migration. QNSP/QNBA/`.qnar` не переписывать молча.
3. Заменить untrusted inbound budget: заявленные PBKDF2-итерации не переносятся. Нужны **явные caps по memory KiB и time/t** до derivation плюс кумулятивный memory×time на цикл. Hostile Argon2id с огромным `m` — отдельный DoS.
4. Не унифицировать note и sync одним параметрическим набором: sync по-прежнему O(packages+attachments); 1+100 при RFC 64 MiB / конверт неприемлем как sequential wall-clock и как peak RSS, если derivations не строго последовательны с освобождением памяти.
5. Измерить low-end SKU. Не объявлять OWASP/RFC набор shipping default по одной desktop-машине.
6. Не смешивать с посторонним UI-релизом. Не ослаблять salt/key/AES-GCM/AAD/max/recovery.

`ReadyForLaterMigration: true` в отчёте spike означает: пакет оценки исполняем и совместимость fail-closed уже есть. Это **не** разрешение включить Argon2id в App.

### Эксперимент

Условия, без которых Argon2id не входит в продукт: независимый security review; отчёт spike на заявленной машине; отдельный ADR смены shipping параметров; тесты смешанного флота (старый клиент отвергает новый alg; новый клиент читает PBKDF2 N).

## Альтернативы

- **Поднять PBKDF2 note до 490_000 (~50 мс) и sync оставить 100_000** — отвергнуто как несогласованный «актуальный ориентир»; experimental точка запрещена как default; sync остаётся ~10 мс/envelope.
- **Поднять sync вместе с note и увеличить 20M budget** — отвергнуто: продуктовое решение о секундах KDF на 1+100 и о DoS-headroom; в этом этапе его нет.
- **Сразу включить Argon2id в App** — отвергнуто: ломка wire-format, нет caps, нет mixed-fleet writers.
- **Свой исходник Argon2 вместо библиотеки** — отвергнуто для spike: выше риск, чем pinned Konscious 1.3.1 только в Tools.

## Последствия

- Решение §10.2 закрыто **без** crypto migration: ни raise, ни production Argon2id.
- Старые и новые PBKDF2-конверты взаимочитаемы как раньше.
- Появление Argon2id-библиотеки в testhost идёт через Tools, не через production encrypt path.

## Проверки

- `Argon2idFeasibilityBenchmarkTests`, `Argon2idFeasibilityBenchmarkHostTests`
- `KdfDescriptorEnvelopeTests` (в т.ч. `ARGON2ID|…` до derivation)
- `Pbkdf2CostBenchmarkTests` (production N и `Argon2idAccepted: false`)
- `EncryptedArchivePbkdf2DefaultTests` / AR-S8…S10
- CLI: `QuickNotes.Tools.exe argon2id` (dummy only)
