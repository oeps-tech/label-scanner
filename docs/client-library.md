# C# client DLL

The client library uses C#/.NET 10, the same application platform as the server. It connects to the scanner server over WebSocket. The server owns the camera and Bluetooth reader; a client connection starts the source selected by its port. The client computer needs the .NET 10 runtime, but no scanner GUI, camera driver, Python or recognition packages.

## Three operations

| Operation | API | Behavior |
| --- | --- | --- |
| Connect | `await scanner.ConnectAsync(ip, port, thisComputer)` | `true` uses `127.0.0.1` and ignores `ip`; `false` uses the supplied IP or host name. The port is required in either case. |
| Receive | `scanner.JsonReceived += json => ...` | Event raised once for each valid incoming JSON message. Subscribe before connecting. |
| Close | `await scanner.CloseAsync()` | Stops receiving and closes this client's connection. The same object can connect again. |

Receiving is an event because messages can arrive at any time. No polling call is required. The JSON is the original server text. Version 1 has exactly `label_version`, `oeps_pn`, `lot` and `quantity`; quantity remains a string. Version 2 has exactly `label_version`, `sales_order_id`, `document_name`, `out_id` and `out_number`; the IDs are JSON integers and printed numbers are strings. Version 3 has `label_version`, `oeps_pn`, `mpn`, `quantity`, with explicit nulls for missing identifiers. Version X has only `label_version` (string `"X"`) and `raw` (string). The library checks the envelope and structured fields before raising events; it does not validate X content. Parse JSON before using raw text to restore escaped controls and Unicode.

Replace both DLLs for C support. Older SDKs disconnect on unsupported versions. New clients support 1/2/3/C/X on the same connection; existing version 1/2 JSON and the connection API are unchanged.

Version C contains exactly `{"label_version":"C","command":"For Odoo OUT"}`. The scanner verifies CRC-32 on the barcode `C*<command>*<checksum>` and omits the checksum from JSON. Command names are nonblank printable ASCII without `*` or controls; maximum 4,085 characters. Text is preserved exactly, including spaces and case. The scanner and SDK have no fixed command list: the application decides which names to act on or ignore. Invalid command labels arrive as X and must not be treated as validated commands.

## Build and import locally

Run from this repository:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-client.ps1
```

This builds a local bundle under `.local/client-sdk`; it does not create or publish a release. Copy both `Scanner.Client.dll` and `Scanner.Contracts.dll` into your application's `lib` directory and reference them from a .NET 10 project:

```xml
<ItemGroup>
  <Reference Include="Scanner.Client">
    <HintPath>lib/Scanner.Client.dll</HintPath>
  </Reference>
  <Reference Include="Scanner.Contracts">
    <HintPath>lib/Scanner.Contracts.dll</HintPath>
  </Reference>
</ItemGroup>
```

The XML documentation files in the bundle are optional and enable editor help. Applications built inside this repository can use a project reference instead.

## Example

```csharp
using Scanner.Client;
using Scanner.Contracts;

await using var scanner = new ScannerConnection();

// Register before connecting so the first message is received.
scanner.JsonReceived += json => Console.WriteLine(json);
scanner.StateChanged += state => Console.Error.WriteLine(state);

// Server on this computer; IP is ignored.
await scanner.ConnectAsync(ip: null, port: 8765, thisComputer: true);

// For a server on another computer, use instead:
// await scanner.ConnectAsync("192.168.1.50", 8765, thisComputer: false);

