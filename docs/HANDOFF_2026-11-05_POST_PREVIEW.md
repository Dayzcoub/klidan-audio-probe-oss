# Handoff — KLIDAN Audio Probe после публичного Preview

**Дата:** 2026-11-05  
**Для нового чата:** использовать этот документ и [актуальную дорожную карту](ROADMAP.md); не ориентироваться на старый roadmap октября.

## Репозитории и релиз

- OSS: `Dayzcoub/klidan-audio-probe-oss`.
- Основная ветка: `main`, HEAD при handoff `a8f48877de8a73237333548afc58b8f6dcd978ae`.
- Первый публичный релиз: [`v0.1.0-preview.1`](https://github.com/Dayzcoub/klidan-audio-probe-oss/releases/tag/v0.1.0-preview.1), prerelease опубликован 2026-11-05.
- Installer: `KLIDAN-AudioProbe-Setup.exe`, 47 685 474 bytes, SHA-256 `4e8e742bcce490ae5a1740fb9eadaa9c64ef42086ad019397273094bc3d3ad44`; manifest и release notes прикреплены. Installer unsigned.
- Исторический приватный `Dayzcoub/klidan-audio-probe` НЕ объединять историей с OSS и не раскрывать приватные данные.
- Репозиторий игры: `Dayzcoub/game-prodolji-pesniy`. Интеграция в нём ещё не подтверждена и требует отдельной работы.

## Реальное состояние продукта

- Реализованы WASAPI discovery, формат/Initialize, period discovery и exact custom periods, bounded stream/duplex, repeatability, sustained validation 60/300/600 s.
- Реализован Automatic Scan с явным согласием, exact pairing, fail-closed состояниями, сохранением результатов.
- **S4 готов:** same-scan evidence-based Primary/Fallback, чистые 300/600 s persisted/readback; Fallback может быть NONE.
- **S5 готов:** versioned profiles v1–v4, локальная привязка, sanitized export как **unbound**, Evidence Library, `LOCAL_FULL_EVIDENCE` ZIP с manifest/SHA-256/offline Verify.
- GUI WPF ru/en, Worker через Named Pipe, Dev Tools OFF by default; installer и Preview опубликованы.
- `AudioHost NOT_CERTIFIED`, физический RTL `NOT_MEASURED`. Не выдавать прошлые hardware observations за проверку релизного бинарника.
- Реальный полный лабораторный проход в опубликованном Preview не подтверждён. Протокол: `docs/LAB_PILOT_PROTOCOL.md`.
- Старые сообщения про «всё ещё надо сделать S4» или «нет ZIP-бандла» **устарели**.
- Давнее преждевременное завершение Audient scan на другом ноутбуке не следует считать текущим воспроизведённым blocker без свежего trace.

## Приоритет на новый чат

**Не начинать ещё один DPI/QA-марафон.**

Главная следующая разработка: **I1, контракт обмена v4-профилем Audio Probe → KLIDAN**:
1. Изучить точный v4 schema/serializer и реальные точки конфигурации KLIDAN AudioHost.
2. Составить таблицу полей, evidence requirements и статусов отказа. `LOCAL_FULL_EVIDENCE` содержит private endpoint IDs; sanitized profile unbound и не допускает silent activation.
3. Определить способ повторной привязки/проверки Capture/Render, driver/USB context, WASAPI exact format/period и MMCSS на текущей машине.
4. Разделить read-only import (I2) и opt-in apply/rollback (I3), не изменять AudioHost в I1.
5. Завершить конкретным ADR/контрактом и независимым Draft PR. Никаких выдуманных физических измерений.

**Параллельная практическая ветка:** один реальный L3 Standard 300 s scan с согласия оператора, с сохранением v4 и evidence ZIP; не является блокером документации/импорта. При ошибке исследовать конкретные логи. Если требуются действия оператора на Windows, **всегда дать готовый PowerShell-скрипт запуска**.

## Правила взаимодействия

- Русский, кратко, технически, прямые решения. Не задавать повторных вопросов, если ответ уже в контексте.
- Пользователь устал от многократных GUI/DPI тестов: **только адресные проверки затронутого кода**.
- Не создавать новую гигантскую QA-матрицу или многократно перечислять одинаковые PASS.
- При назначении Codex: точный репозиторий, branch, scope, ограничения, конечный результат; Codex должен **продолжать после промежуточного коммита**, не останавливаться на отчёте о препятствии.
- Сборка/работа с локальным репозиторием обычно из `H:\klidan-audio-probe-oss`, но сверять реальное состояние, не делать reset/stash/clean чужих изменений.
- GitHub Actions в пользовательском процессе должны оставаться manual-only, не менять триггеры без указания.
- Не выполнять merge, release, tag, физический аудиовывод и установку драйверов без явного решения пользователя.

## Документация

- `docs/ROADMAP.md` — актуальные направления I1/I2/I3, L3, U1.
- `docs/PROFILE_FORMAT.md`, `docs/schemas/device-configuration-profile-v4.schema.json` — данные/валидация.
- `docs/LAB_EVIDENCE_BUNDLE.md` — ограничение приватности ZIP.
- `docs/adr/ADR-001-portable-profile-boundary.md` — принят принцип разделения Probe и KLIDAN.
- `docs/archive/ROADMAP_2026-10-29.md` — исторические заметки, не current backlog.
