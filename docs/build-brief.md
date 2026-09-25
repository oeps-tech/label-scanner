# OEPS Scanner — complete implementation brief for Codex in VS Code

Build the complete application described below in this workspace. This is an implementation handoff from an earlier design conversation. Do not assume you can access that conversation; this document contains the requirements you need.

The user is attaching four references: the final Server GUI mockup, the updated TestClient GUI mockup, the separate Live Preview window mockup, and the final physical label design. Inspect all four before implementing. The three GUI images define the visual direction; this text defines behaviour when there is a discrepancy. The label image defines the geometry and field locations. Mockup values, camera settings, scores, and addresses are illustrative, not live measurements or configuration constants.

The brief also includes the subsequently agreed **optional live camera preview in the client**. It uses a separate connection on the **same server IP and port** as label events. The preview opens in a separate image-only window, as shown in its own mockup. Keep all controls required by this brief even if an illustrative mockup omits one, including Same computer and Label timeout.

Carry the work through implementation, meaningful tests, Windows build/publish scripts, documentation, and runnable deliverables. Do not stop at a plan, a UI skeleton, or a simulated demonstration. Respect the workspace's existing instructions and preserve unrelated work. Make routine implementation decisions independently and document them. Ask only for a material missing requirement that cannot reasonably be resolved from this brief. If hardware, model access, or the execution platform prevents a particular validation, complete the available work and state the exact limitation without claiming it passed.

## 1. Product and scope

Create an offline Windows label-scanning system for OEPS electronics inventory. A camera attached to the server computer reads a 50 × 30 mm component label containing four ArUco markers, two Data Matrix codes, one QR code, and a three-row area for a printed or handwritten quantity.

Deliver:

1. **Scanner.Server**: Windows desktop GUI, camera control, local recognition, acceptance checks, event display, WebSocket server for label events and optional live preview, feedback processing, and local data collection.
2. **Scanner.Contracts**: shared, versioned message and data definitions.
3. **Scanner.Client**: reusable C# client library without a GUI dependency, suitable for several applications.
4. **Scanner.TestClient**: Windows desktop GUI exercising the actual client library, including optional live camera preview, received event images, and field-specific human feedback.
5. **Scanner.Recognition**: local recognition worker, plus a separate training/evaluation workflow using exported verified examples.
6. Tests, example configuration/template, VS Code launch/build tasks, packaging scripts, and operator/developer documentation.

Server and client must work both on the same computer and on different computers on the local network. Only the server accesses the camera. Start with Windows x64; ordinary USB webcams must work. Inference, capture, storage, and communication must operate offline after installation and model provisioning. Do not require cloud OCR, a subscription, or a GPU for basic operation.

Physical setups discussed:

| Station | Camera height above table | Package thickness | Approximate distance to top label |
| --- | --- | --- | --- |
| A | 30 cm | 1–10 cm | 20–29 cm |
| B | 50 cm | 1–22 cm | 28–49 cm |

The desired search area is approximately 30 × 30 cm. These are design conditions, not a guarantee that a particular webcam resolves the small label throughout that area. Support selectable full-resolution capture, focus controls where available, and changes in label position/scale/perspective. Do not assume a centred label or fixed pixel crop in the unaligned camera frame. Final optical coverage at every package height and recognition accuracy require physical camera/lens testing; software upscaling cannot establish them.

Version shown by both applications: **v0.1.0**. Do not display “Mockup”. This project does not print labels or update Odoo/inventory automatically. Client applications will decide what business action a reading should cause.

## 2. Solution and implementation approach

Use C# and WPF with MVVM for both desktop applications. Use a supported .NET LTS release; .NET 10 is the intended baseline at the date of this brief. Verify and pin compatible dependency versions instead of assuming that examples for a different release work. Keep `Scanner.Client` and `Scanner.Contracts` free of WPF, camera, and recognition dependencies. Package the client library as a NuGet package in addition to normal project references.

Suggested repository structure:

```text
OEPS.Scanner.sln
src/Scanner.Server/
src/Scanner.Contracts/
src/Scanner.Client/
src/Scanner.TestClient/
recognition/
training/
tests/
assets/reference/
config/templates/
scripts/
docs/
.vscode/
```

Use a persistent local Python worker for image processing and trainable quantity recognition. OpenCV ArUco and zxing-cpp are suitable starting components; verify the capabilities of the exact installed versions. Prefer existing libraries to a custom decoder. A practical ownership model is for the worker to own capture and produce preview frames plus analysis results, while C# owns the application state, acceptance policy, local persistence, public socket server, and GUI. If another capture arrangement is materially simpler, document it, but ensure exactly one component owns the physical camera.

Use a documented private IPC channel between C# and the worker, such as framed stdin/stdout or named pipes. Keep it distinct from the public client protocol. Frame messages explicitly, send diagnostics on a separate channel, and keep the process alive between scans. Never start Python or reload model weights for each frame. A normal operator should launch one Windows application and should not install Python packages manually.

Use asynchronous, cancellable operations; do not block the WPF thread during capture, OCR, networking, storage, or model loading. Keep preview, analysis, and per-client output queues bounded. Process fresh frames instead of accumulating a stale backlog. A slow client must not stall camera operation or other clients.

Fan out one camera capture to local preview, recognition, and the subscribed remote previews. A preview subscription must never open the camera again, change scan mode, or trigger recognition. Capture/analysis resolution is independent of the smaller remote-preview resolution. Encode an eligible network preview frame once and share its immutable bytes across subscribers; skip additional network-preview encoding when there are no preview subscribers. Local preview and detection continue normally.

Persist settings and data under an appropriate per-user application-data directory, not the installation directory or repository. Use SQLite for structured metadata and feedback, and ordinary image files for image bytes. Include schema migrations and atomic file writes.

## 3. Reference label, geometry, and field identity

The final physical label is 50 mm wide × 30 mm high. It contains:

| Location | Element | Meaning |
| --- | --- | --- |
| Top left | ArUco ID 0 | Alignment |
| Top right | ArUco ID 1 | Alignment |
| Bottom left | ArUco ID 2 | Alignment |
| Bottom right | ArUco ID 3 | Alignment |
| Left | Data Matrix ECC 200 | Serial number / OEPS component identifier |
| Immediately to its right | QR code | Lot |
| Between lot QR and right Data Matrix | Vertical rectangle divided into three rows | Current quantity override |
| Right | Data Matrix ECC 200 | Printed quantity |

Use the **ArUco DICT_4X4_50** dictionary with IDs 0, 1, 2, and 3. The same IDs repeat on all labels. They are position references, not unique item identifiers. Markers were inserted at about 5 × 5 mm per image in ZebraDesigner; an inserted image can include a white margin. Do not assume that the black detected square itself is 5 mm wide. Derive the marker geometry from the final reference label.

Create a versioned label template containing reference marker corners, code regions, the quantity box, and the three interior row regions. Derive the initial template from the attached label, and provide a simple template setup/calibration facility so small layout revisions do not require editing source code. A diagnostic/template view is sufficient; do not build a general label designer.

