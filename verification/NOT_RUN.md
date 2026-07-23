# Chưa chạy trong môi trường hiện tại

- `dotnet restore`
- `dotnet build`
- `dotnet test`
- `dotnet publish -r win-x64`
- Mở cửa sổ Avalonia trên Windows
- Original LaMa checkpoint self-test
- Original LaMa video integration test

Lý do: môi trường hiện tại không có .NET SDK và không phải Windows. GitHub Actions chứa các bước build, packaged runtime diagnostics, desktop startup smoke test, original-LaMa CPU self-test và video integration test. Chỉ lần Actions mới có dấu xanh mới là bằng chứng cho các bước này.
