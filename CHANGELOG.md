# Changelog

## 1.0.0

- Bỏ toàn bộ kiến trúc Avalonia/PySide6/WinUI prototype trước.
- Tạo project mới .NET 8 + WinUI 3.
- Worker video chạy process riêng.
- FFmpeg hoạt động độc lập với runtime LaMa để preview nguồn luôn dùng được.
- Mask editor mới: brush, eraser, rectangle, ellipse, pan, zoom, undo, redo, reset, soft mask.
- State machine bắt buộc xác nhận mask trước Preview/Xử lý.
- Artifact Windows đóng gói sẵn Python nhúng, PyTorch CUDA có CPU fallback, source gốc `advimman/lama` và Big-LaMa.
- Temporal reconstruction là chính cho video; LaMa chỉ fallback ở pixel thiếu confidence.
- Queue có pause, cancel, retry và resume theo segment.
- GitHub Actions có build solution, UI startup smoke, LaMa CPU self-test và video integration test.
