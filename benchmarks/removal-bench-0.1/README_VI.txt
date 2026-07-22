CleanFrame Removal Bench 0.1

Mở hai file *_before_after_3s.mp4 trước.
Bên trái là video gốc, bên phải là kết quả phục hồi.

Mốc này:
- dùng mask người dùng đã xác nhận;
- phục hồi hai chiều theo thời gian bằng optical flow;
- dùng OpenCV inpaint làm dự phòng;
- chỉ thay đổi pixel bên trong mask;
- giữ audio trong preview;
- chưa dùng LaMa vì model không có trong môi trường chạy hiện tại;
- chưa phải ứng dụng Windows hoàn chỉnh.

Tiêu chí duyệt:
1. Dấu đã biến mất.
2. Không có mảng bệt hoặc nhấp nháy khó chịu.
3. Không làm hỏng vật thể/nền xung quanh.
