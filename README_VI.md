# ERASA VIDEO 2 — Source 1.0.0

Ứng dụng Windows xử lý ảnh và video trong **mask do người dùng kiểm tra và xác nhận**.

## Kiến trúc

- **.NET 8 + WinUI 3** cho giao diện.
- **Worker `.exe` riêng** cho FFmpeg, runtime và pipeline. Worker lỗi không chủ động đóng giao diện.
- **FFmpeg đóng gói** trong artifact Windows.
- **Mã nguồn gốc `advimman/lama`** được tải từ commit cố định `786f5936b27fb3dacd2b1ad799e4de968ea697e7` khi người dùng bấm cài bộ xử lý.
- Không dùng ONNX, TorchScript chuyển đổi, `simple-lama`, `lama-cleaner` hoặc FFmpeg `delogo`.
- Video dùng optical flow hai chiều và confidence temporal trước; Big-LaMa chỉ điền pixel không đủ dữ liệu từ frame lân cận.

## Luồng sử dụng

1. **Chọn tệp** hoặc **Thêm thư mục**.
2. Vẽ bằng **Cọ / Tẩy / Khung / Elip**, hoặc dùng **Tự động đề xuất** cho video.
3. Bấm **Xác nhận mask**.
4. Bấm **Preview 3 giây**.
5. Khi preview đạt, bấm **XỬ LÝ**.

Ảnh xem trước không bị xóa khi worker lỗi. Preview và Xử lý chỉ được mở sau khi mask đã xác nhận.

## Runtime LaMa

Artifact Windows đầy đủ **đóng gói sẵn** Python nhúng, PyTorch CUDA (có CPU fallback), source gốc LaMa và checkpoint Big-LaMa. Người dùng không cần Python, pip, CMD hay PowerShell.

- Máy có NVIDIA: chế độ Tự động ưu tiên CUDA.
- Không có CUDA: cùng runtime tự chạy CPU.
- Nếu runtime đi kèm bị xóa, ứng dụng vẫn có chức năng cài lại tự động vào `%LocalAppData%\ERASA_VIDEO_2\runtime`.
- Artifact lớn hơn vì ưu tiên mở lên là dùng được.

## Build Windows

GitHub Actions `.github/workflows/build-windows.yml` thực hiện:

- build toàn bộ solution;
- test .NET và Python;
- publish worker riêng;
- FFmpeg utility self-test;
- publish WinUI 3;
- startup smoke test thật của `ERASA.Video.exe`;
- cài runtime CPU;
- self-test Big-LaMa từ source gốc;
- integration test video 3 giây có audio, FPS và độ phân giải.

Chỉ coi bản Windows là đạt khi workflow xanh và artifact `ERASA-VIDEO-2-Windows-x64` được tạo.

## Phạm vi

Đây là công cụ inpainting generic. Nó không xử lý watermark vô hình, SynthID hoặc Content Credentials, và không có bộ nhận diện chuyên phá dấu nguồn của một hãng cụ thể.
