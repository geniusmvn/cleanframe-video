# ERASA VIDEO 0.3 — Original LaMa

Ứng dụng Windows xử lý **ảnh và video theo mask do người dùng tự kiểm tra và xác nhận**, sử dụng trực tiếp mã nguồn gốc `advimman/lama`.

## Luồng sử dụng

**Chọn tệp → Chọn vùng thủ công hoặc Tự động đề xuất → Xác nhận mask → Preview 3 giây → Xử lý**

- Ảnh/video được đọc và tạo thumbnail bằng FFmpeg đóng gói, **không phụ thuộc Python hoặc LaMa**.
- Vì vậy canvas, timeline, cọ, tẩy, khung, elip, pan, zoom, undo/redo vẫn sử dụng được ngay cả khi runtime AI gặp lỗi.
- Tự động đề xuất chỉ tạo mask nháp. Ứng dụng không cho preview hoặc xử lý cho đến khi người dùng bấm **Xác nhận mask**.
- Lỗi đọc tệp và lỗi runtime được hiện đầy đủ trong giao diện, có nút **Thử đọc lại** và **Mở log**.

## Nền tảng xử lý

- Workflow tải đúng repository `advimman/lama` tại commit cố định `786f5936b27fb3dacd2b1ad799e4de968ea697e7`.
- Worker import trực tiếp `from saicinpainting.training.trainers import load_checkpoint`.
- Model dùng cấu trúc gốc `config.yaml` + `models/best.ckpt`.
- Không dùng ONNX, TorchScript chuyển đổi, `simple-lama`, `lama-cleaner` hoặc FFmpeg `delogo`.
- LaMa chạy ở process Python riêng; UI không nạp PyTorch/model và không tự đóng khi worker lỗi.

## Giao diện

- Tên và logo **ERASA VIDEO**.
- Tông trắng, xám nhạt và cam theo mẫu đã cung cấp.
- Preview lớn bên trái, hàng đợi bên phải.
- Tab **Video / Ảnh**, thêm nhiều tệp, thêm thư mục và kéo thả.
- Cọ, tẩy, khung, elip, pan, zoom, mask mềm, hoàn tác, làm lại và đặt lại.
- Trạng thái runtime FFmpeg, công cụ đề xuất và LaMa được kiểm tra riêng.

## Hàng đợi và video

- Queue được lưu tại `%LocalAppData%\ERASA_VIDEO\queue.json`.
- Video được chia thành segment để **Tạm dừng / Tiếp tục / Thử lại** không phải chạy lại toàn bộ.
- **Hủy** dọn state và file tạm của job.
- Pixel raw ngoài mask được chép lại từ frame gốc trước encode.
- Sau encode, worker kiểm tra độ phân giải, FPS, thời lượng và audio.

## Kiểm thử trong GitHub Actions

Workflow thực hiện theo thứ tự:

1. Restore và build toàn bộ solution .NET.
2. Chạy test mask, queue, xác nhận mask và parser FFprobe.
3. Tạo video có audio bằng FFmpeg và kiểm tra `MediaToolService` tạo thumbnail mà không gọi Python/LaMa.
4. Chạy Python unit tests và source verification.
5. Publish ứng dụng Windows.
6. Chẩn đoán utility runtime và original-LaMa runtime trong chính artifact đã ghép.
7. Mở thật `ERASA.Video.exe` trong smoke test và kiểm tra process không văng khi khởi động.
8. Chạy original LaMa CPU self-test.
9. Chạy integration video 3 giây, kiểm tra kích thước, FPS, thời lượng và audio.

## Cách đưa source vào GitHub Desktop

1. **Repository** → **Show in Explorer**.
2. Giữ thư mục `.git`, thay toàn bộ source cũ bằng source 0.3.
3. Summary: `Rebuild ERASA VIDEO 0.3`
4. **Commit to main** → **Push origin**.
5. Chỉ tải `ERASA-VIDEO-Windows-x64` sau khi Actions có dấu xanh.

## Trạng thái bàn giao

Source 0.3 đã được kiểm tra Python/XML/YAML và các hợp đồng tĩnh trong môi trường hiện tại. Không có .NET SDK hoặc Windows GUI runner trong phiên này, nên **không tuyên bố build Windows thành công** trước khi GitHub Actions mới chạy xanh.