Match detected marker IDs and corners to the reference, estimate a homography, check the alignment, and rectify the label. Crop code regions with their quiet zones. Crop quantity rows inside the outer border and horizontal dividers. Fixed printed borders and dividers are not handwriting. Keep the complete quantity box crop, with all three rows, for client review, and retain row crops separately for recognition and training.

Keep measurements, raw camera coordinates, reference coordinates, and display coordinates distinct. Record the transform and ROI locations. Choose and document a canonical rectified resolution, for example 1000 × 600 pixels; upscaling is not evidence of additional camera detail. Use the native capture for decoding/quality checks where appropriate, and preserve original-resolution evidence locally.

Bind field identity to the rectified location, symbology, and content format together. Do not classify solely by string length. Do not combine the left code from one label with the right code from another. For v0.1.0, emit one unambiguous label at a time. If multiple complete labels are visible, show an ambiguity diagnostic and require one to be presented; do not silently select or merge them. This does not require multi-label inventory processing.

The final label uses standard QR. Micro QR may be supported when the chosen decoder and template support it, but it is not a requirement to replace the attached QR or a reason to change the printed label.

## 4. Exact payload validation

Use full-string, case-sensitive matches and ASCII character classes. Do not use Unicode `\d` as a substitute for `[0-9]`. Do not silently fix case, strip internal spaces, replace characters, or normalize a code until it matches. Preserve the original decoded payload separately from parsed values. No generic 12-character serial check remains.

The patterns below are regex bodies: use a true full match. In C#, use `\A` and `\z` around the body; in Python use `re.fullmatch`. Do not accept trailing newlines through a permissive `$` anchor.

### 4.1 Serial number

Every serial starts with **OEPS**. The earlier OESP spelling was a typo and must not be accepted.

Allowed forms:

- `OEPS` followed by exactly 6 digits.
- `OEPS` followed by one uppercase A–Z letter and exactly 6 digits.
- `OEPS` followed by one uppercase A–Z letter and exactly 5 digits.

Regex body:

```regex
OEPS(?:[0-9]{6}|[A-Z][0-9]{5,6})
```

Valid examples: `OEPS010243`, `OEPSA123456`, `OEPSA12345`.

Invalid examples: `OESPA123456`, `OEPSa12345`, `OEPS12345`, `OEPSA1234`, or a value with spaces.

This field is called `serial_number` in the protocol to match the user's terminology. It may identify a component type rather than an individual physical package; never assume package-level uniqueness.

### 4.2 Lot

Format: **MMYY_pack_kkkk**.

- MM: exactly two digits from `00` through `12`. `00` is allowed.
- YY: exactly two digits from `20` through `60`, inclusive.
- pack: exactly one of `REEL`, `TUBE`, `TAPE`, `_BAG`, `_BOX`, `TRAY`, `SPOL`, `OTHR`.
- kkkk: exactly four ASCII uppercase letters, lowercase letters, or digits; preserve their case.

Regex body:

```regex
(?<month>0[0-9]|1[0-2])(?<year>[2-5][0-9]|60)_(?<pack>REEL|TUBE|TAPE|_BAG|_BOX|TRAY|SPOL|OTHR)_(?<lot_id>[A-Za-z0-9]{4})
```

The named-group syntax above is for .NET; use the equivalent syntax if validating in Python. The underscores in `_BAG` and `_BOX` are part of the package value. Therefore `0026__BAG_aB09` and `1260__BOX_X7z2` are valid. Do not parse using a naive underscore split that loses this distinction. `0826_OTHR_SwQ2` is another valid example.

Do not accept a year of `00`, even if it appears in an older screenshot or test photograph. Do not add “must not be a future date” validation: the specified range is the authoritative rule.

### 4.3 Quantity Data Matrix

The first character is always `0`. Only digits and an optional decimal point are allowed. A dot means a decimal separator, never a thousands separator. Allow at most one dot, with digits on both sides. No commas, signs, exponent notation, or spaces.

Regex body:

```regex
0[0-9]*(?:\.[0-9]+)?
```

Examples:

| Raw payload | Numeric value |
| --- | --- |
| `0` | 0 |
| `0150` | 150 |
| `0000001` | 1 |
| `012.5` | 12.5 |
| `00.25` | 0.25 |

Reject `150`, `012.`, `01.2.3`, `012,5`, and negative values. Use invariant-culture decimal parsing. Preserve raw text and represent numeric quantities without binary floating-point rounding. Reject parse overflow instead of truncating. Do not invent a stock maximum; any explicit numeric limits must be documented and configurable where useful.

### 4.4 Quantity box

The same quantity area supports printed numbers and handwriting. Do not require the operator to write the initial value by hand. The box has three rows, numbered **1 = top, 2 = middle, 3 = bottom**.

A recognized row quantity may contain digits and one dot as a decimal separator. The Data Matrix's leading-zero requirement does **not** apply to the box.

Regex body for a recognized row:

```regex
[0-9]+(?:\.[0-9]+)?
```

Zero is a valid quantity and must not be mistaken for empty. A comma must not silently become a dot. If OCR proposes a string that fails the grammar, that row is not a valid recognized quantity.

## 5. Three-row recognition and final quantity selection

Evaluate all three rows separately, excluding their borders. Return a status and supporting measurements for each row:

- `empty`: confidently blank.
- `recognized`: one plausible quantity passes grammar and enabled confidence/quality checks.
- `unreadable`: visible markings or crossed-out content, but no acceptable quantity; the image itself was good enough to inspect.
- `ambiguous`: multiple plausible live quantities or uncertainty about what the marks mean.
- `unknown`: insufficient evidence, unavailable inference, or image quality too poor to distinguish empty from marked.

The final rule is **exactly one readable quantity across the box**, or a confidently blank box with a valid quantity DM. The other rows may contain completely unrecognizable overwritten markings. This supersedes the earlier, stricter rule that any mark anywhere in the box would always block a reading.

| Box result | Quantity DM | Emit a label event? | Selected quantity |
| --- | --- | --- | --- |
| All three rows confidently empty | Valid and passes applicable checks | Yes | DM quantity |
| All three rows confidently empty | Missing, unreadable, invalid, or fails a required check | No | None |
| Exactly one recognized row; other rows empty or unreadable | Any state | Yes, if all other required checks pass | Recognized row |
| No recognized row and at least one row contains marks | Any state | No | None |
| Two or three recognized rows, even with equal values | Any state | No | Ambiguous |
| A required row assessment is ambiguous or unknown | Any state | No | None |

Do not guess that the bottommost or most recent-looking number is current. Do not treat a crossed-out number as valid merely because a text model can transcribe its digits. Add conservative checks for cancellation strokes, multiple candidates, and poor image quality; expose uncertainty rather than manufacturing certainty. A printed quantity and handwriting can be supported by the same workflow, but arbitrary overwrite recognition is not guaranteed.

A valid recognized box quantity always overrides the quantity DM. The quantity DM can deliberately be blacked out. Its absence, invalid format, poor quality, or correction count must not block a valid box-sourced result. Serial and lot remain mandatory. Keep any available DM diagnostics in the event without using an invalid DM value as a fallback.

