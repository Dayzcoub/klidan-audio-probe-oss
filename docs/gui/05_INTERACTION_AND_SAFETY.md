# 05. Interaction, safety and state behavior

## State machine (UI-level proposal)
`IDLE → PREPARING → CONSENT_REQUIRED → INITIALIZING → RUNNING → STOPPING → COMPLETED / COMPLETED_WITH_WARNINGS / FAILED / CANCELLED`.

`CONSENT_REQUIRED` is never automatic. Passive discovery and support queries can be started from a deliberate button press without active stream consent; Initialize-only still needs warning/confirmation because it may reserve Exclusive endpoint.

### Allowed actions
| State | UI action | Result |
|---|---|---|
| IDLE | Select endpoint | Show passive facts; do not change audio session |
| IDLE | Scan passive matrix | Query selected endpoint only; no Initialize/Start |
| IDLE | Validate formats | Ask to confirm temporary Exclusive initialization; no Start |
| IDLE | Meter input / output test | Show dedicated per-mode consent |
| RUNNING | Stop | Request cancellation immediately, disable Start, wait for core's terminal event |
| STOPPING | Start again | Disabled until cleanup is confirmed or reported failed |
| FAILED | Retry | New explicit invocation; never automatic stream restart |
| any | Change endpoint | Prohibited during active test; selected device frozen in run context |

## Completion semantics
Keep **four independent rows** (do not derive one from another):
- Operation lifecycle: `NOT_STARTED, STARTING, RUNNING, STOPPING, COMPLETED, FAILED, CANCELLED`.
- WASAPI stream status: `STREAM_COMPLETED, STREAM_COMPLETED_WITH_WARNINGS, STREAM_FAILED, ...`.
- Data-quality verdict: `CLEAN, ANOMALIES_PRESENT, SEVERE_ANOMALIES, UNKNOWN`.
- Evidence authority: `PASSIVE_QUERY, INITIALIZE_ONLY, LIVE_CAPTURE_METER, OPERATOR_REVIEW_REQUIRED`, no auto certification.

## Consent UX
- No persisted blanket consent to audible output or Exclusive capture.
- Consent includes endpoint friendly name, direction, format, selected channel, maximum duration, reservation and audible-output risks.
- Default button is Cancel for potentially audible output, Start explicitly chosen.
- Confirmation must be bound to an immutable request fingerprint; changing endpoint/format/channel/duration invalidates confirmation.
- Emergency Stop is always accessible while a test is active, even with another inspector open.
- No UI Stop should claim instant success until core confirms Stop/cleanup.

## Asynchronous behavior
- Discovery never freezes the UI. Use progress and cancellation where feasible.
- Active test has one active-session ownership token, prevents two simultaneous modes on the same managed probe context.
- Late/stale events from a previous session are discarded by session ID; no mixing old and current meter values.
- Completion is a terminal core event with actual cleanup and error details. If process crashes/disconnects, show `Сеанс потерян · состояние устройства неизвестно` and explicitly do not show green completion.
- Closing a window with an active test first sends Stop, then waits for termination/cleanup, with bounded escalation and visible failure semantics. Define exact process-stop behavior in Core integration ADR.

## Copy and localization
- Interface language v1: Russian. Keep technical tokens `WASAPI`, `Exclusive`, `PCM16`, `HRESULT`, `Capture`, `Render` where precision matters.
- Friendly terms: `Входы`, `Выходы`, `Матрица форматов`, `Уровни по каналам`, `Начать проверку`, `Остановить`, `Запись файла не ведётся`.
- Do not translate `READY` into ambiguous `Поддерживается`: say `Готов к запуску` with tooltip `Initialize + event setup succeeded; Start not called`.
- Warn about unsupported formats separately from busy device / access conflict / invalidated hardware.

## Error messaging
`Не удалось занять устройство: оно может использоваться другой программой.` [details: HRESULT].
`Формат не поддерживается в точном режиме Exclusive.` [candidate spec].
`Поток завершился с предупреждениями: [N] разрывов, [N] ошибок временных меток.`
`Проверка была остановлена. Отчёт содержит только фактически наблюдённые данные.`
