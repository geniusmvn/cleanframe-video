# Changelog

## 1.2.0 — tested runtime bundle

- Không cài Python/model bằng worker trong CI hoặc lúc người dùng bấm xử lý.
- GitHub Actions chuẩn bị Python nhúng, PyTorch CUDA có CPU fallback, source advimman/lama và Big-LaMa bằng script riêng có retry và kiểm tra ZIP.
- Runtime chỉ được đánh dấu sẵn sàng sau khi bridge LaMa chạy self-test thật.
- Job LaMa tải lên runtime đã kiểm thử; job WinUI bắt buộc tải và đóng gói đúng runtime đó.
- Artifact cuối kiểm tra lại trạng thái runtime qua worker trước khi upload.
- Thêm log rõ ràng và kiểm tra cấu trúc artifact sau khi truyền giữa các job.

## 1.1.0 — architecture reset

- Tách Worker.Core khỏi Worker.Host executable.
- Test project không còn tham chiếu worker `.exe`.
- Bỏ build/test trộn `x64` và `Any CPU`.
- Chia GitHub Actions thành 5 job có dependency rõ ràng.
- Artifact Windows chỉ được tạo sau khi original LaMa CPU/video integration đạt.
- Sửa cấu hình UTF-8 console bằng `Console.SetOut/SetError`.
