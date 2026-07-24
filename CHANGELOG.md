# Changelog

## 1.3.0 — tensor-only original LaMa runtime

- Bỏ toàn bộ nhánh cài PyTorch-Lightning/TensorBoard/TorchMetrics/future trên Windows.
- Thêm job Linux đọc checkpoint gốc bằng `weights_only=True`, khởi tạo `FFCResNetGenerator` từ source gốc và chạy forward test.
- Xuất state chuẩn sang `generator.safetensors`; Windows không tải hoặc mở `best.ckpt` thô.
- Nâng runtime nhúng lên Python 3.10 + PyTorch 2.1.2 CUDA 11.8 có CPU fallback.
- Worker và ứng dụng chỉ dùng runtime được đóng gói; bỏ command cài runtime và bỏ fallback sang runtime cũ trong AppData.
- CI chia thành 6 cổng: source, Core, worker, Linux export, Windows integration và WinUI artifact.
- Thêm test C# cho trạng thái runtime safetensors và test Python cho exporter/builder.

## 1.1.0 — architecture reset

- Tách Worker.Core khỏi Worker.Host executable.
- Test project không còn tham chiếu worker `.exe`.
- Bỏ build/test trộn `x64` và `Any CPU`.
- Artifact Windows chỉ được tạo sau khi original LaMa video integration đạt.
