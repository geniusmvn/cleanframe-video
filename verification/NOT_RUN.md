# Chưa chạy trong môi trường tạo source

Môi trường tạo gói source này không có .NET SDK và không tải được dependency/runtime lớn từ Internet, nên các bước sau **chưa được chạy tại đây**:

- `dotnet restore`
- `dotnet test`
- `dotnet publish` Windows x64
- tải source `advimman/lama` và `big-lama.zip`
- nạp checkpoint LaMa gốc / CPU self-test thật
- video integration test 3 giây bằng checkpoint thật
- mở artifact trên máy Windows thực tế

GitHub Actions trong source được cấu hình để thực hiện các bước đó. Chỉ coi là có build Windows khi workflow chạy xanh và có artifact `ERASA-VIDEO-Windows-x64`.
