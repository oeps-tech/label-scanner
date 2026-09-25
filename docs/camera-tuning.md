# Camera and decoding

The Python worker owns the camera through OpenCV DirectShow on Windows. It requests the configured resolution, frame rate and controls; actual driver readback is visible in Camera settings. A request is not a guarantee of actual capture rate.

Only Data Matrix is enabled in ZXing-C++. Each latest frame is searched directly, with rotation and downscaling support. There is no full-label alignment, ArUco detection, template, OCR, row extraction or quality-score gate. All decoded symbols are validated independently on the server.

Capture, decoding and optional local preview have separate bounded queues. Slow decoding cannot build a frame backlog or directly block preview. The FPS indicators distinguish measured capture from UI updates. Disabling preview avoids local JPEG encoding/display. Resolution and exposure can affect actual camera FPS and readability; test the actual label at the intended distance. USB format negotiation is still managed by the camera driver.

Keep adequate contrast and a clear border around printed codes. Full native captures feed decoding; only preview is reduced to fit 1280×720. No images are written to disk or sent to clients.
