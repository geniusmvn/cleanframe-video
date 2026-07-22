# Changelog

## 0.2.0-source-milestone

- New .NET 8 / WinUI 3 project, separate from legacy PySide6 architecture.
- Isolated video worker process using JSON Lines IPC.
- Generic static-overlay detector over multiple sampled frames.
- Rectangle, ellipse, brush, eraser, soft mask, zoom/pan, undo/redo/reset.
- Bidirectional Farneback temporal reconstruction with confidence map.
- Telea + Navier–Stokes spatial fallback.
- Fast / Beautiful modes, queue pause/cancel/retry/resume, persisted queue state and immutable per-job mask snapshots.
- Windows GitHub Actions build, test and portable artifact assembly.
- No LaMa, ONNX Runtime DirectML or FFmpeg delogo in this milestone.
