FFmpeg binaries are intentionally not committed to source.
GitHub Actions downloads `ffmpeg.exe` and `ffprobe.exe`, runs tests with them, then places them inside the Windows artifact under `tools/ffmpeg/bin`.
