# 04. Screen map and wireframes

## Top-level navigation
1. **Устройства** (landing): Capture/Render filters, selection and passive facts.
2. **Форматы**: single-endpoint Exact Exclusive matrix, optional explicitly confirmed Initialize-only validation.
3. **Входные каналы**: topology and opt-in active Capture meter (C1).
4. **Выходные каналы**: select channel and opt-in short tone (C2 exists in code; GUI integration pending).
5. **Отчёты**: local diagnostic results, export and method/limits; no automatic profile certification.
6. **Диагностика**: detailed runtime log and error stages (may be a drawer rather than separate page).

Sidebar is *navigation*, not a substitute for device selection. Current endpoint context stays obvious at the top of each work area. A selected Capture endpoint never silently carries over as the Render endpoint.

## Shell layout (desktop, diagrammatic)
```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ KLIDAN  Audio Probe                            Только локально     [Справка] │
├──────────────┬────────────────────────────────────────────────┬──────────────┤
│ Устройства   │  [Выбранное устройство] Capture / Render       │ Инспектор    │
│ Форматы      │ ┌────────────────────────────────────────────┐ │              │
│ Входы        │ │  Содержимое выбранной вкладки               │ │ Evidence     │
│ Выходы C2    │ │  матрица /  индикаторы / тесты              │ │ HRESULT      │
│ Отчёты       │ │                                            │ │ Подробности  │
│              │ └────────────────────────────────────────────┘ │              │
├──────────────┴────────────────────────────────────────────────┴──────────────┤
│ Нет активного теста                       [Экспорт]  [Начать] / [Стоп]      │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Landing: devices
- Left main area: categorized list `Входы` / `Выходы`, search by friendly name, offline/unavailable state.
- Right or lower pane: detailed selected endpoint, Windows mix format, channel count/mask, driver metadata when obtainable.
- High-visibility badge: `Пассивный просмотр · поток не запущен`.
- Do not show `Compatibility PASS` based solely on endpoint existence.

## Format matrix
- Header: name and direction, `Пассивная проверка` / `Проверить Initialize`.
- Table 6 format/channel rows × four rate/period columns; at least one view with all 24 candidate/period outcomes.
- Evidence inspector displays exact IsFormatSupported/Initialize HRESULTs, alignment retry, actual buffer frames, event registration, cleanup.
- Before `validate`: explicit consent mentioning temporary Exclusive reservation; no Start or audio transfer.
- Export CTA after run; export of partial failure results allowed with truthful status.

## Capture meter
- Top: selected Capture endpoint, `48 кГц`, `PCM16`, mono/stereo, Default/Minimum, duration.
- Main: channel strips; for each: `Ch 1`, short-window peak bar, RMS, session peak hold, clip count.
- Secondary: timeline of Capture events and stream integrity; integrity is visually distinct from level meters.
- Bottom: consent → Start → in progress `Стоп` → Completed/Interrupted + report.
- If a stream started but capture frames are zero, show `Нет данных`, not `Тишина`.

## Output test (D2 synthetic shell; live C2 integration pending)
- Selected Render endpoint only, channel choices 1/2 initially.
- Repeated low-level 997 Hz gated tone with a bounded 1–10 s duration.
- Structural text: `Тестовый канал: Windows Ch 1`; `Другие каналы: цифровая тишина`; `Физический выход: неизвестно`.
- Separate optional field for *user-confirmed* physical connection after test; default unknown. Do not infer automatically.

## Reports
- Filter by `type`, date, selected device *local-only mapping* if saved, `PASSIVE_QUERY`, `INITIALIZE_ONLY`, `LIVE_CAPTURE_METER`, `OUTPUT_TEST` (C2 code exists; GUI integration pending).
- Evidence detail: observed facts, missing fields, caveats, method.
- `Экспорт безопасного JSON`: preview lists excluded sensitive fields; refuse overwrite or prompt a new filename.
- Future profiles are intentionally a **different object** from an individual run report.

## Responsive
- ≥1440 DIP: optional visible right inspector.
- 1024–1439: inspector collapses into a drawer; table horizontally scrolls.
- At minimum 1024×720: accessible Start/Stop and endpoint name stay visible; no hidden test controls.
