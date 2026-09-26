# Dependency notices

Application source licensing has not been assigned by this implementation. Dependencies keep their upstream licenses; the project's own source is not automatically covered by them.

Pinned build records: `global.json`, project `packages.lock.json`, `recognition/requirements.lock`. The complete bundled Python distributions include their upstream `.dist-info` metadata and license files. Preserve these notices when redistributing the worker/runtime. The full Windows runtime includes Microsoft's license notices.

| Dependency | Purpose / upstream source |
| --- | --- |
| .NET 10 / WPF / ASP.NET Core | Desktop runtime and listener; [dotnet source and licensing](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf). |
| Python 3.12.10 | Bundled worker runtime; [Python license](https://docs.python.org/3/license.html). |
| OpenCV 4.12.0.88 | Camera capture and preview resizing; Apache-2.0 upstream plus distribution third-party notices in [opencv-python](https://github.com/opencv/opencv-python). |
| zxing-cpp 2.3.0 | Data Matrix decoding; Apache-2.0, [upstream](https://github.com/zxing-cpp/zxing-cpp). |
| NumPy 2.2.6 | Camera image arrays; BSD-3-Clause plus bundled dependency notices, [upstream](https://github.com/numpy/numpy). |
| WiX 4.0.6 | Build-time MSI tooling; [WiX license](https://github.com/wixtoolset/wix/blob/main/LICENSE.TXT). Not needed on the operator machine. |

No cloud OCR, proprietary decoder purchase or GPU is required for basic local operation. The supplied label and GUI reference images remain user-provided assets.

Release packaging selects only the pinned decoder distributions; legacy OCR/training packages in a developer's environment are not bundled.
