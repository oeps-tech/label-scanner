# Data Matrix worker

Run `.tools/python/python.exe -m recognition.worker`. Private IPC is bounded length-prefixed UTF-8 JSON through stdin/stdout (`ipc.py`). The C# server owns field/CRC validation, cooldown and client-driven camera demand. The worker owns physical camera capture, Data Matrix-only decoding and optional in-memory JPEG preview.

`connect` / `disconnect` / `apply_camera_settings` use monotonically increasing generations. Older commands and results are rejected. `local_preview` controls encoding separately from scanning. `enumerate_cameras` is operator-triggered. `replay` is a developer-test command that reads a fixture; it does not save images. There are no downloads, model loading or filesystem writes in the worker.

Dependencies: OpenCV, NumPy and ZXing-C++; see requirements.lock. Tests use real encoded Data Matrix and QR fixtures, real decoding and a real worker subprocess. No physical camera performance has been claimed by these tests.
