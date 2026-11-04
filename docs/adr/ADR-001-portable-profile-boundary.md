# ADR-001 — Portable data contract, Windows-first probing

Status: Accepted for repository bootstrap (2026-10-29).

## Decision
Implement Windows WASAPI diagnostics first in independent C++20 code. Keep device-neutral evidence records and JSON schema versioned, with Windows-specific fields nested behind a backend namespace. Do not embed COM interfaces, WASAPI handles, endpoint IDs as portable hardware identities, or DLL dependencies in reusable profile contracts.

KLIDAN imports/revalidates a report/profile as data, not a binary dependency, automatic transport override or unverified recommendation. Audio Probe runs without KLIDAN.

## Rationale
An Audient iD4 and Behringer FLOW 8 have different Windows endpoint topology and format behavior. The same model can change behavior with driver, sample-rate configuration, firmware and USB operating mode. External Linux UCM or ASIO benchmark profiles are references, not WASAPI guarantees.

## Consequences
- Preserve independent capture/render endpoints, identity evidence and driver versions.
- Separate probe vs measured vs physically certified results.
- Cross-platform backends are future decisions; no speculative scaffolding now.
- Device IDs containing machine-specific information are local only by default.
