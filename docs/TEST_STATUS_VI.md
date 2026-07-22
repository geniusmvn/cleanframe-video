# Trạng thái kiểm thử tại thời điểm đóng gói source

## Đã tạo test

- detector chọn đúng overlay cố định trong synthetic fixture và trả rỗng khi chỉ có chuyển động ngẫu nhiên;
- alpha-mask detector ổn định qua nhiều frame window và không fallback về bounding box;
- raw compositor giữ nguyên pixel ngoài mask;
- temporal reconstruction thực tế giữ nguyên pixel ngoài mask trước encode;
- worker lỗi nhưng supervisor/UI host vẫn sống;
- exception path và cancel path dọn workspace/file `.partial.mp4`;
- giữ duration, fps, resolution và audio bằng synthetic FFmpeg fixture;
- preview 3 giây xuất thành công bằng synthetic fixture;
- queue retry, resume từ state file và giữ trạng thái paused sau khi mở lại;
- hai regression fixture gốc được đánh dấu Skip vì file nguồn không có trong ZIP bàn giao.

## Kiểm tra thực sự đã chạy trong môi trường đóng ZIP

- Đọc inventory 11 file trong ZIP: đạt.
- Parse README, report và pipeline notes: đạt.
- FFprobe 4 MP4 bàn giao: đạt; chi tiết ở `verification/handoff_ffprobe.json`.
- Parse XML cho XAML/csproj/props: xem `verification/source_checks.txt`.
- Static source structure/token/bracket check: xem `verification/source_checks.txt`.
- Kiểm tra integrity ZIP source: xem `verification/source_checks.txt`.

## Chưa chạy

- `dotnet restore` và `dotnet test`: môi trường đóng gói không có .NET SDK.
- Build WinUI/Windows artifact: môi trường hiện tại là Linux, không phải Windows runner.
- Worker `.exe --self-test`: chưa có executable Windows.
- End-to-end hai fixture gốc: thiếu `haizz(1).mp4` và `Wool_puppets_sleeping_in_moonlight_202607071922.mp4`.

GitHub Actions chỉ tạo artifact `CleanFrameVideo2-Windows-x64` nếu restore, test, worker self-test và publish WinUI đều đi qua.