Do not send rejected/uncertain candidate labels to clients. Show their reasons locally and allow explicitly enabled diagnostic capture for model improvement. Client feedback is still needed because a result that passed the automatic rules may be wrong.

## 6. Recognition implementation and honest confidence

Provide a functioning pretrained local quantity recognizer, not a placeholder. Use an inference-provider interface so models can be replaced. Evaluate a suitable handwriting-capable pretrained model, such as a TrOCR-based model, with separate printed recognition if needed. Choose the actual architecture after testing available models and licensing; a handwriting-only model must not be assumed equally good at printed digits, and a printed-only OCR engine must not be presented as handwriting support.

Keep inference and training preprocessing aligned. Support decimal points, variable digit counts, different writers, printed text, and the row classifications above. Avoid a generative model's tendency to invent digits in empty or blacked-out rows by separating blank/mark assessment from text recognition and retaining an unreadable outcome. Restricting output vocabulary is useful but is not proof that content exists.

Expose raw model scores and their meaning. A score of 0.97 is not automatically a calibrated 97% probability of correctness. Never assign synthetic confidence 1.0 to a code or number because an API did not provide a score. Store `null` and a reason when unavailable. Identify the model version and preprocessing version in each event. Display loading, unavailable-model, and failed-inference states honestly.

The operator application must not silently download models on every launch. Provide explicit provisioning/build scripts and package or locate the compatible runtime weights for offline use. Verify model licenses and include notices. Hardware-free replay fixtures may be used for development, but simulation must be clearly separate from real scanning and not be the delivered implementation.

## 7. Acceptance checks and profiles

Provide independently selectable booleans and numeric inputs as requested. Show **Enabled**, **Current value**, **Threshold**, **PASS / FAIL / UNKNOWN / N/A**, and a reason. Separate hard semantic rules from optional image-quality filters: disabling sharpness or confidence must not permit malformed serial/lot values, multiple live box quantities, or mixing labels.

| Setting | Control and intended measurement |
| --- | --- |
| Sharpness | Enable + minimum score; measure blur/sharpness before any artificial sharpening |
| Contrast | Enable + minimum normalized contrast; document the calculation |
| Glare/overexposure | Enable + maximum affected-area percentage, with a documented practical detector |
| All four ArUco markers | Boolean requirement, enabled initially; report each ID |
| Code layout | Enable + region/position tolerance; basic field-to-label association is always mandatory |
| Code contents | Display serial, lot, and selected quantity validation; mandatory for emitting valid labels |
| Error corrections | Enable + maximum reported corrections; 0 is strict mode |
| Matching frames | Enable + required number of agreeing fresh frames |
| Quantity confidence | Enable + minimum acceptable recognized-row score |
| Empty-row confidence | Enable + minimum score for accepting empty status; ambiguous evidence never becomes empty just because this filter is disabled |
| Event interval | Integer milliseconds, default 500 |
| Internal session gap | Milliseconds before a silent observation session reset; initial default 1000, in advanced settings |

Values in the mockup such as sharpness 120, contrast 0.25, glare 5%, matching frames 3, recognition score 0.90, and empty score 0.98 are starting examples only. Define units, ranges, metrics, preprocessing and normalization before adopting them as defaults. Provide a usable initial profile and explain the tuning process. Show the worst applicable ROI and per-ROI detail. Retain the applied profile snapshot/version with each event.

Check serial and lot regions independently, and check all row regions sufficiently to justify the box decision. Only gate the quantity DM when it supplies the quantity. A blank box has no useful interior edges; use its border or nearby printed structure to assess focus instead of rejecting it for low Laplacian variance. Do not mistake normal white paper for glare just because its pixels are bright. If a proposed glare score cannot be measured reliably, make that limitation visible rather than presenting a meaningless percentage.

Keep the algorithm and resolution used for quality scoring consistent. A sharpening filter must not allow a blurry input to pass a pre-sharpening focus check. If optional enhancements aid decoding, retain the unenhanced evidence and record what was applied.

An enabled, applicable check that returns UNKNOWN blocks acceptance. A disabled check does not. A genuinely irrelevant check is N/A, not a fake PASS. Lack of a quantity model is an operational failure, not proof of a blank box. A recognizer that can still independently establish all rows are blank may use the DM fallback only if the implemented capabilities and diagnostics make that distinction real.

QR and Data Matrix error correction are not accuracy probabilities. Expose corrections only if the chosen decoder actually reports them for that format. A QR correction level such as Q is a property of the printed symbol, not a count of repairs made during the current decode. Use null for unsupported correction telemetry. Keep correction-limit checking disabled by default if unavailable; if the user enables it, show why scanning is blocked. Do not infer zero corrections from successful decoding or pay for a proprietary dependency without the user's authorization. Do not claim “100% confidence” from zero corrections.

If the all-four requirement is disabled, emit only if a tested subset still gives a sufficiently constrained, unambiguous alignment; otherwise fail. Do not treat “checkbox disabled” as permission to guess the crop.

Save and load camera/setup profiles including camera identity, capture controls, template, thresholds, model selection, and interval. Changing a relevant setting must invalidate in-progress matching-frame evidence so old and new configurations cannot be combined.

## 8. Server camera and scan controls

Implement these exact concepts; do not reintroduce Start/Pause/Stop as the primary control model:

| Control/state | Behaviour |
| --- | --- |
| Connect | Open the selected camera and start preview |
| Disconnect | Release the physical camera, cancel pending recognition, stop scanning; retain last frame and last event |
| Single detection = false | While connected, continuously look for valid labels |
| Single detection = true | Keep preview active; wait for Trigger new scan |
| Trigger new scan | Arm one scan; wait for one valid result; generate one label event; disarm and return to waiting |

Connecting in single mode does not automatically scan. Trigger is unavailable while disconnected and disabled while a single scan is already armed. An invalid candidate does not consume the trigger. A trigger can read the same label again and must produce a new reading ID. It must analyze a fresh frame captured after the trigger, not resend the previous event. After success, remain connected and keep preview running. Disconnect cancels an outstanding scan and prevents late worker results from generating events.

Changing from continuous to single mode cancels any automatic scan and waits for a new trigger. Changing to continuous resumes scanning with fresh stability evidence. Use explicit state/generation tokens to reject stale asynchronous results following disconnect, reconnect, mode changes, profile changes, or model updates.

Keep the last detected label crop, quantity crop, values, metadata and its acceptance results visible until a new event replaces them, even after disconnect. Keep current candidate diagnostics separate from that immutable last-event snapshot so a moving preview cannot change the meaning of an old event.

The WebSocket server and feedback handling remain active regardless of whether the camera is connected or scanning. Camera faults and model faults must be visible and must not discard reviewable stored events.

For subscribed clients, live preview runs whenever camera capture is active, including while single detection waits for a trigger, while a scan is in progress, and after a single detection has completed. Disconnecting the camera stops new preview frames, but does not close the label/feedback service or clear the last displayed image. Keep a preview subscription open and idle through an ordinary camera disconnect; resume with fresh frames after reconnect. Do not repeatedly transmit a retained image as if it were live.

### Camera settings cogwheel

Open a Camera settings window with controls supported by the actual device:

