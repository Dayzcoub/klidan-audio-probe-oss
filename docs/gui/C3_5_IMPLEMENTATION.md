# C3.5 — physical device selection and simplified scanner

The normal WPF path is now `Select device → Scan → Progress → Result`. Developer Tools retain the endpoint table, exact endpoint selection, manual format/Initialize/Stream/Duplex controls, raw operation evidence and manual profile save.

Physical groups are cached for each accepted Windows discovery snapshot. The normal ComboBox does not rebuild its `ItemsSource` during selection; refreshes replace the snapshot collection and reconcile the selected item by `GroupId` only after membership and identity checks succeed.

## Identity boundary

The native Windows discovery response reads `PKEY_Device_ContainerId` from the endpoint property store when it is available and well formed. A group is confirmed only when Capture and Render endpoints carry the same available Container ID. Friendly names, form factor and Shared Mode format are display/evidence fields; they never establish physical membership.

Endpoint IDs remain exact local command identities and are never shown in the normal operator view. The grouping service reports the identity provenance and verification state. Missing Container IDs produce unresolved single-endpoint groups. Multiple eligible endpoints in one container produce an ambiguous group and expose a small Capture/Render choice on the normal device screen; Developer Tools are not required for this ordinary hardware decision.

Driver Loopback is not treated as an analogue Capture endpoint. The conservative name hint recognizes `Loopback`, `Loop-back`, `Loop back` and `loop_back` case-insensitively after separator normalization; this is an exclusion hint, not physical proof. When one ordinary Capture, one loopback Capture and one Render share a confirmed Container ID, the ordinary pair may be selected automatically. Unknown Capture types are never asserted analogue; loopback-only or otherwise unresolved cases remain partial or require an explicit pair. Windows does not expose a universal analogue-connector mapping through MMDevice metadata, so a name/form-factor match must not be promoted to confirmed physical routing.

Parent device/interface traversal through SetupAPI was investigated read-only, but `DEVPKEY_Device_Parent` was not useful for the SWD AudioEndpoint entries on the validation machine, so it is not used for automatic grouping. If Container ID is absent or contradictory, grouping remains unresolved and the user may run a single-endpoint partial scan or explicitly pair endpoints. A refresh that changes the selected group or makes an automatic pair non-unique invalidates the prepared selection and consent.

On the validation machine, native discovery found 87 endpoints and 17 active endpoints. PnP inspection showed the active Audient Analogue and Loop-back Capture/Render endpoints sharing one Container ID; the analogue Capture is therefore eligible while Loop-back remains excluded by the conservative name hint, and multiple Render endpoints still require an explicit Render choice. This observation is not generalized by friendly name and does not prove analogue connector identity.

When Capture and Render have different Container IDs, the UI keeps them in separate groups. An operator may explicitly pair them after a warning; the resulting plan is `OPERATOR_SELECTED`, retains both exact endpoint IDs and the discovery snapshot, and is never reported as physically confirmed.

The scan still refreshes discovery, verifies exact endpoint membership, builds the existing immutable plan, obtains one Worker-authoritative authorization, executes the existing C3.4 operations, and persists the existing report/profile atomically. No second discovery occurs after session registration.

Profile v3 keeps the native Duplex report unchanged and maps its terminal status to explicit evidence state: `DUPLEX_CLEAN` to `DUPLEX_QA`, `DUPLEX_WARNINGS` to `DUPLEX_WARNINGS`, `FAILED` to `FAILED`, `INCOMPLETE` to `INCOMPLETE`, and `CANCELLED` to `DUPLEX_CANCELLED`. Only `DUPLEX_CLEAN` can enter the provisional recommendation evaluator. If profile derivation fails after a real scan, the scan summary is still written atomically with status `SUMMARY_SAVED_PROFILE_FAILED`; this status never claims that a profile was saved.
