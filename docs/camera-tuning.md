# Camera and decoding

The Python worker owns the camera through OpenCV DirectShow on Windows. It requests the configured resolution, frame rate and controls; actual driver readback is visible in Camera settings. A request is not a guarantee of actual capture rate.

All supported ZXing-C++ barcode formats are enabled, including Data Matrix, QR, Aztec and linear codes. Each latest frame is searched directly, with rotation and downscaling support. There is no full-label alignment, ArUco detection, template, OCR, row extraction or quality-score gate. Each decoded symbol is routed independently to OEPS 1/2, supplier 3 or raw X; every failed validation falls back to X. Searching more symbologies can change decode time; the existing timing/FPS display shows the measured cost.

Capture, decoding and optional local preview have separate bounded queues. Slow decoding cannot build a frame backlog or directly block preview. The FPS indicators distinguish measured capture from UI updates. Disabling preview avoids local JPEG encoding/display. Resolution and exposure can affect actual camera FPS and readability; test the actual label at the intended distance. USB format negotiation is still managed by the camera driver.

Keep adequate contrast and a clear border around printed codes. Full native captures feed decoding; only preview is reduced to fit 1280×720. No images are written to disk or sent to clients.
