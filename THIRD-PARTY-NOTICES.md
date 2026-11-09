# Third-party notices

Markshot is Copyright © 2026 guoqing.wu (GitHub: well74741). It includes the following third-party components,
each under its own license:

| Component | License | Use |
| --- | --- | --- |
| [.NET 8 runtime, WPF, Windows Forms](https://github.com/dotnet) | MIT | Runtime bundled in the self-contained build |
| [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) 4.2 | Apache-2.0 | OCR pipeline |
| [RapidOCR / PaddleOCR PP-OCRv5 models](https://github.com/RapidAI/RapidOCR) | Apache-2.0 | Detection, classification and recognition models, dictionaries (`models/v5`) |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) 1.29 | MIT | Local inference |
| [SkiaSharp](https://github.com/mono/SkiaSharp) 3.119 | MIT | Image decoding and scaling |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) 2.0 | BSL-1.0 | Polygon clipping (RapidOcrNet dependency) |
| [NAudio](https://github.com/naudio/NAudio) 2.2 | MIT | Screen recording: system sound (WASAPI loopback) and microphone |
| [zxing-cpp](https://github.com/zxing-cpp/zxing-cpp) 3.1.1 (.NET wrapper ZXingCpp 0.5.3) | Apache-2.0 | QR code / barcode reading; native library shipped as `zxingcpp-native.dll` |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) 0.16.11 | Apache-2.0 | QR code / barcode reading (second pass) |
| [FFmpeg](https://ffmpeg.org) 6.1.1 (gyan.dev essentials build) | GPL v3 | Screen recording: encoding and export. Shipped unmodified as a separate program, `ffmpeg\ffmpeg.exe`; its license and build / source information are in `ffmpeg\ffmpeg.exe.LICENSE` and `ffmpeg\ffmpeg.exe.README` (source: https://github.com/FFmpeg/FFmpeg/commit/e38092ef93) |

The full license texts are available at the linked project pages.
