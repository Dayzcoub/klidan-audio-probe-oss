# Hardware Compatibility Matrix

This matrix records operator evidence only. It is not a certification list,
does not promise driver compatibility, and does not measure physical RTL.

| Device | Evidence scope | Result |
|---|---|---|
| Audient iD4 | Windows WASAPI Exclusive, 48 kHz PCM16; operator evidence summarized for this public matrix | Primary 4 ms / PRO_AUDIO; Fallback 7 ms after software re-evaluation |
| DEXP U700 | Windows WASAPI evidence summarized for this public matrix | 4 ms / PRO_AUDIO; Fallback 6 ms / OFF |
| Bloody G501 | Windows WASAPI evidence summarized for this public matrix | 6 ms / PRO_AUDIO; Fallback NONE |

The public snapshot must not include raw QA archives, endpoint IDs, serials,
user paths or machine identifiers. AudioHost is not certified by these rows.
