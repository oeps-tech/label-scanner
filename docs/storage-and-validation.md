# Validation and storage

No scan database, audit trail, label images, model packages or exports are created by the application. Server and client persist only their connection/camera preferences. Latest data, diagnostic results and counters are in memory. Existing legacy evidence files are not opened or deleted.

Validation order: supported literal version `1`, exact five-field structure separated by a single `*`, PN grammar, lot grammar and MMYY ranges, quantity grammar, eight-uppercase-hex checksum shape, CRC-32/IEEE equality. Unsupported versions never fall through to version 1 field validation. Input is never trimmed or rewritten. Quantity is preserved as a string; zero is valid. CRC is checked on exact ASCII input through the quantity field, excluding the final `*` and checksum. Double-asterisk separators are rejected.

The Lot begins with four digits `MMYY`: month `MM` must be `00`–`12`, and year `YY` must be `00` or `21`–`99`. Month zero and year zero are independently allowed. Years `01`–`20` are rejected; there is no additional check against the current date. The same constraints apply when the client DLL validates received JSON.

Duplicate suppression uses monotonic time and is independent for each exact encoded payload. Only a broadcast queued to at least one client starts its 500 ms interval. Camera idle timing is also monotonic; it resets when any client connects or the debug override is enabled.
