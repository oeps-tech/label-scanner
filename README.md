# OEPS Scanner

Windows server and test client. The server reads Data Matrix ECC 200 directly from camera frames, validates a versioned payload and CRC-32, and broadcasts four data fields over WebSocket. No ArUco markers, OCR models, label screenshots, evidence database, feedback or scan-history files are used.

## Local run

For installation, download the Windows x64 MSI from [GitHub Releases](https://github.com/oeps-tech/label-scanner/releases/latest). It installs both server and test client with separate shortcuts and includes the .NET and Python runtimes. Both apps show their version at the bottom right. The launcher offers updates on startup; offline operation uses the installed version. Close both apps before updating. The release also includes a client SDK ZIP for integration into other C#/.NET applications.

Run `Run.cmd` to build and open both applications. No model download is required. To prepare/build without opening windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -PrepareOnly
```

Initial dependencies: `scripts/setup.ps1` (pinned .NET SDK and Python runtime). Tests: `scripts/test.ps1`. The existing per-user launcher/update architecture remains; no new release is required for local development.

## Label payload

```text
<label_version>*<OEPS_PN>*<Lot>*<quantity>*<checksum>
1*OEPSA010123*0926-TRAY-Q5R2*0*AE1BA040
```

- Check version first: only literal `1` is supported.
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

## Client protocol

Connect to `ws://<server>:8765/labels`. Each server text message is exactly:

```json
{"label_version":1,"oeps_pn":"OEPSA010123","lot":"0926-TRAY-Q5R2","quantity":"0"}
```

Quantity is a **string**, preserving precision and formatting. No checksum, timestamps, IDs, images, envelope or greeting are transmitted. There is no acknowledgement, feedback or replay mechanism. Disconnected clients miss readings; nothing is stored for later delivery. The test client displays only the latest data in memory and reconnects while connection is requested. [Client DLL integration](docs/client-library.md) describes `ConnectAsync(ip, port, thisComputer)`, `JsonReceived` and `CloseAsync()`. Run `scripts/build-client.ps1` to build a local DLL bundle. `examples/Scanner.LabelConsumer` demonstrates its use.

Each exact valid payload has its own 500 ms cooldown after being queued to at least one client. Observations inside the cooldown do not extend it. Different payloads may be sent immediately, including multiple codes in one frame. A label remaining visible may therefore repeat every 500 ms; receivers should not equate each message with an inventory transaction.

## Camera and debug

Choose the camera and capture settings on the server. The camera opens when a data client connects, remains open while any client is connected, and closes after one minute with no clients. **Keep camera connected (debug)** overrides this policy. Switching the override off starts the idle grace period if no clients are present. Camera failures are retried while demand exists.

The server starts in a fixed 900 × 350 compact view. **Show details (debug)** reveals the preview, latest JSON and diagnostics in a resizable window; this preference is saved. Hiding details pauses preview work while scanning and delivery continue.

Local preview is independent of decoding, has no fixed 10 FPS throttle, and shows measured capture and preview-update FPS. In the detailed view, **Enable camera preview** controls local JPEG encoding and display without stopping scanning. Preview images stay in memory and are not sent to clients. Diagnostics show the latest decoded payloads, validation failure stage, expected CRC, cooldown/send result, capture/decode timing and session counters. No scan data is written to disk.

Only connection/camera preferences are saved (`.local/run/server/settings.json` and `.local/run/client/client-settings.json` during local runs). Existing data from the previous implementation is left untouched; the new application does not open it. Close both old applications and rerun `Run.cmd` after updating.

The original `docs/build-brief.md`, labels and mockups are historical reference material. This Data Matrix specification supersedes the original handwriting/evidence workflow. See `docs/protocol.md`, `docs/installation.md` and `docs/verification-results.md`.