- Capture resolution and frame rate.
- Auto/manual focus and manual focus value.
- Auto/manual exposure and exposure value.
- Gain.
- Auto/manual white balance.
- Power-line frequency: 50 or 60 Hz, where supported.
- Preview, Apply, Cancel, and Restore defaults.

Read back actual applied capabilities/settings; do not report a requested resolution as active if the camera rejected it. Disable unsupported controls with explanations. If a control requires restarting capture, handle it explicitly and clear stale analysis. Save settings in the selected profile.

## 9. Server GUI

Match the attached Server mockup: light grey Windows utility background, Segoe UI, thin pale-blue panel borders, white/read-only grey fields, flat blue rectangular action buttons, restrained status colours, small checkboxes, and a compact status bar. Use resizable WPF layout and DPI-aware sizing; do not hardcode a screenshot as the interface. Keep all main controls usable at typical laptop sizes using sensible minimum sizes and scrolling where necessary.

Top: camera selector, cogwheel, Connect/Disconnect, connection state, Single detection checkbox, Trigger new scan, and current detection state.

Network row:

- **Computer IP is read-only** and obtained from the computer's active network interfaces. It is not a configurable text box and need not be a static IP address.
- Update it when network configuration changes. If several usable addresses exist, identify the interface and show/list the actual alternatives. Do not show `0.0.0.0` as the address clients should type.
- One editable Port, default 8765, shared by both endpoints; connected label-client count; event interval, default 500 ms.
- Count label connections as Clients and show preview-subscriber count separately in a small status/detail area. One application opening both connections is not two label clients.
- Hide `ws://`, the `/labels` and `/preview` paths, and WebSocket terminology from the normal operator flow. These are internal to the library/protocol documentation. There is no second preview-port input.
- In a preview/settings area, expose remote-preview frame rate, maximum dimensions and JPEG quality. Keep these independent of camera capture controls and the 500 ms label-event interval; save them with the profile.

Left main panel: camera preview, alignment/ROI overlays, and current candidate diagnostics. Right main panel: **Last detected event**, not Last sent event. It updates whenever a valid event is generated, including when zero clients are connected.

Last detected event contains the exact label and box crops, serial, lot, raw/parsed DM quantity, box quantity and row, selected quantity and source, confidence, capture time, reading ID, data revision/session details, model version, and quality results. Provide View event JSON with image Base64 collapsed or omitted from the text display while retaining the full message in the actual payload. Make zero clients explicit; never claim delivery to clients that are absent.

Acceptance checks panel: enabled states, thresholds and current/last relevant metrics; per-region details available without a large new workflow. Keep thresholds separate from camera exposure/focus controls.

Bottom actions: Load profile, Save profile, Export training data, and Model settings. A template setup facility and optional diagnostic image/replay input can live in an advanced/settings area. No fake database sync indicator from the unrelated source applications.

Status bar: socket listening/fault state, camera state, single/continuous scan state, and **v0.1.0**. Preserve retained images after stopping or disconnecting. Do not show invented frame rate, confidence, version, or successful delivery values.

## 10. Event generation, repetition, and timing

Only **valid label events** are broadcast on the label endpoint. There are **no label_lost, no_label, invalid_label, or camera-state application events**. Clients can wait or apply their own timeout. Normal connection closure/errors and solicited feedback acknowledgements are still necessary protocol mechanics, not label-detection events. The optional preview endpoint separately carries camera frames, including scenes with no valid label; these are not label events and do not run through the label-acceptance rules or reset the client's label timeout.

Continuous mode: produce at most one event in each configured interval, default **500 ms**, even if a reliable label remains in view. This is a maximum emission rate, not a promise that OCR completes twice per second. Continue capture/preview at the supported speed. Every repeated event must have fresh evidence; do not resend an old image merely to maintain the rate. Use a monotonic clock for interval enforcement, and do not queue a burst of old readings after delays. Apply the emission rate globally, not separately per client. Rapid manual triggers must also respect the minimum interval.

Every generated observation has a new UUID `reading_id`. Broadcast the same immutable event and ID to all currently connected label subscribers, and store it even with no clients. A feedback reply references that exact observation. Preview-only connections do not receive label events. Stability from repeated readings is not human confirmation and must never generate training labels automatically.

Use:

- `label_session_id`: UUID for continuous observation of one spatially tracked label.
- `data_revision`: starts at 1 within a session; increments when the accepted decoded/source data changes.
- `unchanged`: true when data matches the previous emitted event in that session.

Compare all canonical decoded field strings, box row statuses and recognized values, selected row, selected numeric quantity, and selected source. Do not include timestamps, confidence fluctuations, images, current quality scores, or UUIDs in the equality comparison. Keep field equality precisely defined in shared code and tests.

Keep a session through brief misses; silently end it after the configured absence gap, explicit camera disconnect, or clear change of label identity/location. There is no public lost event. A new physical-looking label with identical data may be impossible to distinguish; do not promise unique physical package identification. Explicit single triggers always generate a fresh reading ID even if the session/revision stays unchanged.

For matching-frame acceptance, require fresh, consecutive, eligible frames with agreeing data and row selection. Reset on conflicting or invalid evidence, long gaps, and scan configuration changes. Count only frames acquired after a single trigger for that trigger. All data and transmitted image crops in an event come from one selected accepted frame, with earlier frames used only as stability evidence. Do not assemble a label from codes decoded at different times.

Client applications must not interpret repeated label events as repeated inventory transactions. Document this prominently in the library example.

## 11. Public connection and message protocol

Use **WebSocket over TCP** with **one configurable port, default 8765**, and two independent endpoints on the same listener:

| Endpoint | Subscription | Traffic |
| --- | --- | --- |
| `/labels` | Normal label connection | JSON label events with Base64 crops, feedback, and feedback acknowledgements |
| `/preview` | Optional separate preview connection | Binary messages containing live JPEG camera frames and their metadata |

Sharing a port does not share a subscription: a client connected only to `/labels` receives no live preview frames, and a client connected only to `/preview` receives no label events. The two existing label-event crops remain in `/labels` messages whether or not preview is enabled. Do not multiplex continuous video into the label connection and do not introduce port 8766 or any other dedicated preview port.

Use the same IP/host and configured port for both connections. Keep endpoint construction inside the client library; operator UIs expose only server IP/host and port. Each accepted connection has its own bounded output path. Client subscription/disconnection controls whether the server sends that client previews; no global preview broadcast to unsubscribed clients.

- Same computer: connect through loopback, default `127.0.0.1`.
- Other computers: connect to the displayed LAN address.
- Support loopback and LAN clients simultaneously. Bind deliberately; displaying an IP is separate from choosing a listener interface.
- Keep the server hosted by Scanner.Server, not as an extra manually launched service.
- Handle port conflicts and connection failures visibly. Do not silently switch ports.
- Document Windows Firewall setup for a trusted private LAN. Do not automatically expose the app to the public Internet, configure router forwarding, or silently change firewall rules.
- Ordinary trusted-LAN operation is the initial scope. Document the need for TLS/authentication before deployment beyond that scope.

