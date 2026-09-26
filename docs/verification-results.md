# Data Matrix migration verification

Automated validation for the v0.1.0 Windows release. Release artifact checks are recorded in `artifacts/release-verification.json` during packaging.

- Real Data Matrix decoding of two symbols in one frame; QR is ignored; rotated input works without markers.
- Real worker subprocess: decode, preview enable/disable, disconnect/reconnect, and no worker filesystem output.
- Slow-decoder regression: preview continues independently.
- CRC-32 standard/printer vectors; all allowed PN/lot/quantity shapes; corruption, wrong version, invalid grammar and invalid checksum rejection.
- Exact four-field JSON with string quantity, including high precision and zero.
- Per-payload 500 ms boundaries and interleaved different payloads.
- Client-driven camera demand: idle startup, last-client timeout at exactly 60 seconds, reconnect and always-on override.
- Real WebSocket broadcaster and shipping client SDK: multiple clients, no greeting/backlog, data-only messages, disconnect counts, no image endpoint.
- Reusable client DLL: this-computer override, explicit IP, exact JSON event, close/reconnect, invalid-port rejection and invalid messages withheld from callbacks. The example also compiles against only the exported DLLs.
- WPF layouts, saved-setting migration, exact quantity display, no new audit/model storage and clean shutdown.
- Update staging, activation after readiness, saved-version recovery, invalid checksums, incompatible runtimes, missing worker files, unsafe archive paths and stable-release selection.

Physical C930e capture performance and print-quality acceptance must be checked on the running station. Tests do not establish an optical misread rate.