Console.WriteLine("Receiving labels. Press Enter to close.");
Console.ReadLine();
await scanner.CloseAsync();
```

Use port **8765** for camera readings or **8766** for barcode-reader readings (or the ports configured on the server). The same `ConnectAsync` method and `/labels` endpoint work for both; no DLL API change is required. Reader clients trigger the Bluetooth connection and receive only reader scans.

The working command-line example is `examples/Scanner.LabelConsumer`. No arguments connect to this computer on port 8765. Supply an IP/host and optionally a port for another computer, or `--local 9000` for this computer on another port.

## Connection behavior

For typed data, branch on the label version:

```csharp
scanner.LabelReceived += data =>
{
    if (data.IsCommand)
    {
        // Dispatch only commands your application supports. This example just displays them.
        Console.WriteLine(data.Command);
        return;
    }
    if (data.IsRaw)
    {
        // Either use the exact decoded text, or return here to ignore raw X readings.
        Console.WriteLine(data.Raw);
        return;
    }
    switch (data.LabelVersion)
    {
        case 1:
            Console.WriteLine($"{data.OepsPn}: {data.Quantity} (lot {data.Lot})");
            break;
        case 2:
            Console.WriteLine($"{data.DocumentName} (sale.order ID {data.SalesOrderId})");
            Console.WriteLine($"{data.OutNumber} (stock.picking ID {data.OutId})");
            break;
        case 3:
            Console.WriteLine($"OEPS: {data.OepsPn ?? "missing"}, MPN: {data.Mpn ?? "missing"}, quantity: {data.Quantity}");
            break;
    }
};
```

`LabelData` keeps its integer `LabelVersion` API and the existing version 1/2 constructors. For raw data, `IsRaw` is true, `VersionText` is `"X"`, and the C# `LabelVersion` is `LabelData.RawVersion` (0). The JSON converter writes `"X"`, never numeric 0, on the wire. Versions 1/2/3 remain numeric. Factories `LabelData.FromSupplier(oepsPn, mpn, quantity)` and `LabelData.FromRaw(raw)` are available. Fields belonging to other versions are omitted from JSON; missing version 3 identifiers are explicit nulls. `DocumentName` contains Odoo's printed `document_name` (e.g. `S/2680`), never substituted with `name` (`S23661`). The consuming application can build record links from the IDs and its configured Odoo address.

For commands, `IsCommand` is true, `VersionText` is `"C"`, `Command` contains the exact text, and the integer `LabelVersion` is `LabelData.CommandVersion` (-1). The JSON converter writes `"C"`, never -1. `LabelData.FromCommand(command)` creates command data. Neither the scanner nor the DLL executes command text. Reader commands are delivered for every scan without suppression; camera commands may repeat every 500 ms like other camera labels.

Raw X includes any decoded text that fails versions 1/2/3/C, including bad OEPS CRCs and invalid supplier labels; clients requiring validated data should ignore `IsRaw` messages. Version 3 requires valid quantity AND (valid OEPS PN OR nonempty MPN). Either identifier may be null, but not both. Missing/invalid quantity or missing both identifiers causes the original supplier text to arrive as X. The client SDK enforces the same requirement on structured version 3 JSON.

- `Connected` reports the current connection state. `StateChanged` reports connection progress and receive errors. Connection failures are also raised as exceptions from `ConnectAsync`; its optional cancellation token cancels connection establishment. Connecting has a ten-second timeout.
- Event handlers run on the receive loop, without UI-thread dispatch. Keep handlers short and do not throw or block waiting for connect/close. In WPF or WinForms, post UI updates through the application's dispatcher. Slow processing should be queued by the application.
- `LabelReceived` remains available if the application prefers a typed `LabelData` object. `DisconnectAsync` is the existing equivalent of `CloseAsync`.
- The DLL does not automatically reconnect. After a disconnect, the application can call `ConnectAsync` again. The scanner test GUI implements its own retry policy. Messages received while disconnected are not replayed.
- Closing one client leaves other clients connected. The server closes each source after one minute without clients on its own port unless that source's debug override is enabled.
- For every current and future label version, a code remaining visible to the camera may be received again every 500 ms. The barcode-reader port forwards every scan, including immediate identical repeats, without duplicate suppression. Different payloads are independent. The library forwards each server reading; it does not deduplicate inventory transactions.
