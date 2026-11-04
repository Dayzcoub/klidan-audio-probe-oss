# 02. Design foundations

## Brand alignment
Use the **actual KLIDAN WPF resource values**, not an improvised new palette. The values below come from main-app `DesignTokens/Colors.xaml`; other semantic names are **proposed for Audio Probe**.

| Existing brush | Hex | Audio Probe role |
|---|---|---|
| `SurfaceRootBrush` | `#0B1020` | application background |
| `SurfaceOneBrush` | `#121A2D` | large panes |
| `SurfaceTwoBrush` | `#19243B` | cards, data rows |
| `SurfaceThreeBrush` | `#22304B` | selected/hover surfaces |
| `TextPrimaryBrush` | `#F4F7FC` | titles, primary values |
| `TextSecondaryBrush` | `#B8C2D8` | labels and explanations |
| `TextMutedBrush` | `#8490A8` | helper values and disabled text |
| `AccentPrimaryBrush` | `#FFB22C` | primary CTA, selected tab |
| `AccentFocusBrush` | `#5EC7FF` | focus ring, keyboard navigation |
| `AccentDangerBrush` | `#FF6B6B` | failure, stop-critical (contextual) |
| `StatusSuccessBrush` | `#3FBF8F` | completed with valid evidence |
| `BorderBrush` | `#33425F` | subtle boundaries |

**Proposed additions, not existing KLIDAN canonical tokens:** `Status.Info=#5EC7FF`; `Status.Unknown=#8490A8`; `Meter.Signal=#3FBF8F`; `Meter.Warning=#FFB22C`; `Meter.Clip=#FF6B6B`. Never use color as the sole meaning.

## Typography (proposed)
- App body: Segoe UI; strong Cyrillic support; do not distribute font files.
- Numbers in meters and timings: tabular figures (`tnum`) and monospaced formatting where practical.
- `Heading.1 24/32 semibold`, `Heading.2 18/26 semibold`, `Body 14/21`, `Body.Small 12/18`, `Metric.Large 28/34 semibold`, `Mono 12/18`.
- Labels state the measured quantity and unit: `Peak, dBFS`, `Буфер, кадры`, `Период, мс`.
- Show digital silence as `−∞ dBFS` if observed; missing data as `Нет данных`, not `0.0 dBFS`.

## Geometry and sizing
- Spacing scale inherited from UI Bible: `4, 8, 12, 16, 24, 32, 48` px / DIP.
- Radius: 4/8/12/16. Cards should not look like soap bubbles.
- Control height: 40 compact, 48 default, 56 for the prominent Stop/critical action (the main app currently has `ControlHeight=56`).
- Window reference: **1600×900**, minimum proposal **1024×720** with collapsible inspector/sidebar. Test at 1366×768 and Windows 100%, 125%, 150% scaling. Do not silently inherit the game editor's 1366 minimum into the probe; this is a compact utility.
- Sidebar: 224 DIP on wide layouts, icon+tooltip compact variant on smaller widths. Details inspector: 320–360 DIP, collapsible.
- Safe content width: flexible; tables must support horizontal scroll rather than cropping HRESULTs.

## Motion
- `instant 0 ms`, `fast 100`, `normal 180`, `slow 280`. Paused/reduced-motion mode disables nonessential transitions.
- Meter refresh is visual sampling only, never control of audio timing. Suggest 10–20 fps GUI redraw with latest immutable snapshot, independent from Core processing.
- Active test Stop must respond immediately, regardless of animations.

## Digital meters
- Display range proposal −60…0 dBFS with separate `CLIP` at full-scale sample observations. Below −60: visible low-signal hint, retain exact numerical value in details.
- Peak = current 100 ms (or configurable approved window) snapshot; RMS = same window; hold = session maximum. Distinguish with text and shape, not merely hue.
- In the absence of a complete window, `—`/`Нет полного окна`; an actual all-zero window is `−∞ dBFS / Тишина`.
- A meter is not a calibrated LUFS/VU/acoustic SPL instrument; never call it one.

## Icon language
- One outline icon family, fixed stroke and sizes 16/20/24. Provide text beside all critical actions (`Начать проверку`, `Стоп`, `Экспорт`). Do not mix emoji into production chrome.

## Style source-of-truth
`design-tokens.json` is a portable **design reference**, not immediately a XAML or CSS dependency. Implementation must generate or maintain canonical WPF resources (after framework ADR) and record mapping to main KLIDAN tokens. Avoid direct source-copy dependency on the game app.
