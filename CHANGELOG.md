# Changelog

## 0.3.0 — functional editor rebuild

- Decoupled media probe, thumbnail and timeline frame extraction from Python/LaMa.
- Added packaged FFmpeg media service and its real integration test.
- Added separate utility-runtime and original-LaMa runtime diagnostics.
- Added explicit mask confirmation gate before preview/process.
- Rebuilt editor fit/zoom/pan behavior and reduced mask overlay opacity.
- Reworked queue cards, full error panel, retry-load and open-log actions.
- Fixed stale queue errors and tab switching behavior.
- Added artifact runtime diagnostics and desktop startup smoke test.
- Added tests for media metadata, thumbnail generation and mask-confirmation policy.

## 0.2.0 — clean rebuild

- Rebuilt the app shell as ERASA VIDEO with supplied brand assets.
- Uses original `advimman/lama` source and original checkpoint layout directly.
- Added Video/Image tabs, file/folder import, drag/drop and persistent queue.
- Added brush, eraser, rectangle, ellipse, pan, softness, undo/redo/reset and zoom.
- Added separate worker process, logs, pause/cancel/retry/resume and segment checkpoints.
- Added output contract checks for video dimensions, FPS, duration and audio.
