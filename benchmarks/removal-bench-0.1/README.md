# Removal Bench 0.1 baseline

Thư mục này giữ metadata và hash của bộ bàn giao đã được người dùng duyệt.

- Không chép lại 4 MP4/4 PNG vào source ZIP để tránh nhân đôi artifact.
- Hai video nguồn regression không có trong bộ bàn giao, nên không có test end-to-end cho hai fixture gốc.
- `report.json` ghi `lama_used: false`; source CleanFrame Video 2 không được phép diễn giải baseline này là LaMa.
