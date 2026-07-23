# Changelog

## 1.1.0 — architecture reset

- Tách Worker.Core khỏi Worker.Host executable.
- Test project không còn tham chiếu worker `.exe`.
- Bỏ build/test trộn `x64` và `Any CPU`.
- Chia GitHub Actions thành 5 job có dependency rõ ràng.
- Artifact Windows chỉ được tạo sau khi original LaMa CPU/video integration đạt.
- Sửa cấu hình UTF-8 console bằng `Console.SetOut/SetError`.
