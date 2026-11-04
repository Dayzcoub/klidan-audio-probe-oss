# Public research leads (not imported profiles)

These references guided architecture discussion; **no ready-made Windows WASAPI Exclusive profiles have been validated or imported here**. Re-check content, licenses and exact vendor/model applicability before using as evidence.

- ALSA UCM: https://github.com/alsa-project/alsa-ucm-conf — Linux USB-audio use-case/channel configurations. Linux mappings do not guarantee Windows endpoint topology or Windows exclusive period support.
- FLOW 8 Linux channel-layout discussion: https://github.com/alsa-project/alsa-ucm-conf/issues/161 — starting point, not Windows device certification.
- JackTrip community audio-interface latency measurements: https://community.jacktrip.org/t/audio-interface-latency-measurements/1118 — published ASIO or other backend RTL data are reference-only; not our WASAPI RTL.
- RetroArch WASAPI source: https://github.com/libretro/RetroArch/blob/master/audio/drivers/wasapi.c — investigate the principles for bounded alignment retry and buffer negotiation, without copying source or licensing assumptions.
- Microsoft Core Audio Exclusive docs: https://learn.microsoft.com/windows/win32/coreaudio/exclusive-mode-streams — platform API rules and limitations.
- DAWbench: https://original.dawbench.com/ and AIXDSP: https://aixdsp.com/round-trip-latency-roundup/ — performance context; not directly importable runtime profiles.

**Decision:** store external reference hints separately from Probe-measured results, with explicit provenance and no automatic application. No scraping or community upload is part of bootstrap.
