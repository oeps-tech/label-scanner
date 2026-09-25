using System.Net.WebSockets;

namespace Scanner.Contracts;

public sealed record SocketMessage(WebSocketMessageType Type, byte[] Bytes);
public static class WebSocketMessages
{
    /// <summary>Reassemble fragments with an enforced byte bound before growing the buffer.</summary>
    public static async Task<SocketMessage?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(16384, maxBytes + 1)];
        WebSocketMessageType? type = null;
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (type.HasValue && type != part.MessageType) throw new InvalidDataException("Fragment type changed.");
            type = part.MessageType;
            if (output.Length + part.Count > maxBytes) throw new InvalidDataException("WebSocket message exceeds configured limit.");
            output.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return new(type.Value, output.ToArray());
        }
    }
}
