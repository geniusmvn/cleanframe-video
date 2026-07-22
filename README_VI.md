# ERASA VIDEO 0.2 — Original LaMa

Ứng dụng Windows xử lý **ảnh và video theo mask người dùng xác nhận**, dùng trực tiếp mã nguồn gốc của [`advimman/lama`](https://github.com/advimman/lama).

## Nền tảng xử lý

- Workflow tải đúng repository `advimman/lama` tại commit cố định `786f5936b27fb3dacd2b1ad799e4de968ea697e7`.
- Worker thêm source upstream vào `sys.path` và import trực tiếp:
  `from saicinpainting.training.trainers import load_checkpoint`.
- Model là cấu trúc gốc `config.yaml` + `models/best.ckpt` trong gói `big-lama.zip`.
- Không dùng ONNX, TorchScript chuyển đổi, `simple-lama`, `lama-cleaner` hoặc FFmpeg `delogo`.
- Bản artifact mặc định đóng gói PyTorch CPU để dễ build và luôn có fallback.
- Trong **Cài đặt**, nút **Cài gói NVIDIA** tự cài PyTorch CUDA cho GTX 1660 Super; người dùng không mở CMD, PowerShell, pip hay Python.

## Giao diện ERASA VIDEO

- Trắng, xám nhạt và cam theo logo ERASA.
- Preview lớn bên trái, danh sách xử lý bên phải.
- Tab **Video / Ảnh**.
- **Chọn tệp**, **Thêm thư mục**, kéo thả file hoặc thư mục.
- Cọ, tẩy, khung, elip, pan, mask mềm, undo, redo, reset và zoom.
- Đề xuất overlay tĩnh tổng quát cho video; người dùng phải kiểm tra/chỉnh mask.
- Preview 3 giây trước khi xử lý toàn bộ.

## Hàng đợi và độ ổn định

- Worker LaMa chạy ở process Python riêng; lỗi worker được bắt và ghi log, không chủ động đóng UI.
- Queue được lưu trong `%LocalAppData%\ERASA_VIDEO\queue.json`.
- Video được chia thành các đoạn 2 giây. **Tạm dừng / Tiếp tục / Thử lại** dùng lại các đoạn đã hoàn thành.
- **Hủy tác vụ** xóa checkpoint và file tạm của job.
- Pixel raw ngoài mask được chép lại từ frame gốc trước encode.
- Sau khi xuất video, worker kiểm tra lại độ phân giải, FPS, thời lượng và sự tồn tại của audio nếu nguồn có audio.

## Build không dùng dòng lệnh

1. Trong GitHub Desktop: **Repository** → **Show in Explorer**.
2. Giữ thư mục `.git`, thay source cũ bằng nội dung source này.
3. Summary nhập: `Rebuild ERASA VIDEO on original LaMa`.
4. Bấm **Commit to main** → **Push origin**.
5. Trên GitHub mở **Actions** → **Build ERASA Windows**.
6. Chỉ tải `ERASA-VIDEO-Windows-x64` khi workflow có dấu xanh.

## Kiểm thử trong workflow

- Test mask/undo/redo và queue retry/resume bằng .NET.
- Test detector không chọn full-frame ngẫu nhiên.
- Test pixel ngoài mask không đổi trong pipeline raw.
- Test checkpoint thay đổi khi mask thay đổi, tái sử dụng segment đã xong và dọn file segment dang dở.
- Tải source/model upstream có commit và checksum cố định.
- Chạy self-test thật với checkpoint LaMa gốc trên CPU.
- Tạo video có audio, xuất preview đúng 3 giây bằng LaMa frame-by-frame và kiểm tra kích thước/FPS/thời lượng/audio.

## Trạng thái

Đây là **source bàn giao**. Không gọi là ứng dụng Windows đã hoàn chỉnh cho đến khi GitHub Actions chạy xanh và artifact được mở thử trên máy Windows thực tế.
