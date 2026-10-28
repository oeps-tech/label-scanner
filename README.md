# OEPS Scanner

Windows server and test client. The server reads barcodes from camera frames or a Bluetooth SPP reader and broadcasts versioned data over WebSocket. Version 1 identifies materials; version 2 identifies Odoo sales orders and deliveries; version 3 extracts supplier data; version C carries commands; version X carries unrecognized raw text. All ZXing-C++ supported barcode formats are enabled, including Data Matrix, QR, Aztec and linear barcodes. No ArUco markers, OCR models, label screenshots, evidence database, feedback or scan-history files are used.

## Local run

For installation, download the Windows x64 MSI from [GitHub Releases](https://github.com/oeps-tech/label-scanner/releases/latest). It installs both server and test client with separate shortcuts and includes the .NET and Python runtimes. Both apps show their version at the bottom right. The launcher offers updates on startup; offline operation uses the installed version. Close both apps before updating. The release also includes a client SDK ZIP for integration into other C#/.NET applications.

Run `Run.cmd` to build and open both applications. No model download is required. To prepare/build without opening windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -PrepareOnly
```

Initial dependencies: `scripts/setup.ps1` (pinned .NET SDK and Python runtime). Tests: `scripts/test.ps1`. The existing per-user launcher/update architecture remains; no new release is required for local development.

## Label payload

Routing order: validate OEPS version `1`, `2` or `C`, parse recognized suppliers as version `3`, then send every other reading as raw version `X`. A supplier label that fails version 3 validation also becomes X; no decoded text is discarded because validation failed. OEPS versions 1/2/C have a 4,096-character payload limit, use single `*` separators and CRC-32/IEEE over exact ASCII bytes before the final separator/checksum. Those inputs are not trimmed or normalized. Versions 3/X are assigned by the server; their barcodes do not need a version prefix or OEPS checksum.

### Version 1: materials

```text
<label_version>*<OEPS_PN>*<Lot>*<quantity>*<checksum>
1*OEPSA010123*0926-TRAY-Q5R2*0*AE1BA040
```

- Label version must be literal `1` for this format.
- Exactly five fields separated by a single `*`. The former double-asterisk format is rejected.
- OEPS PN: `OEPS` plus six digits, or `OEPS` plus one uppercase letter and five or six digits.
- Lot: `MMYY`, `-`, three or four uppercase letters, `-`, four uppercase letters/digits. Month `MM` must be `00`–`12`; year `YY` must be `00` or `21`–`99` (strictly above 20). Zero month and zero year are accepted independently; years `01`–`20` are rejected.
- Quantity: digits, optionally followed by `.` and one or more digits. Zero is valid. Commas, signs, exponent notation and whitespace are rejected. Leading zeros and trailing fractional zeros are preserved.
- CRC-32/IEEE: eight uppercase hexadecimal characters. Calculate over exact ASCII bytes of the first four fields and their single `*` separators. Exclude the final separator/checksum. No trimming, case conversion, newline or normalization.

Python printer reference:

```python
import zlib
payload = f"{label_version}*{oeps_pn}*{lot}*{quantity}"
barcode_data = f"{payload}*{zlib.crc32(payload.encode('ascii')):08X}"
```

`01234567` is a placeholder checksum, not the checksum for the example above. A printer test vector is `1*OEPS010123*0926-TRAY-Q5R2*1` → `455A8C51`. Recalculate the checksum when changing separators; checksums from the previous format are not valid for the new text.

### Version 2: Odoo sales order and OUT

```text
2*<sales_order_id>*<document_name>*<out_id>*<out_number>*<checksum>
2*857*S/2680*1923*WH/OUT/00567*6B6312A1
```

- Exactly six fields including version and checksum.
- `sales_order_id`: internal Odoo `sale.order` ID; `out_id`: internal `stock.picking` ID. Both are digits only, with values from 1 to 2147483647, sent as JSON integers.
- `document_name`: printed sales order number, e.g. `S/2680`. Use Odoo's **`document_name`**, not `name` (`S23661`).
- `out_number`: printed delivery number, e.g. `WH/OUT/00567`.
- Both printed numbers must be nonblank printable ASCII without `*` or control characters. Slashes, spaces and letter case are preserved. No particular sales order or warehouse prefix is required.
- CRC covers the exact first **five** fields and their separators; the example body `2*857*S/2680*1923*WH/OUT/00567` has CRC `6B6312A1`.

Python printer reference:

```python
import zlib
payload = f"2*{sales_order_id}*{document_name}*{out_id}*{out_number}"
barcode_data = f"{payload}*{zlib.crc32(payload.encode('ascii')):08X}"
```

The IDs let a consuming application construct links using its configured Odoo address. The scanner does not connect to Odoo, verify record existence or check that printed numbers match the IDs. The sample IDs are illustrative, not actual records in your database.

## Client protocol

### Version 3: supplier labels

Recognized formats: Worldway's brace-enclosed field list, and the supplied Farnell, Mouser and DigiKey ISO 15434 format 06 labels. All eight supplied examples are kept in `tests/fixtures/supplier-labels.json`.

| Output | Worldway field | ISO 15434 identifier |
| --- | --- | --- |
| `oeps_pn` | `mc` | `P` |
| `mpn` | `pm` | `1P` |
| `quantity` | `qty` | `Q` |

OEPS PN must be a complete whitespace-delimited token matching the same rules as version 1. Missing, invalid or ambiguous OEPS PN becomes `null`. A missing or blank MPN becomes `null`; punctuation such as the comma in `74LVC4245AD,118` is preserved. `P`, `1P`, `3P` and `30P` are distinct identifiers. Version 3 requires **valid quantity AND (valid OEPS PN OR nonempty MPN)**. Quantity uses digits with an optional decimal dot and is sent as a string; zero is valid. Either identifier may be null, but both cannot be missing. Missing/invalid quantity, missing both identifiers, duplicate relevant fields or malformed records cause X fallback with the entire original text.

Actual ASCII RS/GS/EOT delimiters are supported, as are literal `<rs>`, `<gs>`, `<eot>` test notation. The provided DigiKey variants without a closing trailer are accepted. Supplier parsing trims extracted field boundaries; it never changes the original raw string used for duplicate filtering.

```json
{"label_version":3,"oeps_pn":null,"mpn":"74LVC4245AD,118","quantity":"200"}
```

### Version C: commands

```text
C*<command>*<checksum>
C*For Odoo OUT*14C79D4D
```

Exactly three fields; version must be uppercase `C`. Command text must be nonblank printable ASCII, without `*` or control characters. Spaces and letter case are preserved exactly. The complete payload is limited to 4,096 characters (up to 4,085 command characters). CRC-32/IEEE covers the exact ASCII bytes of `C*<command>`; append a single `*` and eight uppercase hexadecimal characters. Invalid structure, text or checksum becomes raw X.

The scanner accepts new command names without a code update. These are initial examples, not a fixed allowed list; the consuming application decides what to recognize and execute:

```text
C*Add Enter and left*775087CB
C*Add Right*2A6624D3
C*For Odoo OUT*14C79D4D
C*For Odoo IN*15941C04
C*Disable*7434FE41
```

Python printer reference:

```python
import zlib
payload = f"C*{command}"
barcode_data = f"{payload}*{zlib.crc32(payload.encode('ascii')):08X}"
```

JSON contains only these two string fields:

```json
{"label_version":"C","command":"For Odoo OUT"}
```

Camera duplicates are suppressed for 500 ms; every reader scan is forwarded, including identical commands. The server and test client display commands without executing them.

### Version X: raw fallback

```json
{"label_version":"X","raw":"OEPS-1234\tsecond field\r\nnext line"}
```

Only the original decoded text is sent; no checksum, prefix, content validation, trimming or separator rewriting is applied. Tabs, CR/LF, RS/GS/EOT, Unicode and asterisks survive JSON round trips; JSON escape sequences are restored by the client JSON parser. The barcode text need not start with `X*`. Every code that does not pass versions 1, 2, 3 or C uses X, including recognized suppliers that fail validation. Clients decide whether to use X.

### Connection and schema

Connect to `ws://<server>:8765/labels` for the camera or `ws://<server>:8766/labels` for the barcode reader (both ports are configurable). Each port sends only its own source. Each server text message contains only the fields for its version. Version 1:

```json
{"label_version":1,"oeps_pn":"OEPSA010123","lot":"0926-TRAY-Q5R2","quantity":"0"}
```

Version 2:

```json
{"label_version":2,"sales_order_id":857,"document_name":"S/2680","out_id":1923,"out_number":"WH/OUT/00567"}
```

Version 1/3 quantity is a **string**, preserving precision and formatting. Version 2 record IDs are JSON integers and printed numbers are strings. Versions 1/2/3 have numeric `label_version`; commands and raw fallback use strings `"C"` and `"X"` respectively. No checksum, timestamps, images, envelope or greeting are transmitted. There is no acknowledgement, feedback or replay mechanism. Disconnected clients miss readings; nothing is stored for later delivery. The test client displays only the latest data in memory and reconnects while connection is requested. [Client DLL integration](docs/client-library.md) describes `ConnectAsync(ip, port, thisComputer)`, `JsonReceived` and `CloseAsync()`. Run `scripts/build-client.ps1` to build a local DLL bundle. `examples/Scanner.LabelConsumer` demonstrates its use. Update both client DLLs for version C: previous DLLs reject unsupported versions.

For camera readings, every label version, including X and future versions, shares the same duplicate policy: each exact decoded payload has its own 500 ms cooldown after being queued to at least one client. Observations inside the cooldown do not extend it. Different payloads may be sent immediately, including different versions and multiple codes in one frame. Whitespace differences count as different raw payloads. A label remaining visible to the camera may therefore repeat every 500 ms. Barcode-reader readings have no duplicate suppression: every scan, including immediate identical repeats, is sent to reader clients. Receivers should not equate camera observations with inventory transactions.

## Camera and debug

Choose the camera and capture settings on the server. The camera opens when a data client connects, remains open while any client is connected, and closes after one minute with no clients. **Keep camera connected (debug)** overrides this policy. Switching the override off starts the idle grace period if no clients are present. Camera failures are retried while demand exists.

The server starts in a fixed 900 × 530 compact view. **Show details (debug)** reveals the preview, latest JSON and diagnostics in a resizable window; this preference is saved. Hiding details pauses preview work while scanning and delivery continue.

The **Barcode reader** section supports paired outgoing Bluetooth SPP COM ports, including the Inateck BCST-73. Select the reader once; connecting a client to the reader WebSocket port opens it automatically. Each source closes after one minute without its own clients and has a separate **Keep connected (debug)** override. The camera never receives reader-port demand. The remembered reader is resolved again if its COM number changes. Both sources share classification and JSON schemas; the client DLL selects the source using its existing port argument.

Reader setup: UTF-8 and **Add Enter (CR+LF)**. **Fast scans (CR+LF)** is on by default: it separates every CR+LF-delimited reading immediately, including multiple identical readings in one Bluetooth packet. Embedded CR+LF is also a separator in this mode. Disable fast mode while disconnected to preserve multiline payloads with the existing 250 ms quiet-gap mode. The 500 ms duplicate filter never applies to reader scans in either mode. See the [reader operator instructions](docs/operator-guide.md#bluetooth-barcode-reader).

Local preview is independent of decoding, has no fixed 10 FPS throttle, and shows measured capture and preview-update FPS. In the detailed view, **Enable camera preview** controls local JPEG encoding and display without stopping scanning. Preview images stay in memory and are not sent to clients. Diagnostics show the latest decoded payloads, validation failure stage, expected CRC, cooldown/send result, capture/decode timing and session counters. No scan data is written to disk.

Only connection/camera preferences are saved (`.local/run/server/settings.json` and `.local/run/client/client-settings.json` during local runs). Existing data from the previous implementation is left untouched; the new application does not open it. Close both old applications and rerun `Run.cmd` after updating.

The original `docs/build-brief.md`, labels and mockups are historical reference material. This barcode specification supersedes the original handwriting/evidence workflow. See `docs/protocol.md`, `docs/installation.md` and `docs/verification-results.md`.
