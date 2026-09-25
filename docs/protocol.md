# Data-only WebSocket protocol

Endpoint: `ws://<host>:8765/labels` (port configurable). No hello/subscription command is needed. Each successful scan produces a JSON text message with exactly `label_version` (integer 1), `oeps_pn` (string), `lot` (string), `quantity` (string). The checksum is verified before broadcast and is not transmitted.

Example: `{"label_version":1,"oeps_pn":"OEPSA010123","lot":"0926-TRAY-Q5R2","quantity":"0"}`.

The endpoint is receive-only for clients. No application acknowledgements, feedback, image channel, persisted backlog or scan history. WebSocket ping/pong is transport-level liveness only. Send queues are bounded; slow clients are disconnected rather than silently dropping their data. The client library validates all four fields. The test client automatically reconnects after connection loss while Connect is active.

The server emits multiple distinct valid Data Matrix payloads independently. Identical payloads are suppressed for 500 ms from the previous broadcast to at least one connected client. A continuously visible code can be sent again once that interval expires. A client joining during a cooldown may wait until the next eligible observation. This is a stream of readings, not exactly-once inventory operations.

See the repository README for the exact payload grammar and CRC-32 printer test vectors. All older label/image/feedback protocols are retired.
