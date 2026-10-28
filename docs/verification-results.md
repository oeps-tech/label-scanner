# Data Matrix migration verification

Command labels update (2026-10-28): **746 core + 319 desktop/reader assertions pass** using `scripts/test.ps1 -Configuration Release -SkipRecognition`; solution builds with no warnings or errors. Coverage includes all five initial C command examples with independently calculated Python zlib checksums, extensible names and exact whitespace/case preservation, payload/text boundaries, invalid commands falling back to unchanged X, strict two-field JSON and the exported SDK's C API. Real WebSocket tests deliver repeated commands and mixed versions on one connection; source-routing tests verify C on both ports, reader repeats without suppression, camera suppression and corrupt-command fallback. The test GUI switches between C and material/raw data without stale fields; all five label-reference examples match live classification. Physical scans were not part of this run; recognition code was unchanged.

Earlier dual-source update: **635 core + 304 desktop/reader assertions passed**. Camera and reader listeners are independent (defaults 8765/8766). Tests exercise first reader-client connection, multiple clients, source isolation even with camera enabled, one-minute idle disconnect independent of camera clients, debug override, connection-loss retry, port collision rejection, occupied-port rollback, port swapping and legacy-port migration. Repeated 1/2/3/X reader events bypass suppression; camera filtering remains intact. Fast CRLF framing tests cover 100 identical frames in one read, delayed chunks, split Unicode/delimiters, error resynchronization and cleanup; a real WebSocket test delivers 30 identical scans to each of two reader clients without sending any to a camera client. Fast mode deliberately treats embedded CRLF as a separator; multiline quiet-gap mode remains available. The checks below also record earlier implementation stages.

Earlier automated validation, including label versions 1/2/3/X. The original v0.1.0 release supports only version 1; these source changes require a new local build or release. Release artifact checks are recorded in `artifacts/release-verification.json` during packaging.

- Real Data Matrix and QR decoding in one frame; rotated input works without markers; Aztec and Code128 are also exercised.
- Real worker subprocess: decode, preview enable/disable, disconnect/reconnect, and no worker filesystem output.
- Slow-decoder regression: preview continues independently.
- CRC-32 standard/printer vectors; all allowed PN/lot/quantity shapes; corruption, wrong version, invalid grammar and invalid checksum rejection.
- Exact four-field JSON with string quantity, including high precision and zero.
- Version 2 CRC printer vector, internal ID boundaries, printed document_name/OUT preservation, strict five-field JSON and malformed-message rejection.
- Mixed version 1/version 2 Data Matrix decoding in the same frame and on rotation; both schemas on one SDK connection, including JSON escaping at the payload size limit.
- Per-payload 500 ms boundaries and interleaved different payloads.
- Same 500 ms policy for version 2, without observations extending the interval or one version blocking another.
- All eight supplied Worldway/Farnell/Mouser/DigiKey fixtures, with both literal separator notation and actual ASCII controls; exact PN/MPN/quantity results.
- Explicit supplier nulls, OEPS token boundaries, MPN punctuation, distinct P/1P/30P/3P identifiers and zero/precise quantities. Both supplier syntaxes exercise valid quantity AND (valid OEPS PN OR nonempty MPN), with exact X fallback for every failure, including malformed/duplicate records.
- Raw X round trips for tabs, CR/LF, NUL, RS/GS/EOT, Unicode, empty/long text and literal separator notation through real barcode images, private IPC and the WebSocket SDK.
- The actual server frame-processing path sends supplier/X messages, forwards missing quantity or both missing identifiers as X, preserves original text and suppresses repeats for every version.
- Client-driven camera demand: idle startup, last-client timeout at exactly 60 seconds, reconnect and always-on override.
- Real WebSocket broadcaster and shipping client SDK: multiple clients, no greeting/backlog, data-only messages, disconnect counts, no image endpoint.
- Reusable client DLL: this-computer override, explicit IP, exact JSON event, close/reconnect, invalid-port rejection and invalid messages withheld from callbacks. The example also compiles against only the exported DLLs.
- WPF layouts, saved-setting migration, exact quantity display, no new audit/model storage and clean shutdown.
- Switching client displays between material and Odoo labels without stale fields; both label reference examples validate against their JSON schemas.
- Supplier and raw display switching without stale values; all four label reference entries agree with live routing and JSON.
- Update staging, activation after readiness, saved-version recovery, invalid checksums, incompatible runtimes, missing worker files, unsafe archive paths and stable-release selection.

Physical C930e capture performance and print-quality acceptance must be checked on the running station. Tests do not establish an optical misread rate.

Bluetooth reader addition (local build): 635 core assertions and 68 desktop/reader assertions passed. Reader tests cover outgoing SPP filtering, fragmented UTF-8/control preservation, scan framing errors and recovery, remembered-device COM reassignment, connection failures/loss, shutdown, actual WebSocket delivery for 1/2/3/X, and shared reader/camera duplicate suppression. The physical BCST-73 was discovered as COM3 (incoming COM4 excluded); opening and closing its SPP connection succeeded after waking it. Physical barcode contents and reader configuration still need an operator scan test. The serial quiet-gap limitations are documented in the operator guide.

BCST-73 ending correction: a physical 237-byte scan was valid UTF-8 and ended in `1E 04 0D 0A` (supplier RS/EOT followed by reader CRLF). A subsequent live test exposed pauses between chunks exceeding 250 ms. The receiver now removes one CR or CRLF ending, retains unterminated chunks for up to five idle seconds, and reports ending errors separately from encoding errors. All 635 core and 91 desktop/reader assertions pass, including payload trailing-newline preservation, fragmented CRLF, delayed supplier chunks and version 3 classification. The final live retest received no scan; an operator scan in the reopened GUI remains to be confirmed.
