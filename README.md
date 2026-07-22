# CleanFrame Video 2 — source milestone

Project mới hoàn toàn bằng **.NET 8 + WinUI 3**. Bản này không vá kiến trúc Python/PySide6 cũ.

## Luồng chính

**Thả video → Tự đề xuất → Chỉnh mask → Preview 3 giây → Xử lý tất cả**

- UI WinUI 3 là process riêng với worker.
- Worker dùng FFmpeg/FFprobe đóng gói trong artifact Windows.
- Reconstruction chính: optical flow Farneback hai chiều + confidence map.
- Spatial fallback: OpenCV Telea + Navier–Stokes, chỉ dùng cho pixel temporal thiếu tin cậy.
- Composite chỉ qua mask người dùng đã xác nhận.
- Không dùng FFmpeg `delogo`.
- Không có LaMa và không có ONNX Runtime DirectML trong milestone này.

## Dùng bằng GitHub Desktop (không cần CMD)

1. Giải nén ZIP này.
2. Trong **GitHub Desktop**, bấm **Repository** → **Show in Explorer**.
3. Xóa file dự án cũ nhưng giữ thư mục ẩn `.git`, rồi chép toàn bộ nội dung đã giải nén vào đó.
4. Trở lại **GitHub Desktop** → bấm **Commit to main** → **Push origin**.
5. Trên GitHub, mở **Actions** → **Build Windows**.
6. Chỉ khi run có dấu xanh, tải artifact **CleanFrameVideo2-Windows-x64**.

## Trạng thái bằng chứng

- Source project, worker, pipeline, tests và workflow đã được tạo.
- Hai video nguồn regression không có trong bộ bàn giao, nên chưa thể chạy lại end-to-end hai fixture `haizz(1).mp4` và `Wool_puppets_sleeping_in_moonlight_202607071922.mp4`.
- ZIP bàn giao chỉ chứng minh preview 3 giây đã tồn tại với audio, 24 fps và 72 frame; không chứng minh source này đã build trên Windows.
- Chỉ gọi là ứng dụng hoàn chỉnh sau khi workflow tạo artifact Windows và test log thật đạt.

Xem `docs/HANDOFF_AUDIT_VI.md`, `docs/ARCHITECTURE_VI.md` và `docs/TEST_STATUS_VI.md`.
