# ERASA VIDEO 2 — Original LaMa 1.3

Ứng dụng Windows xử lý ảnh và video trong vùng mask do người dùng xác nhận. Video ưu tiên phục hồi temporal từ frame lân cận; LaMa chỉ bổ sung phần còn thiếu. Mọi pixel ngoài mask được ghép lại từ frame gốc trước encode.

## Kiến trúc

- `Erasa.Video2.Core`: model, mask, state machine, queue và IPC protocol.
- `Erasa.Video2.Worker.Core`: FFmpeg, temporal reconstruction, gọi LaMa và cleanup/resume.
- `Erasa.Video2.Worker.Host`: process riêng, chỉ nhận request và phát JSON log.
- `Erasa.Video2.App`: .NET 8 + WinUI 3.
- `Erasa.Video2.Tests`: chỉ test Core và Worker.Core, không tham chiếu worker executable.

## Cách dùng source LaMa gốc mà không kéo dependency Lightning cũ vào Windows

1. Job Linux tải archive Big-LaMa đã ghim checksum và source `advimman/lama` tại commit `786f5936b27fb3dacd2b1ad799e4de968ea697e7`.
2. PyTorch `weights_only=True` chỉ đọc tensor từ checkpoint; metadata Lightning bị cô lập và bỏ qua.
3. Job Linux khởi tạo trực tiếp `FFCResNetGenerator` từ source gốc, nạp state, chạy forward test và xuất state chuẩn sang `generator.safetensors`.
4. Job Windows chỉ nhận `config.yaml` + `generator.safetensors`, cài PyTorch/OpenCV/Kornia/PyYAML/Safetensors, tải source LaMa gốc và chạy self-test.
5. Artifact cuối không chứa `pytorch-lightning`, TensorBoard, TorchMetrics, `future` hoặc `best.ckpt` thô.

Người dùng không phải cài Python, pip, CMD hoặc PowerShell. Ứng dụng chỉ dùng runtime đã được CI kiểm thử và đóng gói cạnh file EXE; runtime cũ trong AppData bị bỏ qua.

## Cổng kiểm thử CI

1. Source checks và Python pipeline tests.
2. Core + Worker.Core tests bằng Any CPU.
3. Worker Host + FFmpeg self-test.
4. Linux export và forward-test generator LaMa gốc.
5. Windows runtime self-test + video integration 3 giây có audio.
6. WinUI publish, startup smoke test, worker-failure survival test và artifact cuối.

Chỉ coi bản Windows là dùng được khi cả 6 job xanh và artifact `ERASA-VIDEO-2-Windows-x64` xuất hiện. Source ZIP không phải bằng chứng ứng dụng đã chạy thành công.
