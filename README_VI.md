# LaMa Studio 0.1.0 — source milestone

Ứng dụng Windows mới, tách biệt hoàn toàn khỏi CleanFrame Video 2 cũ.

## Mục tiêu bản đầu

- Mở ảnh hoặc video.
- Người dùng tự vẽ mask bằng Cọ, Tẩy hoặc Khung.
- Ảnh: chạy LaMa một lần và chỉ ghép kết quả trong mask.
- Video: chạy LaMa trên từng frame với cùng mask, ghép lại bằng FFmpeg và giữ audio.
- Chọn Tự động / NVIDIA GPU / CPU.
- Preview ảnh hoặc preview video 3 giây trước khi xuất đầy đủ.
- Không có detector tự động và không dùng FFmpeg delogo.
- Người dùng không phải cài Python, pip, CMD hoặc PowerShell. Artifact Windows đóng gói runtime riêng.

## Nguồn LaMa

- Kiến trúc gốc: `advimman/lama`, Apache-2.0.
- Runtime dùng TorchScript `big-lama.pt` được IOPaint phân phối từ implementation LaMa.
- Attribution và checksum nằm trong `THIRD_PARTY_NOTICES.md` và workflow.

LaMa gốc là mô hình inpainting ảnh. Chế độ video của ứng dụng giải mã video thành frame, chạy LaMa trên từng frame rồi mã hóa lại; không tuyên bố đây là mô hình LaMa được huấn luyện riêng cho video.

## Build bằng GitHub Desktop

1. Chép toàn bộ source vào repo.
2. Trong GitHub Desktop nhập Summary: `Add LaMa Studio source`.
3. Bấm **Commit to main**.
4. Bấm **Push origin**.
5. Mở GitHub → **Actions** → **Build Windows**.
6. Chỉ tải artifact `LaMaStudio-Windows-x64` khi workflow có dấu xanh.

## Trạng thái kiểm chứng

Trong môi trường tạo source này không có .NET SDK và không có mạng để restore NuGet, nên chưa chạy build hoặc model inference tại đây. GitHub Actions có các bước bắt buộc:

- build ứng dụng;
- unit test;
- kiểm tra checksum model;
- self-test LaMa trên ảnh tổng hợp;
- publish artifact Windows.

Chưa được gọi đây là ứng dụng hoàn chỉnh cho đến khi workflow xanh và artifact chạy được trên Windows.
