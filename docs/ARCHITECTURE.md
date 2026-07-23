# Kiến trúc ERASA VIDEO 0.3

## Desktop UI

- .NET 8 + Avalonia 11.
- `MediaToolService` gọi FFmpeg/FFprobe trực tiếp để đọc metadata và thumbnail.
- Canvas chỉnh mask không phụ thuộc Python, PyTorch hoặc model.
- `WorkerClient` chỉ khởi chạy Python khi cần tự động đề xuất hoặc inference.
- Utility runtime và inference runtime được chẩn đoán riêng.
- Mọi exception được chuyển thành trạng thái job, error panel và log; không gọi `Environment.Exit` hoặc `FailFast`.

## Original LaMa worker

`worker/erasa_worker.py` dùng source được clone từ `advimman/lama`:

1. thêm `runtime/lama` vào `sys.path`;
2. import `load_checkpoint` từ `saicinpainting.training.trainers`;
3. đọc `big-lama/config.yaml`;
4. nạp `big-lama/models/best.ckpt`;
5. đưa batch `{image, mask}` vào model;
6. lấy output `batch["inpainted"]`;
7. composite lại pixel raw ngoài mask.

## Mask confirmation

- Tự động đề xuất và thao tác vẽ chỉ tạo/chỉnh dữ liệu mask.
- Mọi thay đổi làm `MaskConfirmed = false`.
- Chỉ nút **Xác nhận mask** mới chuyển job sang `Ready`.
- Preview và queue filter bỏ qua mọi job chưa được xác nhận.

## Video

- FFmpeg decode BGR24 raw frames.
- Mỗi frame chạy cùng original-LaMa checkpoint và mask xác nhận.
- Encode H.264 theo segment ngắn để có checkpoint resume.
- Ghép segment và mux audio nguồn.
- Xác minh metadata output trước khi báo hoàn thành.
