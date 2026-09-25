# Dependency notices

Application source licensing has not been assigned by this implementation. Dependencies keep their upstream licenses; the project's own source is not automatically covered by them.

Pinned build records: `global.json`, project `packages.lock.json`, `recognition/requirements.lock`. The complete bundled Python distributions include their upstream `.dist-info` metadata and license files. Preserve these notices when redistributing the worker/runtime. The full Windows runtime includes Microsoft's license notices.

| Dependency | Purpose / upstream source |
| --- | --- |
| .NET 10 / WPF / ASP.NET Core | Desktop runtime and listener; [dotnet source and licensing](https://github.com/dotnet/runtime) and [WPF](https://github.com/dotnet/wpf). |
| Python 3.12.10 | Bundled worker runtime; [Python license](https://docs.python.org/3/license.html). |
| OpenCV 4.12.0.88 | Camera capture and preview resizing; Apache-2.0 upstream plus distribution third-party notices in [opencv-python](https://github.com/opencv/opencv-python). |
| zxing-cpp 2.3.0 | Data Matrix decoding; Apache-2.0, [upstream](https://github.com/zxing-cpp/zxing-cpp). |
| microsoft/trocr-small-printed and microsoft/trocr-small-handwritten | Separately provisioned pretrained OCR checkpoints; MIT model cards, [printed](https://huggingface.co/microsoft/trocr-small-printed), [handwritten](https://huggingface.co/microsoft/trocr-small-handwritten). Exact repository revisions/checksums are recorded in the model package. |
| WiX 4.0.6 | Build-time MSI tooling; [WiX license](https://github.com/wixtoolset/wix/blob/main/LICENSE.TXT). Not needed on the operator machine. |

No cloud OCR, proprietary decoder purchase or GPU is required for basic local operation. Model redistribution and any added training dataset must retain their own provenance and applicable notices. The supplied label and GUI reference images remain user-provided assets.

The active scanner no longer requires OCR/training libraries. Previously prepared local runtimes may still contain them; preserve their upstream notices if redistributing those files.
