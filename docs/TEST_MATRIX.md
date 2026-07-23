# Ma trận kiểm thử ERASA VIDEO 0.3

| Yêu cầu | Test/source kiểm chứng |
|---|---|
| Media mở được khi LaMa lỗi | `MediaToolServiceTests.BundledFfmpeg_CreatesPreviewWithoutPythonOrLama` |
| Đọc kích thước/FPS/audio | `MediaToolServiceTests.ParseProbeJson_ReadsVideoAudioAndMetadata` |
| Bắt buộc xác nhận mask | `MediaItemTests.Item_CannotProcessUntilMaskIsExplicitlyConfirmed` |
| Detector không chọn vùng ngẫu nhiên/full-frame | `tests/python/test_worker_core.py` |
| Pixel ngoài mask không đổi trước encode | Python composite test + original-LaMa self-test |
| Mask undo/redo/raster ổn định | `MaskDocumentTests.cs`, `MaskRasterizerTests.cs` |
| Retry/resume queue | `JobStatePolicyTests.cs` + segment resume tests |
| Segment dang dở được dọn | `test_prepare_resume_directory_removes_partial_files` |
| Utility runtime hợp lệ | workflow `Packaged utility diagnose` |
| Original LaMa thực sự được nạp | workflow wiring check + packaged diagnose + CPU self-test |
| App không văng khi khởi động | workflow `Desktop startup smoke test` |
| Preview 3 giây giữ thông số/audio | workflow original-LaMa video integration test |
| Worker lỗi không đóng UI | catch theo từng job + queue continuation contract |
