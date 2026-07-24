# Changelog

## 1.2.2 — wheel-only future dependency

- Thay `future==0.18.3` bằng `future==1.0.0` có wheel `py3-none-any`.
- Không còn chạy `setup.py` của future trong Python nhúng.
- Ép toàn bộ dependency Lightning dùng binary wheel.

## 1.2.1 — original checkpoint compatibility

- Bổ sung đúng `pytorch-lightning==1.2.9` và các dependency tương thích mà checkpoint Big-LaMa gốc cần khi giải tuần tự.
- Tách dependency source-only khỏi lệnh `--only-binary` để `future` cài được.
- Kiểm tra import runtime trước khi tải model lớn.
- LaMa bridge ghi traceback đầy đủ khi self-test lỗi.
- Giảm log tải xuống còn theo mốc khoảng 5%.

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
