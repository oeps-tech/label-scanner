"""Exercise the published worker's real private IPC using its own interpreter."""
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import sys
import threading
import time


def main() -> None:
    worker = Path(sys.argv[1]).resolve()
    data = Path(sys.argv[2]).resolve()
    data.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, OEPS_SCANNER_DATA=str(data))
    environment.pop("PYTHONPATH", None)
    example = '1*OEPSA010123*0926-TRAY-Q5R2*1*D91C90D6'
    fixture = data / 'datamatrix-fixture.png'
    subprocess.run([str(worker / 'python/python.exe'), '-c',
        'import sys,cv2,zxingcpp,numpy as np; cv2.imwrite(sys.argv[1],np.asarray(zxingcpp.write_barcode(zxingcpp.BarcodeFormat.DataMatrix,sys.argv[2],width=300,height=300,quiet_zone=12)))',
        str(fixture), example], cwd=worker, env=environment, check=True)
    process = subprocess.Popen([str(worker / "python" / "python.exe"), "-u", "-m", "recognition.worker"], cwd=worker,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment)
    messages: queue.Queue = queue.Queue()
    errors: list[str] = []

    def read() -> None:
        try:
            while True:
                header = process.stdout.read(4)
                if len(header) != 4:
                    return
                size, = struct.unpack("<I", header)
                if not 0 < size <= 64 * 1024 * 1024:
                    raise ValueError("Worker emitted an invalid frame length")
                payload = process.stdout.read(size)
                if len(payload) != size:
                    raise ValueError("Worker ended inside an IPC frame")
                messages.put(json.loads(payload))
        except Exception as exc:
            messages.put(exc)

    def diagnostics() -> None:
        for line in iter(process.stderr.readline, b""):
            errors.append(line.decode("utf-8", errors="replace").rstrip())

    threading.Thread(target=read, daemon=True).start()
    threading.Thread(target=diagnostics, daemon=True).start()

    def send(value: dict) -> None:
        payload = json.dumps(value).encode("utf-8")
        process.stdin.write(struct.pack("<I", len(payload)) + payload)
        process.stdin.flush()

    try:
        first = messages.get(timeout=60)
        if isinstance(first, Exception):
            raise first
        if first.get("type") != "status" or first.get("state") != "ready":
            raise ValueError(f"Expected worker readiness, got {first}")
        send({'type': 'local_preview', 'enabled': False})
        send({'type': 'replay', 'generation': 1, 'path': str(fixture)})
        deadline = time.monotonic() + 15
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError('Packaged worker did not decode the fixture')
            result = messages.get(timeout=remaining)
            if isinstance(result, Exception):
                raise result
            if result.get('type') == 'candidate' and result.get('codes'):
                if result['codes'][0]['raw'] != example:
                    raise ValueError('Packaged decoder returned the wrong data')
                break
        send({"type": "shutdown"})
        process.wait(timeout=15)
        if process.returncode:
            raise RuntimeError(f"Worker exited {process.returncode}: {errors}")
        print(json.dumps({"result": "PASS", "python": str(worker / "python" / "python.exe"), "ready": first,
                          "decoded": example, "shutdown_exit_code": process.returncode, "offline": True, "camera_opened": False}, indent=2))
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


if __name__ == "__main__":
    main()
