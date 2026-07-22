# CleanFrame Video

Ứng dụng Windows **video-first** để:

- thêm nhiều video/ảnh;
- đề xuất vùng overlay cố định tổng quát;
- chọn vùng thủ công bằng khung chữ nhật;
- áp dụng cùng vùng theo tỷ lệ khung hình;
- xử lý hàng loạt bằng LaMa ONNX;
- giữ âm thanh gốc khi xuất video.

> Dự án không chứa bộ nhận diện chuyên biệt cho thương hiệu/nền tảng cụ thể. Chế độ tự động chỉ đề xuất overlay cố định tổng quát và luôn yêu cầu người dùng xác nhận.

## Build Windows

GitHub Actions tự build sau mỗi lần push:

1. Mở tab **Actions**.
2. Chọn **Build Windows**.
3. Chờ dấu xanh.
4. Tải artifact `CleanFrameVideo-Windows-x64`.

Bản build kèm:

- `CleanFrameVideo.exe`
- LaMa ONNX 512×512
- FFmpeg
- runtime cần thiết

## Luồng sử dụng

1. Thêm file hoặc thư mục.
2. Chọn một video mẫu.
3. Bấm **Tự tìm overlay** hoặc kéo khung thủ công.
4. Chọn **Áp dụng cho các file cùng tỷ lệ**.
5. Chọn thư mục xuất.
6. Bấm **Xử lý tất cả**.

## Trạng thái

Đây là bản nền tảng đầu tiên để kiểm tra quy trình build Windows và xử lý video cố định. Chưa có theo dõi vật thể di chuyển.
