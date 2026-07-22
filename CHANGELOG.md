# Changelog

## 0.2.0 — clean rebuild

- Rebuilt the app shell as ERASA VIDEO with the supplied brand assets.
- Uses the original `advimman/lama` source and original checkpoint layout directly.
- Added Video/Image tabs, file/folder import, drag/drop and persistent queue.
- Added brush, eraser, rectangle, ellipse, pan, softness, undo/redo/reset and zoom.
- Added separate worker process, logs, pause/cancel/retry/resume and segment checkpoints.
- Added CPU base runtime and optional in-app NVIDIA runtime installer.
- Added output contract checks for video dimensions, FPS, duration and audio.
- Added original-LaMa CPU and a 3-second video/audio integration test to GitHub Actions.
