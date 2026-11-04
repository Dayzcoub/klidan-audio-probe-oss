# 08. Implementation and review gates

**Sequence:** C1/C2 code exists → D0 design baseline approved → D1 Architecture A accepted → D2 fake-data WPF shell implemented → later integration slices.

| Slice | Work | Definition of done |
|---|---|---|
| D0 Design review | Owner-approved v0.1 visual baseline, IA, component list, consent, evidence labels | **APPROVED 2026-10-30**; unresolved proposals and architecture choices remain explicit; no executable UI required |
| D1 Architecture ADR | WPF + native worker + typed IPC vs WPF + native DLL with stable C ABI | **ACCEPTED 2026-10-30:** Architecture A; exact schemas, timeout values and deployment details remain unvalidated |
| D2 UI shell | Navigation, endpoint list, format table, meter mock, outputs mock, report detail | **IMPLEMENTED:** fake snapshots only, minimum-size and DPI declarations in code, no real audio activation; visual interaction review remains outstanding |
| D3 Passive integration | Real endpoint discovery + format support matrix, typed adapter | No Initialize/Start on launch; no console-string scraping; reusable test coverage |
| D4 Initialize-only integration | On-demand validation with consent | explicit single-endpoint selection, correct cleanup/error/provenance |
| D5 Capture meter integration | Run implemented C1 from GUI + live immutable snapshots | Pending GUI work: Stop visible, warning vs clean, bounded stats, no Render; hardware verification also pending |
| D6 Render channel integration | Run implemented C2 from GUI | Pending GUI work: independent consent, selected channel only, silence-on-stop, no Capture; hardware verification also pending |
| D7 Reporting and distribution | JSON report export, local-only settings, portable build | privacy test, no overwrite, clean install/uninstall, no background active stream |

## Branch boundary
The D0 design baseline, D1 approval and D2 shell were delivered on separate branches. Keep future native worker/IPC implementation separate from WASAPI feature work; do not treat the fake client as a live service API.

## Codex implementation brief for docs-only handoff
```
Repository: Dayzcoub/klidan-audio-probe
Objective: Preserve the owner-approved v0.1 visual design baseline and document D0 status.
Keep design requirements separate from the undecided D1 UI/Core architecture.
Copy the complete original kit under docs/gui/ and record status reconciliation/governance.
Do NOT implement UI code, change CMake/native audio, add dependencies or activate devices.
Retain provenance references to main KLIDAN DesignTokens/Colors.xaml and Spacing.xaml.
Add a small PROPOSED, NOT DECIDED ADR stub for the UI/Core boundary.
Validate links, token JSON syntax, document consistency, Russian copy, and git diff --check.
Commit/push in isolated docs branch; no PR or merge.
```

## Open decisions
- Native Worker command/service extraction; typed IPC schema and protocol negotiation; timeout values; deployment details. Architecture A is accepted, while those implementation contracts remain proposed.
- Runtime verification of minimum window sizing and Per-Monitor-V2 behavior at supported Windows DPI scales.
- Where to save local reports and whether to keep a local report index (never auto-sync cloud).
- Keyboard shortcuts and localization scope beyond Russian.
- Whether physical channel mapping is supported as operator-entered local notes in v1 or later.
- Whether to expose experimental RTL screens in first GUI; default recommendation: hide behind Advanced/Experimental until reliable.
