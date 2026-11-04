# Laboratory evidence bundle v1

`LaboratoryEvidenceBundleService` exports one explicit completed automatic scan as a `LOCAL_FULL_EVIDENCE` ZIP. It is a Core-only portability primitive; it does not start a Worker, query WASAPI, repeat a scan, or alter the source files.

## Membership

`Prepare(sessionId, directories)` derives the scan summary name from the session GUID and reads only the caller-supplied approved `Scans`, `Profiles`, `PeriodDiscovery`, `CustomDuplex`, `Repeatability`, and `SustainedDuplex` directories. Profile references are confined to `Profiles`. Report paths in JSON are hints only: custom and sustained reports are derived from structured operation IDs, while period-discovery and repeatability series are located by their structured report/run IDs. Friendly names and timestamps never establish membership.

The manifest includes the summary, the exact referenced profile, and only the report files linked by the summary child operations. Reports embedded in a profile or scan summary are documented by the profile/summary file itself; a separately persisted v3 base profile is not required. A complete bundle requires a completed scan/save lifecycle, a validated v4 sustained profile, and unambiguous persisted report membership for every profile evidence reference.

Missing, corrupt, contradictory, synthetic, ambiguous, oversized, or out-of-root evidence produces `INCOMPLETE` preparation and cannot be exported as a complete bundle.

## Manifest v1

`manifest.json` contains schema version, bundle and source session identities, original and bundle timestamps, available assembly version (never a fabricated Git SHA), lifecycle/persistence, profile IDs and schemas, Primary/Fallback evidence identities, archive-relative file roles, byte lengths and SHA-256 hashes, completeness, AudioHost eligibility, Physical RTL status, scope, and limitations. The manifest is the control-plane entry and is intentionally not self-hashed; every evidence entry is hashed. SHA-256 detects content changes but does not prove authorship or certification.

The scope is explicitly `LOCAL_FULL_EVIDENCE`. Bundles may contain raw Windows endpoint IDs and machine-specific data and are not anonymized or safe for public distribution. No sanitized or partial export is provided by this contract.

## Export and verification

`Export(preparation, destinationZipPath)` refuses an existing destination, re-prepares and compares a content fingerprint, reads each source again, writes a temporary ZIP, verifies it independently, and atomically publishes the final file. Temporary files are removed on failure. Source files are never written.

`Verify(bundlePath)` is offline: it needs only the ZIP and Core contracts, not the source directories, Worker, or audio APIs. It rejects unsafe or duplicate ZIP names, missing/unexpected entries, modified bytes, incorrect lengths/hashes, invalid manifest identities, and contradictory summary/profile/report identities. A valid bundle never means AudioHost is certified or Physical RTL was measured; the manifest must retain `NOT_CERTIFIED` and `NOT_MEASURED`.

## Current evidence gap

The automatic scan persists the period-discovery and repeatability series, custom-period reports, sustained reports, scan summary, and final profile. Query/initialize/stream/duplex evidence is embedded in the profile/summary contracts rather than exported as separate standalone files. This is intentional for v1 and should be made explicit to reviewers when inspecting a bundle.

## API sketch

```csharp
var prepared = LaboratoryEvidenceBundleService.Prepare(sessionId, directories);
var zip = LaboratoryEvidenceBundleService.Export(prepared, destinationZip);
var result = LaboratoryEvidenceBundleService.Verify(zip);
```

The automatic scan result screen exposes `Экспортировать результаты` only for the exact completed persisted scan result. The operator chooses a destination, confirms the `LOCAL_FULL_EVIDENCE` privacy warning, and receives a success message only after independent verification. Cancelled dialogs, incomplete scans, missing required reports, source changes, existing destinations, and verification failures leave source evidence untouched.