Use one serialized send loop per connection, handle fragmented WebSocket messages, bound message sizes and queues, and define timeouts/cancellation. A reasonable initial maximum label-message size is 64 MiB, configurable and enforced on both ends; apply much tighter limits to feedback and the preview limits specified below. PNG is a good default for lossless label/box crops; never put a full 45–60 MP camera frame in every message. Transport-level ping/pong can be used without adding application lost/presence events.

Use `protocol_version: 1`, explicit message types, UUIDs as strings, UTC ISO 8601 timestamps, and nullable unavailable measurements. Use JSON numbers for scores, counters, dimensions and structural metadata; send quantities as invariant decimal strings, plus original source strings, to preserve precision and leading-zero evidence across languages.

### 11.1 Label event

The following is a coherent proposed contract. Implement and document it consistently in server, contracts, library and test client. Additional diagnostics can be added, but do not silently rename core fields between components. Image payload placeholders below are examples, not literal wire values.

```json
{
  "type": "label",
  "protocol_version": 1,
  "server_instance_id": "321db2b6-2f9e-48f8-a2bb-4f0277063b10",
  "reading_id": "8c7b142e-6f20-4c3d-9a51-27df0386b420",
  "label_session_id": "49d05f98-3d45-4210-90ce-2f3fa937c890",
  "data_revision": 1,
  "unchanged": false,
  "captured_at": "2026-09-11T10:42:18.000Z",
  "emitted_at": "2026-09-11T10:42:18.180Z",
  "template_id": "oeps-label-v1",
  "profile_id": "station-1",
  "profile_revision": 1,
  "model_version": "quantity-v1",
  "preprocessing_version": "1",
  "fields": {
    "serial_number": "OEPS010243",
    "lot": "0826_OTHR_SwQ2",
    "dm_quantity": "0150",
    "box_quantity": "125",
    "box_quantity_row": 2,
    "quantity": "125",
    "quantity_source": "quantity_box"
  },
  "lot_parts": {
    "month": "08",
    "year": "26",
    "pack": "OTHR",
    "lot_id": "SwQ2"
  },
  "dm_quantity_value": "150",
  "box_rows": [
    {"row": 1, "status": "empty", "text": null, "recognition_confidence": null, "empty_confidence": 0.99},
    {"row": 2, "status": "recognized", "text": "125", "recognition_confidence": 0.97, "empty_confidence": 0.01},
    {"row": 3, "status": "empty", "text": null, "recognition_confidence": null, "empty_confidence": 0.99}
  ],
  "quantity_confidence": 0.97,
  "confidence_kind": "model_score_uncalibrated",
  "codes": [
    {"field": "serial_number", "symbology": "DataMatrix", "text": "OEPS010243", "decode_status": "decoded", "format_valid": true, "errors_corrected": null},
    {"field": "lot", "symbology": "QRCode", "text": "0826_OTHR_SwQ2", "decode_status": "decoded", "format_valid": true, "errors_corrected": null},
    {"field": "dm_quantity", "symbology": "DataMatrix", "text": "0150", "decode_status": "decoded", "format_valid": true, "errors_corrected": null}
  ],
  "quality": {
    "passed": true,
    "checks": [
      {"name": "aruco_markers", "enabled": true, "applicable": true, "status": "pass", "value": 4, "threshold": 4, "unit": "markers", "reason": null},
      {"name": "error_corrections", "enabled": false, "applicable": true, "status": "unknown", "value": null, "threshold": 0, "unit": "decoder_reported", "reason": "Not exposed by decoder"}
    ]
  },
  "images": {
    "label": {"mime_type": "image/png", "width": 1000, "height": 600, "base64": "<actual Base64 PNG bytes>"},
    "quantity_box": {"mime_type": "image/png", "width": 300, "height": 330, "base64": "<actual Base64 PNG bytes>"}
  }
}
```

Image dimensions must match the actual encoded bytes. Row and box sizes come from the template, not the example. Serialize every configured quality check and relevant per-ROI detail, not only the two abbreviated checks above. Include raw code position/correction telemetry where available. Distinguish DM `not_found`, `decode_failed`, and `format_invalid`; `not_found` is not the value zero.

When DM supplies the quantity: `quantity_source = "datamatrix"`, `box_quantity = null`, `box_quantity_row = null`, and `quantity_confidence = null` because handwriting confidence is not applicable. Blank detection scores remain available. When an unused DM is invalid, preserve the invalid raw payload under diagnostics and set its usable numeric value to null.

### 11.2 Feedback

Statuses are exactly:

- **1 = confirmed**: the human confirms the selected original reading, including fields they reviewed.
- **2 = rejected**: the reading is incorrect, but no replacement values were supplied.
- **3 = corrected**: one or more explicitly named fields are corrected.

For status 1, the TestClient's Confirm action means confirmation of the whole displayed reading. For status 3, only the named fields become explicitly verified; untouched fields remain unchanged and must not be silently treated as human-verified training answers. Never equate automatic acceptance with either status.

```json
{
  "type": "feedback",
  "protocol_version": 1,
  "feedback_id": "f077fe2a-f2cd-472f-bf67-c09c3df1e255",
  "client_id": "b1a14645-f53f-48c1-95ef-1c59b4a784d0",
  "reading_id": "8c7b142e-6f20-4c3d-9a51-27df0386b420",
  "status": 3,
  "corrections": {
    "box_quantity": "125",
    "box_quantity_row": 2
  }
}
```

For that correction example, assume the referenced original reading proposed `128`. The original event stays immutable; store the correction alongside it. Do not mutate the earlier event or the next model prediction to make it appear that OCR succeeded.

Allowed correction keys: `serial_number`, `lot`, `dm_quantity`, `box_quantity`, and `box_quantity_row`. Values for the four payload fields are strings or explicitly permitted nulls; row is integer 1–3 or null. Derived `quantity` and `quantity_source` are recalculated, not directly edited. Status 3 requires at least one genuine change. Status 1/2 must not carry a misleading correction map.

Support clearing an optional DM/box value explicitly. Clearing a box quantity does not by itself prove every row is empty: do not automatically mark marked/unreadable rows blank or silently authorize DM fallback. Preserve that distinction in the corrected assessment. Require a row selection for corrected handwriting when needed for training, and verify any row/value pairing. Validate corrected serial, lot, and numeric grammar with the same business rules. A corrected DM quantity must include its leading zero. Do not enforce OCR confidence on a human's replacement text.

Reply to the originating client with a feedback acknowledgement containing `feedback_id`, `reading_id`, `accepted`, and a clear error/conflict reason when rejected. Acknowledge only after durable storage. Unknown reading ID, malformed values, unsupported protocol, and conflicting feedback must produce explicit responses, not silent failure.

Make retries idempotent by `feedback_id`. Do not overwrite a prior accepted correction with a conflicting correction from another client. Retain provenance and report the conflict for review. Repeated compatible confirmations can be recorded without corrupting verified values. Feedback may arrive after the camera disconnects or after a newer event was detected. Correlation always uses the specified reading ID, never whichever image is currently live.

Feedback affects verification/training records. It does not silently change future independent predictions, create an inventory transaction, or teach the model immediately.

### 11.3 Optional live preview protocol

