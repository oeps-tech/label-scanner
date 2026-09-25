using Scanner.Client;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await using var scanner = new ScannerConnection();
scanner.JsonReceived += json => Console.WriteLine(json);
scanner.StateChanged += state => Console.Error.WriteLine(state);
try
{
    var ip = args.ElementAtOrDefault(0);
    await scanner.ConnectAsync(ip, int.TryParse(args.ElementAtOrDefault(1), out var port) ? port : 8765,
        thisComputer: ip is null or "--local", cancellationToken: stop.Token);
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally { await scanner.CloseAsync(); }
