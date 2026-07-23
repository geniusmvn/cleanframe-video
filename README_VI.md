# ERASA VIDEO 2 — Architecture 1.2

Ứng dụng Windows xử lý ảnh/video trong vùng mask do người dùng xác nhận. Lõi inpainting import trực tiếp source `advimman/lama` tại commit đã ghim và dùng checkpoint Big-LaMa. Với video, pipeline ưu tiên dữ liệu temporal rồi chỉ gọi LaMa cho pixel còn thiếu.

## Kiến trúc

- `Erasa.Video2.Core`: model, mask, state machine, queue và protocol.
- `Erasa.Video2.Worker.Core`: FFmpeg, kiểm tra runtime, temporal pipeline và xử lý tác vụ.
- `Erasa.Video2.Worker.Host`: process IPC mỏng; worker lỗi không được đóng UI.
- `Erasa.Video2.App`: WinUI 3.
- `Erasa.Video2.Tests`: chỉ tham chiếu Core và Worker.Core, không tham chiếu worker `.exe`.

## Runtime LaMa

Artifact Windows đầy đủ phải chứa sẵn:

- Python 3.8 nhúng;
- PyTorch CUDA có CPU fallback;
- mã nguồn gốc `advimman/lama` tại commit `786f5936b27fb3dacd2b1ad799e4de968ea697e7`;
- checkpoint Big-LaMa;
- FFmpeg và FFprobe.

Người dùng không phải cài Python, pip, CMD hoặc PowerShell. Ứng dụng không tải model trong lúc bấm xử lý. Nếu artifact thiếu runtime đã kiểm thử, ứng dụng báo lỗi thay vì âm thầm cài một môi trường chưa được xác minh.

## CI có cổng chặn

1. Source/Python checks.
2. Core + Worker.Core tests bằng Any CPU.
3. Publish Worker Host và chạy FFmpeg self-test.
4. Chuẩn bị runtime LaMa độc lập, chạy self-test bằng embedded Python, chạy lại qua Worker.Core, rồi chạy video integration 3 giây có audio.
5. Chỉ sau khi bốn tầng trên xanh mới publish WinUI, tải đúng runtime đã kiểm thử từ job 4, đóng gói và kiểm tra lại trạng thái runtime trong artifact cuối.

## Bàn giao

Chỉ coi bản Windows là dùng được khi workflow `Build ERASA VIDEO 2` xanh ở cả 5 job và artifact `ERASA-VIDEO-2-Windows-x64` xuất hiện. Source ZIP không phải bằng chứng ứng dụng đã build hoặc chạy thành công.
