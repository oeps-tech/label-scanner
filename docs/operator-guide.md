# Operator guide

1. Run `Run.cmd`. The server listens without opening the camera when no clients are present.
2. Select the camera and adjust its capture settings. Connect the test client to the server host/IP and port (8765 by default).
3. The camera opens automatically. Present a Data Matrix label using the version 1 format in the README. A valid zero quantity is sent as `"0"`.
4. Inspect the latest data on the client. The checksum and debug fields stay on the server.
5. On the server, enable **Show details (debug)** to see the preview, latest JSON and live diagnostics for rejected version/PN/lot/quantity/CRC or duplicate suppression. No reading or image is saved.

Under **Latest valid data**, **Label versions & JSON** opens a reference window with a version selector, validation rules, checksum calculation and matching Data Matrix/JSON examples. Version 1 is currently supported. The reference is read-only and can remain open while the scanner runs.

No clients for one minute closes the camera. A new client reopens it. Enable **Keep camera connected (debug)** to operate without clients.

**Show details (debug)** is off by default: the server uses a fixed 900 × 350 window containing camera and network controls. Enable it for the resizable detailed view. The choice is saved for the next launch. Hiding details pauses preview encoding and rendering while scanning and delivery continue. In the detailed view, **Enable camera preview** separately controls preview work; its preference is preserved when hiding details.

The same payload may be emitted again every 500 ms while visible. Client disconnects do not create a replay backlog. Restart both applications after updating the local code.
