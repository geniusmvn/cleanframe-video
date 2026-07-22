# Ma trận kiểm thử

| Yêu cầu | Test/source kiểm chứng |
|---|---|
| Detector không chọn vùng ngẫu nhiên | `tests/python/test_worker_core.py` |
| Detector không trả full-frame | `test_static_detector_does_not_return_random_full_frame` |
| Pixel ngoài mask không đổi trước encode | `test_composite_contract_keeps_outside_mask` + LaMa self-test |
| Mask undo/redo/raster ổn định | `MaskDocumentTests.cs`, `MaskRasterizerTests.cs` |
| Retry/resume queue | `JobStatePolicyTests.cs` + `test_completed_segment_is_reused_for_resume` |
| Checkpoint đổi khi mask đổi | `test_resume_signature_is_stable_and_changes_with_mask` |
| File segment dang dở được dọn | `test_prepare_resume_directory_removes_partial_files` |
| LaMa gốc thực sự được nạp | workflow `Verify original LaMa wiring` và CPU self-test |
| Preview 3 giây và giữ kích thước/FPS/thời lượng/audio | worker `verify_video_contract` + workflow video integration test 3 giây |
| Worker lỗi không đóng UI | mọi lệnh worker chạy qua `WorkerClient`, được catch theo từng job |