Use the camera's current full view, not only the rectified label. This lets the operator position the package even when no marker, code or quantity can yet be read. Use the same orientation as the server's camera preview, preserve aspect ratio, and keep recognition independent of any preview resize/compression. Overlays are optional for v0.1.0; do not bake live overlays into immutable label-event evidence.

Initial remote-preview settings: **5 FPS**, configurable for example from 1 to 15; fit inside **1280 × 720** pixels without stretching or upscaling; JPEG quality **75**, configurable. These are pragmatic starting defaults, not promised performance. For a 4:3 capture, fitting that bounding box gives 960 × 720. The label-event interval remains a separate 500 ms setting. Reducing preview resolution or frame rate must not reduce recognition capture resolution or matching-frame requirements.

Define one complete binary WebSocket message per preview frame. For a simple explicit v1 contract, use:

1. Four-byte unsigned **big-endian** length of the UTF-8 JSON header.
2. That many header bytes.
3. The JPEG bytes for exactly one frame, without Base64.

Example header:

```json
{
  "type": "preview_frame",
  "protocol_version": 1,
  "stream_id": "bfca13d4-6c75-43e3-b5f9-573882ae4c96",
  "sequence": 42,
  "captured_at": "2026-09-11T10:42:18.000Z",
  "width": 960,
  "height": 720,
  "mime_type": "image/jpeg",
  "payload_length": 84321
}
```

Actual dimensions and byte count must match the image. Use a new stream UUID when camera acquisition restarts, with a monotonic sequence within that stream; skipped sequence numbers are normal. Preview frames have no `reading_id`, do not imply a valid label, and never become feedback targets. The JPEG shown above is only described by its example header; actual frame messages contain real JPEG bytes after that header.

Enforce a small bounded header, for example at most 4096 bytes, and a configurable maximum whole preview message, initially 4 MiB. Validate lengths, protocol, dimensions, and image limits before allocation/decompression. Reassemble fragmented WebSocket messages before parsing. The header and image travel together, so metadata from a dropped frame cannot be paired with a later image.

For each subscriber, keep at most the newest pending preview frame plus any send already in progress. Drop superseded frames; never build a seconds-long video backlog. Bound send time and close only an unresponsive preview connection if necessary. Prioritize capture, label analysis, label events and feedback over preview encoding/delivery; use an independently rate-limited encoding task and reuse encoded bytes across subscribers. Demonstrate with tests that a slow preview consumer cannot indefinitely block the label path.

When no one subscribes, skip network-preview encoding and transmission, while retaining local camera preview. Enabling/disabling preview in one client affects only its own preview connection. Turning preview off does not disconnect its label connection. Preview failure must not stop label reading or feedback; expose it as a separate client-side connection state.

Do not record the continuous preview to disk or treat it as training examples by default. Persist label-event evidence as before. On camera disconnect, stop sending frames and discard queued stale frames; retain the client's last received preview locally. Show frame age / “No recent preview frame” in the main TestClient window, outside the image-only preview window, based on local monotonic receive time, and do not infer the exact cause from silence. Capture timestamps provide context, but cross-machine clock differences must not be mistaken for network latency. Send neither lost-label nor camera-disconnected application events. On camera reconnect, resume with fresh frames only. The preview stream may be live while valid-label events time out; show those states independently.

## 12. Reusable client library

Implement an async, disposable, UI-independent `Scanner.Client` API with strong contracts. It should provide operations equivalent to:

```text
ConnectAsync(hostOrIp, port, cancellationToken)
DisconnectAsync(cancellationToken)
ReadLabelEventsAsync(cancellationToken) or an equivalent event subscription
StartPreviewAsync(cancellationToken)
ReadPreviewFramesAsync(cancellationToken) or an equivalent event subscription
StopPreviewAsync(cancellationToken)
ConfirmAsync(readingId, cancellationToken)
RejectAsync(readingId, cancellationToken)
CorrectAsync(readingId, corrections, cancellationToken)
Separate label and preview connection state / protocol error notifications
```

Handle message framing/reassembly, JSON parsing, binary preview headers, image decoding to raw byte containers, cancellation, bounded buffering, explicit reconnect policy, and feedback acknowledgement correlation. Never depend on WPF BitmapSource in this library. Expose settings for host/port, timeouts, and reconnection; hide the fixed internal `/labels` and `/preview` paths from ordinary consumers while documenting them for other languages.

`ConnectAsync` opens the label connection only. `StartPreviewAsync` opens a second connection to the same host/port at `/preview`, and `StopPreviewAsync` closes only that preview connection. `DisconnectAsync` and disposal close both. Do not open preview implicitly when subscribing to label events. Reconnect the preview only while preview is still explicitly enabled; preserve independent failure states and do not clear pinned readings on reconnect. Return typed preview metadata and compressed image bytes through the client API with bounded latest-frame consumption; do not expose WPF objects or require a second port from consumers.

Feedback retries reuse the same feedback ID. Do not automatically resend non-idempotent business actions. Preserve reading/session/revision distinctions and expose all raw/parsed field values. Include a small example showing a consumer receiving an event and submitting a correction. Provide a `.nupkg`, XML documentation, and a protocol document for non-C# consumers.

## 13. TestClient GUI

Match the attached TestClient mockup and use the actual Scanner.Client library for all communication. Do not duplicate the transport implementation in its code-behind. Provide an optional separate live-preview window while preserving the main window’s selected-event images and review controls.

Connection panel:

- Same computer Boolean. When true, use `127.0.0.1` and show a disabled/read-only host field.
- When false, enable Server IP/host entry. This client destination is editable, unlike the server's read-only Computer IP display.
- Port, default 8765, Connect/Disconnect, actual connection status.
- Label timeout in milliseconds, example/default 5000, Auto-reconnect, and time since the last valid event.
- A label timeout means “no recent valid reading”, not “socket disconnected”; preserve old results and mark them as old. Do not invent a received lost event.
- **Show live preview** Boolean, initially off. Enabling it opens the optional preview connection; disabling it closes that connection and stops video traffic to this client. Show separate preview connected/waiting/stale/error state and frame age in the main TestClient window only. Keep IP and port shared; expose no second address, port, or internal endpoint path.

Main panels:

- No embedded live camera panel. **Show live preview** opens a separate modeless window titled **OEPS Live Preview**, so the main review layout keeps its available space.
- Received label crop and quantity-box crop, with zoom/Fit and selected row identification.
- Selected event values, quantity source, raw and parsed DM, confidence, timestamp, full reading ID, session/revision, model, and quality diagnostics.
- Hold selected event for review. Incoming events continue to the history list while the chosen reading/images stay fixed. The optional live camera preview continues independently. Starting to edit feedback must pin the event even if the operator forgot to check Hold, so a changing live selection cannot redirect a correction.
- Per-field correction controls for Serial, Lot, DM quantity, and Box quantity; original and corrected values side by side. Include optional-field clearing and box row selection where necessary.
- Three actions: **1 · Confirm**, **2 · Reject**, **3 · Send correction**. Show Pending / Acknowledged / Failed / Conflict honestly. A send call alone is not acknowledgement. Disable incompatible actions while a correction draft would be lost or ambiguous.
- An event history with capture/receive time, serial, lot, selected quantity, revision, and status such as New, Unchanged, Under review, Confirmed, or Corrected. Status Unchanged is not a human confirmation.
- View event JSON and Clear list. Clearing the local list must not delete server evidence or feedback.

