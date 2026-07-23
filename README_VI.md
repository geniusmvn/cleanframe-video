# ERASA VIDEO 2 — Architecture 1.1

Ứng dụng Windows xử lý ảnh/video trong vùng mask do người dùng xác nhận. Lõi inpainting sử dụng trực tiếp source `advimman/lama` và checkpoint Big-LaMa; video ưu tiên dữ liệu temporal rồi chỉ gọi LaMa cho vùng thiếu dữ liệu.

## Kiến trúc

- `Erasa.Video2.Core`: model, mask, state machine, queue và protocol.
- `Erasa.Video2.Worker.Core`: FFmpeg, runtime LaMa, temporal pipeline và xử lý tác vụ.
- `Erasa.Video2.Worker.Host`: process IPC rất mỏng, không chứa logic xử lý.
- `Erasa.Video2.App`: WinUI 3; lỗi worker không được đóng UI.
- `Erasa.Video2.Tests`: chỉ tham chiếu Core và Worker.Core, không tham chiếu worker `.exe`.

## CI có cổng chặn

1. Source/Python checks.
2. Core + Worker.Core tests bằng Any CPU.
3. Publish Worker Host và FFmpeg self-test.
4. Cài source LaMa gốc, Big-LaMa và chạy CPU/video integration.
5. Chỉ sau khi 4 tầng trên xanh mới publish WinUI và tạo artifact.

Runtime LaMa được cài từ trong ứng dụng vào thư mục người dùng; người dùng không phải cài Python, pip, CMD hoặc PowerShell. Artifact không nhồi runtime AI khổng lồ chưa kiểm thử.

## Bàn giao

Chỉ coi bản Windows là dùng được khi workflow `Build ERASA VIDEO 2` xanh ở cả 5 job và artifact `ERASA-VIDEO-2-Windows-x64` xuất hiện.
