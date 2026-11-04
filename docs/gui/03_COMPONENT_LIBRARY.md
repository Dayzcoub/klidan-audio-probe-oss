# 03. Component library and states

All components include: `Default, Hover (where relevant), Focus, Disabled, Busy, Empty, Error` as applicable. Critical status includes text + icon, never color alone.

| Component | Purpose | Required behavior |
|---|---|---|
| `AppShell` | Header, navigation, workspace, bottom runtime status | Displays version, offline status, never auto-starts active testing |
| `EndpointListItem` | Friendly name, direction, transport, online state | Selection does not Initialize or Start; opaque ID local-only in advanced detail |
| `EndpointDetailHeader` | Selected device, driver/format facts | Unknown metadata shown as unknown; direction unambiguous |
| `ModeLevelPill` | Passive query / Initialize-only / Active Capture / Active Render | Label + icon; distinguishes device reservation and audio transfer |
| `FormatMatrixTable` | Rows by rate/channels/encoding/period | Cells: `SUPPORTED_REPORTED`, `READY`, `FAILED`, `UNSUPPORTED`, `INCOMPLETE`, `NOT_TESTED`; tooltip shows HRESULT and provenance |
| `EvidenceBadge` | Lifecycle, signal, integrity and confidence | Separate badges for `Reported`, `Initialized`, `Started`, `Clean`, `Operator review` |
| `ChannelMeterStrip` | Digital per-channel short-window peak/RMS + hold + clip | Explicit `нет данных` vs `тишина`; no physical socket claims |
| `RunControlBar` | Settings, consent and Start/Stop | Only one dominant CTA; Stop always visible while active; no silent fallback |
| `ActiveTestConsentDialog` | Separate consent per potentially disruptive operation | Names endpoint, mode, duration, output/capture side effects, last explicit confirmation |
| `OutputChannelCard` | C2 selected Render channel | Text: Windows channel index, physical mapping unknown; all unselected channels zero by design |
| `DiagnosticsTimeline` | stages and errors, times, HRESULT | Stable ordering, copy and export, no PII in shared outputs |
| `ReportExportPanel` | JSON save and summary | Preview privacy, schema/version, read-only before export; never overwrite silently |
| `EmptyState` | No device, no selection, no measurements | Clear next action; does not imply failure |
| `ErrorNotice` | Invalidated endpoint, busy Exclusive, unsupported format | Show plain-language explanation + exact HRESULT in expandable detail |

## State boundaries
- `Ready` in the **format matrix** means event-client initialization readiness, **not** a successfully started stream.
- `Completed` in Capture lifecycle means elapsed/completion, **not** an error-free stream. Display `Stream quality: CLEAN/ANOMALIES_PRESENT/...` separately.
- `Signal present` indicates nonzero PCM, **not** sound heard on a speaker.
- `Physical mapping unknown` must remain shown until separately supplied by an operator.
- `No input samples yet` is not `Silent`.

## Table design
- Default columns: format, rate, channels, Default, Minimum. Each cell supports detail popover containing support HRESULT, init HRESULT, actual period, event readiness and cleanup provenance.
- Provide filters `Все`, `Готово`, `Неподдерживается`, `Ошибки`; do not hide untested candidates by default.
- `busy`/Exclusive conflict needs a separate label from unsupported format.

## Consent content (sample)
**Проверка входа** · «Будет временно открыт вход *[название]* в режиме WASAPI Exclusive на 5 секунд. Другие программы могут потерять доступ к устройству. Запись файла не ведётся. Выходы звука не используются.» Buttons `Отмена` / `Начать Capture`.

**Проверка выхода** (C2) · «Будет воспроизведён тихий прерывистый тестовый сигнал на Windows-канале [N] устройства *[название]*. Сначала уменьшите громкость усилителей/мониторов. Физическое назначение разъёма неизвестно.» Buttons `Отмена` / `Воспроизвести тест`.

Both dialogs must be truthful at runtime and carry mode-specific confirmations to the core; dismissing consent must not activate a stream.