Bound the history and memory use; keep pinned/in-review events usable while newer events arrive. Images and metadata remain visible after client disconnect or timeout. The mockup's example is deliberately a model misread `128` with actual handwriting `125`; do not hardcode that behaviour into the real pipeline.

Receiving a preview frame does not create a history row, update Last detected/Selected event, reset the valid-label timeout, or change the reading ID targeted by feedback. The client can continue seeing live camera movement while waiting for a valid label or while single detection is idle. It must not label a frozen retained frame “Live”.

### Separate Live Preview window

- Standard native title bar with minimize, maximize/restore, and close. The client area contains only the camera image: no toolbar, buttons, status text, metadata, or detection overlays.
- Resizable, modeless, owned by the main TestClient window; at most one preview window per client. Keep the full frame visible using aspect-ratio-preserving fit and neutral letterboxing; do not stretch or crop it. Opening it must not block label review or feedback.
- **Show live preview** defaults off. Checking it creates or brings forward the window and starts the optional `/preview` connection when the client is connected. Unchecking closes the window and stops that subscription. Closing with the window’s X also unchecks the Boolean and stops only preview; `/labels` remains connected.
- Use the same host and configured port (default 8765) for both connections: `/labels` for events/feedback and `/preview` for camera frames. Do not expose endpoint paths or a second port in the GUI.
- On main-client Disconnect, stop both connections but retain images. An already open preview window may retain its last frame; report disconnected/stale and frame age in the main client only. Reconnect resumes preview only if Show live preview remains checked. A camera outage likewise retains the last frame and reports staleness in the main window. Never claim a retained image is live.
- Before the first frame, use a plain neutral client area. Keep all connection/error/waiting information in the main window so the preview remains image-only. Closing the main application closes this window and releases its resources.
- Keep a bounded latest-frame buffer; UI rendering must not queue an unbounded video backlog. Opening, closing, or resizing preview must not change selected-event crops, feedback targets, label timeout, or history.
- Verify checkbox/X synchronization, repeated opening without duplicate windows/subscriptions, aspect ratio at different window sizes, disconnect/reconnect retention, and uninterrupted label events/feedback when preview is closed. Confirm the preview client area contains only the image or neutral empty background.

Client controls do not own the camera, train models, or edit server acceptance thresholds. Network-triggered camera scanning is not part of this version's public client protocol.

## 14. Local storage and training lifecycle

Training is a core requirement, not a future placeholder. Implement data collection, verified export, a working fine-tuning/evaluation entry point for the selected model, and model import/rollback. Do not claim an improved model exists without actual labelled examples and evaluation.

For generated events, persist:

- Immutable event JSON and decoded raw strings.
- Source capture identifier, original image evidence, rectified label, full box and row crops, ROI/transform information, and image hashes.
- Original predictions, row statuses, scores, quality results, template/profile/model/preprocessing versions, timestamps and all IDs.
- Feedback records with client ID, feedback ID, status, field-level corrections, and verification provenance.

Store both confirmed correct readings and corrected readings. A rejected reading without replacement is useful negative evidence but is not a positive text-training target. Unreviewed predictions and high-confidence automatic results remain unverified. If only lot was corrected, that does not verify the handwriting. Unknown text and confirmed blank text are different annotations.

Retain native or original-resolution crops/source coordinates for training, not just an enlarged GUI screenshot. Deduplicate identical image bytes where useful, but retain each observation and its feedback relationship. Use explicit, documented retention settings rather than silently filling disk or deleting unexported verified samples. Handle storage errors visibly; never acknowledge feedback that was not saved. Provide optional bounded diagnostic collection for blocked scans, since those difficult examples never reach ordinary clients, with a simple way to manually annotate/export them for training.

Export training data to a self-contained folder/ZIP with image files and a JSONL manifest. Include the original prediction, verified target, field name, row, blank/unreadable/cancelled annotations if verified, all provenance/version fields, and hashes. Incremental export must avoid duplicate imports. Export separate datasets for text recognition and row/mark classification as needed. Do not train text recognition on an image from one row with another row's corrected target.

Provide training scripts that load the manifest, validate samples, fine-tune the selected model, resume from a checkpoint, evaluate, and produce a versioned model package. Training occurs separately from the scanner and may use a GPU. Keep train/validation/test splits grouped so repeated photos of the same label/session do not leak across them; use image similarity/hashes and a recorded grouping policy. Include held-out writers where such labels are available.

Report exact whole-quantity accuracy, false accepts, rejection/coverage rate at selected thresholds, and printed/handwritten/blank/marked performance. Character accuracy alone is insufficient for inventory quantities. State sample counts and evidence limits.

Model packages include weights, tokenizer/processor, required preprocessing, supported characters, version, runtime compatibility, checksums, licensing, and evaluation summary. Validate before activation; switch only between scans, clear stale inference/stability buffers, and retain the previous model for rollback. No per-click online learning, arbitrary execution from model archives, or UI freeze during imports. If pretrained model downloads are unavailable, provide the actual integration, provisioning procedure and explicit missing-artifact status; do not fall back to fabricated OCR results.

## 15. Packaging, configuration and documentation

Deliver a single VS Code workspace/solution with build and launch configurations for Server and TestClient individually and together, plus tests and training commands. Use locked versions for Python and .NET dependencies, and include any required native DLLs for Windows x64.

Provide PowerShell scripts for development setup, model provisioning, tests, .NET publishing, and packaging the Python worker. Prefer a reproducible self-contained Windows distribution with a bundled worker/runtime and model assets or an explicit offline model package. The normal operator should not need Visual Studio, Python, a .NET SDK, or internet access to run installed applications. A folder-based release is acceptable; if an installer is produced, document what it installs. Provide the client NuGet package independently.

Include:

- README with build/run instructions and architecture.
- Operator guide for camera connection, single/continuous modes, optional live preview, troubleshooting, the shared IP/port, independent label/preview age indicators, client timeout and correction flow.
- Protocol documentation and sample client usage, including a label-only consumer and a consumer that explicitly starts/stops the independent preview subscription on the same port.
- Label template and exact validator documentation.
- Camera/profile tuning guide with metric limitations.
- Storage/export/train/evaluate/import/rollback documentation.
- Dependency/model licences and version records.
- Known limitations and a clear account of real hardware/Windows validation performed.

Runtime defaults: disconnected camera, single detection enabled for safe first use, server listening on the one configured port if available, TestClient live preview disabled until requested, no hardcoded camera name/address, and no fake preloaded “live” result. The optional preview defaults are 5 FPS, 1280 × 720 maximum bounds and JPEG quality 75; they are independent of label-event timing. Show empty states until real capture/detection or explicitly labelled replay input supplies the relevant image/event. The GUI examples are visual references, not default measured data.

## 16. Meaningful tests and acceptance criteria

