# 07. Accessibility and design QA

## Usability requirements
- Primary actions usable with keyboard alone, tab order follows visual order, focus visible with existing `AccentFocusBrush=#5EC7FF`.
- All status changes include text/icon, not only color. Red-green color differentiation is not a requirement.
- Default text at least 14 DIP; compact diagnostic metadata at least 12 DIP with scaling; avoid opaque truncation of endpoint names/HRESULTs.
- Controls meet reasonable target size 40–48 DIP, critical Stop 56 DIP; no tiny click-only meter bars.
- Windows DPI 100/125/150/200%: layout reflows, no cropped dialog/Start/Stop, at minimum supported dimensions.
- Screen reader labels include units and scope: `Канал 1, пиковый уровень последнего окна, минус 18 децибел относительно полной шкалы`.
- Reduced motion and high contrast should preserve content and status. Provide a text list alternative to charts.
- Tabular digits in meters; explicit sample-rate, bits, channel count; copyable HRESULT.

## Empty vs invalid vs silent
- No endpoint selected: neutral empty state.
- No live run performed: `Не проверялось`.
- Initialized but no frames: `Нет данных`.
- Observed all-zero PCM: `Тишина`/`−∞ dBFS`.
- Critical audio failure: `Ошибка`, exact stage and HRESULT.
- Completed with stream anomalies: `Завершено с предупреждениями`; still allow export.

## Acceptance matrix
| Test | Fixture/driver | Acceptance |
|---|---|---|
| Device list empty/disconnected | fake endpoint service | neutral state, no crash, no audio API activation |
| Exclusive matrix 24 rows | fixture exact statuses | support vs init vs ready differentiated, full HRESULT details |
| Capture: loud → silence | synthetic meter snapshots | live bar falls; session hold remains |
| Capture: no full window | fixture | no fabricated zero |
| Capture: finished with warning | fixture | lifecycle and quality separately visible |
| Render consent cancelled | fake service | no invocation; no signal |
| Render channel selected | future C2 fake worker | selected channel labeled; all others zero in synthetic frames |
| Stop while active | fake delayed stop ack | UI shows STOPPING until ack, cannot restart prematurely |
| Worker crash/device loss | fake disconnected IPC | unknown/failed, never green completed |
| Export | sanitized fixture | reportId UUID, no endpoint ID/path, honest evidence source |
| Window scaling and keyboard | Windows UI automation/manual | no occluded stop, usable tab order, accessible labels |

## Testing policy
- Automated UI tests depend on **mock Core**, never on user sound devices.
- Snapshot rendering/presentation tests are deterministic; no screenshot comparator may certify audio correctness.
- Keep unit, integration, visual and hardware QA categories separate.
- Review UI text for excessive use of `PASS`, `CLEAN`, `READY`, and any implied certification.
- Hardware test results stay in separate operator evidence records; do not invent them to close a UI task.

## Performance budget (design target, not measured)
- UI thread responsive during asynchronous discovery and scans.
- Meter rendering at 10–20 fps from last immutable state, not on each WASAPI packet.
- Avoid unbounded event logs and data grids without virtualization; default retain a bounded recent timeline plus explicit export.
- Reduced animation when window loses focus, without affecting audio service lifecycle.
