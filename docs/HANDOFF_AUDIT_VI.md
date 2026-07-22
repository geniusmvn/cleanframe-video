# Kiểm tra bộ bàn giao Removal Bench 0.1

## Nội dung đã đọc

Bộ ZIP có 11 file: README tiếng Việt, `results/report.json`, 2 video so sánh trước/sau, 2 clean preview, 4 ảnh trước/sau và `src/pipeline_notes.py`.

`pipeline_notes.py` chỉ là ghi chú tài liệu: Telea + Navier–Stokes tạo spatial candidate, reference được tạm inpaint, Farneback truyền kết quả sạch theo hai chiều, fuse temporal/spatial, rồi composite qua soft mask đã xác nhận.

## ZIP chứng minh

- Có 2 video so sánh trước/sau và 2 clean preview; FFprobe đo từng MP4 đúng 3.000 giây.
- Mỗi MP4 có video H.264 1280×720, 24 fps, 72 frame và audio AAC stereo 48 kHz.
- README, report và pipeline notes nhất quán rằng bench dùng optical flow Farneback hai chiều.
- Spatial fallback được ghi là blend Telea + Navier–Stokes.
- `lama_used` trong report là `false`; Removal Bench 0.1 không dùng LaMa.
- Report gắn hai job với nguồn `haizz(1).mp4` và `Wool_puppets_sleeping_in_moonlight_202607071922.mp4`, cùng mốc preview 3 giây.
- Các preview/ảnh cho phép kiểm tra trực quan baseline mà người dùng đã duyệt.

## Điểm cần diễn giải chính xác

- README và pipeline notes nói chỉ composite qua mask đã xác nhận.
- Tuy nhiên report ghi `outside_mask_max_raw_pixel_change` là **9** cho sparkle và **8** cho overlay chữ. Vì vậy ZIP không chứng minh byte ngoài mask hoàn toàn bất biến; source mới có test riêng yêu cầu giá trị raw ngoài mask không đổi chính xác trước encode.
- Audio tồn tại trong preview, nhưng ZIP không cung cấp lệnh mux/encode hoặc hash audio stream để chứng minh audio bit-identical với nguồn.

## ZIP chưa chứng minh

- Không có hai video nguồn, nên không thể tái chạy regression end-to-end.
- Không có source thực thi của bench; `pipeline_notes.py` là tài liệu, không phải implementation chạy được.
- Không có mask nguồn, lệnh FFmpeg, log chạy, dependency lock hoặc test executable.
- Không chứng minh worker process riêng, queue, pause/cancel/retry/resume hay UI sống sau lỗi worker.
- Không chứng minh artifact Windows.

## Kết luận dùng cho milestone này

Giữ nguyên preview đã duyệt làm baseline thị giác; không dựng lại hoặc thay thế chúng từ dữ liệu thiếu. Source CleanFrame Video 2 chỉ được phép tuyên bố những gì build/test thực tế xác nhận.
