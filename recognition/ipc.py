"""Private IPC: unsigned LE32 length + UTF-8 JSON. Stdout is never a log."""
import json
import struct
import threading

MAX_MESSAGE = 32 * 1024 * 1024


def read_exact(stream, count):
    data = bytearray()
    while len(data) < count:
        chunk = stream.read(count - len(data))
        if not chunk:
            if not data:
                return None
            raise EOFError("Truncated IPC message")
        data.extend(chunk)
    return bytes(data)


def read_message(stream):
    header = read_exact(stream, 4)
    if header is None:
        return None
    size, = struct.unpack("<I", header)
    if not 0 < size <= MAX_MESSAGE:
        raise ValueError("IPC length outside allowed range")
    body = read_exact(stream, size)
    if body is None:
        raise EOFError("Truncated IPC body")
    result = json.loads(body.decode("utf-8"))
    if not isinstance(result, dict):
        raise ValueError("IPC message must be an object")
    return result


class Writer:
    def __init__(self, stream):
        self.stream = stream
        self.lock = threading.Lock()

    def send(self, message):
        data = json.dumps(message, allow_nan=False, separators=(",", ":")).encode("utf-8")
        if len(data) > MAX_MESSAGE:
            raise ValueError("IPC result exceeds 32 MiB")
        with self.lock:
            self.stream.write(struct.pack("<I", len(data)))
            self.stream.write(data)
            self.stream.flush()
