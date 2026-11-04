# KLIDAN Audio Probe — GUI Design Kit

**Status:** owner-approved visual/design baseline v0.1, 2026-10-30. D2 implements a runnable fake-data WPF shell; no native worker or live audio integration exists. D1 separately accepts Architecture A; this design baseline is not a release specification. Designed for the standalone repository `Dayzcoub/klidan-audio-probe`.

See [design governance](GOVERNANCE.md) for the D0 decision, maturity corrections, token provenance and versioning rule. D1 Architecture A approved 2026-10-30; see [D2 implementation](D2_IMPLEMENTATION.md).

## Purpose
Provide a consistent visual, interaction, data, accessibility and implementation contract **before** coding the first desktop GUI. Audio Probe is a diagnostic tool, not the game operator console; inherit KLIDAN's visual grammar without copying gaming-specific widgets.

## Source of visual alignment
- Approved upstream design-token source (read 2026-10-30): actual brand surfaces, text, accent, status brushes; private path omitted.
- Approved upstream spacing reference: actual spacing, radii and operator sizing; private path omitted.
- Approved upstream UI guidance: professional dark console; semantic tokens; states; accessible interaction; private path omitted.
- `Dayzcoub/klidan-audio-probe/docs/ROADMAP.md` at C1 commit `b19d221`: shipping vs planned capabilities.

## Files
1. `01_PRODUCT_AND_SCOPE.md` — goals, user tasks, scope, feature maturity.
2. `02_TOKENS_AND_FOUNDATIONS.md` — UI tokens, typography, layout, charts and meters.
3. `03_COMPONENT_LIBRARY.md` — reusable elements and their states.
4. `04_SCREEN_MAP_AND_WIREFRAMES.md` — screen hierarchy and responsive wireframes.
5. `05_INTERACTION_AND_SAFETY.md` — user flows, consent, async/busy/failure.
6. `06_PROBE_CORE_CONTRACT.md` — proposed separation of UI from native test engine.
7. `07_ACCESSIBILITY_AND_QA.md` — design QA acceptance, accessibility, test matrix.
8. `08_IMPLEMENTATION_SLICES.md` — staged delivery after C2.
9. `design-tokens.json` — machine-readable reference values, with inherited vs proposed provenance.
10. `preview.html` — **static illustration** of the design language, *not* a working tester and not wired to devices.

## Ground rules
- **No automatic active probing.** Discovery can be passive; Initialize, Capture and Render require explicit user action, informed consent and a bounded lifecycle.
- `IsFormatSupported` ≠ initialized ≠ stream started ≠ signal audible ≠ device certified.
- Never invent mapping of Windows channel indices to physical sockets.
- Do not collapse errors into green badges, do not present mock data as live device results.
- No live DSP/VST3, no editing of the KLIDAN game app, no physical QA required to approve the design kit.
- Choose the desktop framework through a small ADR rather than silently coupling the native probe to WPF or committing to unsustainable console scraping.

## Review gate
D0 approval covers the visual baseline and documented safety/accessibility requirements. D1 must decide the Desktop UI ↔ Native Probe Core boundary before a UI shell or typed adapter is implemented. Core/audio behavior remains independent of presentation.
