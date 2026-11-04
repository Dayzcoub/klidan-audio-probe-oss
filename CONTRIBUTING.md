# Contributing

Contributions are welcome through reviewed pull requests. Keep changes
narrow, preserve the OS-neutral report/profile boundary, and do not add
runtime dependencies or cloud/telemetry behavior without an explicit design
decision.

Before opening a pull request:

- run the relevant native CTest and GUI Core/Worker checks;
- run `git diff --check`;
- document Windows and physical-QA limitations honestly;
- do not include endpoint IDs, serials, usernames, local paths, recordings or
  machine-specific profiles;
- preserve third-party notices and identify every new dependency and license.

Physical audio tests require explicit operator authorization and must follow
`docs/TEST_SAFETY.md`. Automated tests use synthetic data and cannot establish
hardware capability or physical RTL.
