# Chẩn đoán runtime

ERASA VIDEO phân tách ba lớp:

1. **FFmpeg** — đọc file, metadata, thumbnail và timeline.
2. **Utility worker** — OpenCV/NumPy cho tự động đề xuất vùng tĩnh.
3. **Original LaMa** — source `advimman/lama` + checkpoint `big-lama` cho inpainting.

Thanh trạng thái hiển thị từng lớp riêng. Nếu LaMa lỗi, người dùng vẫn mở file và chỉnh mask. Log nằm tại `%LocalAppData%\ERASA_VIDEO\logs`.