Implement automated tests for the core policies and protocol, plus fixture/replay tests for recognition. Use manually verified expected payloads in image fixtures; never copy expectations from the same decoder run being tested and call that independent verification. Synthetic barcode fixtures should use known encoded data. The final label reference should be checked with the real decoder. The generated GUI mockups may contain illustrative, non-decodable codes and must not be used as scanner fixtures.

Minimum meaningful coverage:

1. Serial: all three valid forms, lowercase rejection, OESP typo rejection, off-by-one lengths, non-ASCII digits and trailing newline rejection.
2. Lot: MM 00/12 valid, 13 invalid; YY 20/60 valid, 19/61 invalid; every package including double underscores for _BAG/_BOX; four-character case-preserved suffix.
3. DM quantity: `0`, `0150`, `00.25`; invalid sign/comma/trailing dot/multiple dots/overflow; invariant parsing on a comma-decimal Windows culture.
4. Box quantities: printed and handwritten integers/decimals; zero is not empty; no forced leading zero; malformed strings fail.
5. Every row-selection table case, including a valid middle row with other rows blacked out, and two readable equal quantities still being ambiguous.
6. A valid box quantity emits even when the quantity DM is missing, dirty, malformed, or fails an otherwise enabled DM-specific correction threshold.
7. Marked but unreadable box with valid DM does not emit; empty box with invalid DM does not emit.
8. No combination of codes from different labels; correct field assignment after rotation/perspective correction; all marker IDs and row numbering are correct.
9. Sharpness/quality behaviour on focused, blurred, low-contrast, glare-affected and blank-region fixtures; no improvement in the acceptance score caused solely by post-capture sharpening.
10. Unknown enabled metrics block acceptance; disabled or genuinely N/A checks do not; zero reported corrections is distinct from null telemetry.
11. Continuous mode respects the interval, fresh-frame rule and repeated-event semantics; same values produce new reading IDs without falsely incrementing revision.
12. No clients: last-event display and local evidence still update. Connect a client and confirm event delivery without camera reinitialization.
13. Single mode connect waits; each trigger waits through invalid frames, emits exactly once, disarms, and keeps camera preview active. Triggering the same label again works.
14. Disconnect/mode/profile/model changes cancel stale work; no late event slips through; displayed last event remains unchanged.
15. No public lost/invalid/camera-state events; silent internal session reset and client label timeout behave as specified.
16. Real WebSocket integration with two simultaneous clients, same-computer loopback, fragmentation, reconnect, cancellation, oversized payloads, and a slow client.
17. Feedback 1/2/3 routes to the exact immutable reading; per-field corrections and row selection work; invalid IDs/values fail clearly; retries are idempotent; conflicts cannot overwrite verified data silently.
18. Feedback during camera disconnect or while newer observations arrive; acknowledgement follows durable storage.
19. TestClient holds the selected event during editing, retains its crops after timeout/disconnect, and does not confuse unchanged data with confirmed data.
20. Export/import includes verified samples only where appropriate, preserves raw predictions, prevents duplicates, keeps row/label groups out of both training and test, and supports a real training/evaluation smoke test.
21. Model installation checks compatibility, activates between scans and rolls back without corrupting the prior model. Missing models are reported honestly.
22. Window resize, DPI scaling, device unplug/replug, unsupported camera controls, port conflict and storage failure have usable states.
23. A published Windows build launches both GUIs and uses the bundled worker on a clean environment without a developer Python/SDK installation, to the extent the available execution environment permits testing.
24. One listening port serves both endpoints: a label-only connection receives label JSON/crops and no binary preview frames; a preview-only connection receives preview frames and no label events. Simultaneous subscribers work on loopback and LAN.
25. Show live preview opens/closes only the preview subscription. Label reception, feedback, and other clients' subscriptions continue unchanged. With zero preview subscribers, no network-preview JPEG encoding is performed, and local preview/detection continue.
26. Preview frames continue while single detection waits for a trigger, after it completes, and when no valid label is visible. Frames do not reset the label timeout, generate label-history entries, or bypass acceptance checks.
27. Preview is resized without stretching or lowering recognition resolution. Framed binary headers, fragmentation, lengths, payload decoding, timestamps and stream/sequence handling are validated; malformed or oversized frames fail safely.
28. A slow preview subscriber gets bounded latest-frame delivery with drops instead of a backlog, and does not block label/feedback processing. JPEG encoding is shared when multiple subscribers receive the same frame.
29. Camera disconnect stops new frames and cancels stale queued output; the last client image is retained and ages visibly. The label connection stays available. Fresh preview resumes on camera reconnect without fake live frames or lost/camera-state application events.
30. Hold selected event and draft corrections stay pinned to the original images/reading ID while live preview and new label events continue. Preview failure, disablement or reconnect cannot change that feedback target.

Use automated checks where they establish behaviour; do not substitute large numbers of superficial tests for real image and network validation. Aim for readable, maintainable components and finish the requested end-to-end path before optional extensions.

## 17. Execution order and final handoff

Begin by inspecting the repository and attachments. Then:

1. Extract and save the template, define shared contracts, validators and quantity decision logic, and write the focused policy tests.
2. Prove real marker/code detection and rectification against appropriate label fixtures; add image replay for repeatable development.
3. Integrate the real local quantity model and row/quality assessments; make uncertainty visible.
4. Implement Server camera state, GUI, persistence and the two independent label/preview endpoints on one port.
5. Implement the reusable client library and TestClient, with optional preview subscription, per-reading feedback and durable acknowledgement.
6. Complete export, fine-tuning/evaluation, model import/rollback, configuration and release packaging.
7. Run integration tests and any available physical-camera tests, compare the interfaces with the attached mockups, and fix practical layout/workflow issues.

At completion, report exact commands to build and run, paths to release artifacts and the client package, tests performed, measured recognition results if any, and concrete untested conditions. Do not call a fake image feed, a placeholder model, or a UI-only implementation “complete”. If model quality still needs real training examples, say so while delivering the functioning inference and training pipeline.

## 18. Primary technical references

Use current official documentation and the exact installed source/API when implementing. These references establish starting points, not a guarantee that every language wrapper exposes the same telemetry:

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) — .NET 10 is the intended supported LTS baseline.
- [OpenCV ArUco detection](https://docs.opencv.org/4.x/d5/dae/tutorial_aruco_detection.html) — predefined dictionaries and marker corners.
- [zxing-cpp Python wrapper](https://github.com/zxing-cpp/zxing-cpp/blob/master/wrappers/python/README.md) — image decoding, formats and returned positions; inspect the actual wrapper for additional properties.
- [ZXing Java result metadata](https://zxing.github.io/zxing/apidocs/com/google/zxing/ResultMetadataType.html) — example of correction-count metadata; this does not establish its availability in zxing-cpp or .NET.
- [TrOCR documentation](https://huggingface.co/docs/transformers/model_doc/trocr) — a candidate pretrained recognition architecture; choose and test the actual model/checkpoint.
- [DENSO QR error correction](https://www.qrcode.com/en/about/error_correction.html) — correction capability is distinct from certainty or symbol cleanliness.

Implement the agreed product using this brief and the attachments. Preserve the distinction between a valid automatic reading, a stable repeated reading, and a human-verified reading throughout the system.
