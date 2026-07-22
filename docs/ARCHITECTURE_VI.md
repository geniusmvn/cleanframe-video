# Kiến trúc CleanFrame Video 2

## Process

- `CleanFrame.Video2.App`: WinUI 3, không chạy OpenCV/FFmpeg trong UI process.
- `CleanFrame.Video2.Worker`: console process riêng, nhận JSON Lines qua stdin và trả event qua stdout.
- Worker crash được chuyển thành trạng thái `Failed`; UI process không bị kết thúc theo.
- Khi người dùng retry và worker đã chết, supervisor thử mở worker process mới.

## Pipeline

1. FFprobe đọc fps, kích thước, thời lượng và audio stream.
2. Detector lấy nhiều frame mẫu; chấm persistence của edge và motion thấp trên toàn khung hình, không chỉ bốn góc.
3. Detector tạo alpha mask từ connected component/morphology và contour, không dùng nguyên bounding box.
4. Người dùng chỉnh mask bằng rectangle, ellipse, brush, eraser; mask lưu dưới dạng thao tác tọa độ chuẩn hóa.
5. UI chỉ áp cùng mask cho video có cùng tỉ lệ khung hình đã đọc được; mỗi job giữ một snapshot mask riêng để chỉnh sửa sau đó không làm đổi job đang chờ.
6. Worker extract frame vào workspace tạm.
7. Với từng frame, current/reference được tạm inpaint trong mask để mask cố định không làm lệch flow.
8. Farneback flow current→reference và reference→current tạo warp và forward/backward consistency confidence.
9. Fuse temporal hai chiều. Telea + Navier–Stokes chỉ đóng góp khi confidence temporal thiếu.
10. Ổn định màu ở inner/outer boundary ring.
11. Composite alpha chỉ trong mask; raw frame ngoài mask được clone nguyên giá trị trước encode.
12. FFmpeg encode vào file `.partial.mp4` ẩn cùng thư mục/ổ đĩa với file đích, giữ audio bằng stream copy khi container cho phép và fallback AAC khi cần.
13. Chỉ sau encode thành công mới đổi file staging thành file đích; file staging và workspace đều bị xóa khi hoàn thành, lỗi hoặc cancel.

## Fast / Beautiful

- Fast: flow ở nửa độ phân giải rồi upscale vector về frame gốc.
- Beautiful: flow full resolution và confidence chặt hơn.
- Cả hai chạy CPU; chưa thêm ONNX Runtime DirectML vì pipeline này chưa cần model.
