# C# client DLL

The client library uses C#/.NET 10, the same application platform as the server. It connects to the scanner server over WebSocket. The server owns the camera and starts it when a client connects. The client computer needs the .NET 10 runtime, but no scanner GUI, camera driver, Python or recognition packages.

## Three operations

| Operation | API | Behavior |
| --- | --- | --- |
| Connect | `await scanner.ConnectAsync(ip, port, thisComputer)` | `true` uses `127.0.0.1` and ignores `ip`; `false` uses the supplied IP or host name. The port is required in either case. |
| Receive | `scanner.JsonReceived += json => ...` | Event raised once for each valid incoming JSON message. Subscribe before connecting. |
| Close | `await scanner.CloseAsync()` | Stops receiving and closes this client's connection. The same object can connect again. |

Receiving is an event because messages can arrive at any time. No polling call is required. The JSON is the original server text: exactly `label_version`, `oeps_pn`, `lot` and `quantity`; quantity remains a string. The library checks the data format before raising the event.

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

The working command-line example is `examples/Scanner.LabelConsumer`. No arguments connect to this computer on port 8765. Supply an IP/host and optionally a port for another computer, or `--local 9000` for this computer on another port.

## Connection behavior

- `Connected` reports the current connection state. `StateChanged` reports connection progress and receive errors. Connection failures are also raised as exceptions from `ConnectAsync`; its optional cancellation token cancels connection establishment. Connecting has a ten-second timeout.
- Event handlers run on the receive loop, without UI-thread dispatch. Keep handlers short and do not throw or block waiting for connect/close. In WPF or WinForms, post UI updates through the application's dispatcher. Slow processing should be queued by the application.
- `LabelReceived` remains available if the application prefers a typed `LabelData` object. `DisconnectAsync` is the existing equivalent of `CloseAsync`.
- The DLL does not automatically reconnect. After a disconnect, the application can call `ConnectAsync` again. The scanner test GUI implements its own retry policy. Messages received while disconnected are not replayed.
- Closing one client leaves other clients connected. The server closes the camera after one minute with no clients unless its debug override is enabled.
- A code remaining visible may be received again every 500 ms. The library forwards each server reading; it does not deduplicate inventory transactions.
