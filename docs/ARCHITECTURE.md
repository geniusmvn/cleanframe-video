# Kiến trúc ERASA VIDEO

## Desktop UI

- .NET 8 + Avalonia 11.
- UI process không nạp PyTorch hoặc model.
- `WorkerClient` khởi chạy Python đóng gói như process riêng và đọc JSON Lines UTF-8.
- Mọi exception từ worker được chuyển thành trạng thái job và log, không gọi `Environment.Exit`.

## Original LaMa worker

`worker/erasa_worker.py` dùng source được clone từ `advimman/lama`:

1. thêm `runtime/lama` vào `sys.path`;
2. import `load_checkpoint` từ `saicinpainting.training.trainers`;
3. đọc `big-lama/config.yaml`;
4. nạp `big-lama/models/best.ckpt`;
5. đưa batch `{image, mask}` vào model;
6. lấy output `batch["inpainted"]`;
7. composite lại pixel raw ngoài mask.

## Video

- FFmpeg decode thành BGR24 raw frames.
- Mỗi frame chạy qua cùng model LaMa gốc và mask được xác nhận.
- Encode H.264 theo đoạn ngắn để có checkpoint.
- Ghép segment bằng stream copy, sau đó mux audio nguồn.
- Xác minh metadata output trước khi báo hoàn thành.
