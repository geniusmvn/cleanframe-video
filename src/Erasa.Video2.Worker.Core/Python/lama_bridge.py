from __future__ import annotations

import argparse
import collections
import json
import math
import os
import shutil
import subprocess
import sys
import traceback
import types
from pathlib import Path
from typing import Iterable, Optional

import cv2
import numpy as np
import torch
import yaml


def configure_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            reconfigure(encoding="utf-8", errors="backslashreplace")


configure_stdio()


def emit(kind: str, progress: Optional[float] = None, message: Optional[str] = None, **extra) -> None:
    payload = {"kind": kind, "progress": progress, "message": message, **extra}
    print(json.dumps(payload, ensure_ascii=True), flush=True)


def runtime_paths(runtime: str) -> tuple[Path, Path, Path]:
    root = Path(runtime).resolve()
    source = root / "lama-source"
    config = root / "model" / "config.yaml"
    checkpoint = root / "model" / "models" / "best.ckpt"
    if not (source / "saicinpainting" / "training" / "modules" / "ffc.py").exists():
        raise FileNotFoundError("Thiếu source advimman/lama đã ghim commit.")
    if not config.exists() or not checkpoint.exists():
        raise FileNotFoundError("Thiếu checkpoint Big-LaMa.")
    return source, config, checkpoint


def _get_shape(value):
    if torch.is_tensor(value):
        return tuple(value.shape)
    if isinstance(value, dict):
        return {name: _get_shape(item) for name, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_get_shape(item) for item in value]
    if isinstance(value, (int, float)):
        return type(value)
    raise ValueError(f"unexpected type {type(value)}")


def import_original_generator(source: Path):
    source_text = str(source)
    if source_text not in sys.path:
        sys.path.insert(0, source_text)

    # The original FFC implementation only needs get_shape from saicinpainting.utils.
    # Injecting this tiny compatibility module avoids importing the original training-only
    # PyTorch-Lightning stack while the actual generator implementation remains upstream code.
    compatibility = types.ModuleType("saicinpainting.utils")
    compatibility.get_shape = _get_shape
    sys.modules["saicinpainting.utils"] = compatibility

    from saicinpainting.training.modules.ffc import FFCResNetGenerator  # type: ignore

    module_file = Path(sys.modules[FFCResNetGenerator.__module__].__file__).resolve()
    expected = (source / "saicinpainting" / "training" / "modules" / "ffc.py").resolve()
    if module_file != expected:
        raise RuntimeError(f"Đã import sai source LaMa: {module_file}")
    return FFCResNetGenerator


def lookup_path(root: dict, path: str):
    value = root
    for part in path.split("."):
        value = value[part]
    return value


def resolve_config(value, root: dict):
    if isinstance(value, dict):
        return {key: resolve_config(item, root) for key, item in value.items()}
    if isinstance(value, list):
        return [resolve_config(item, root) for item in value]
    if isinstance(value, str) and value.startswith("${") and value.endswith("}"):
        return resolve_config(lookup_path(root, value[2:-1]), root)
    return value


class LamaModel:
    def __init__(self, runtime: str, requested_device: str):
        source, config_path, checkpoint_path = runtime_paths(runtime)
        self.requested_device = requested_device
        self.device = choose_device(requested_device)
        emit("log", 0.01, f"Đang nạp source LaMa gốc trên {self.device.type.upper()}…")
        generator_class = import_original_generator(source)
        config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
        generator_config = resolve_config(config["generator"], config)
        generator_config.pop("kind", None)
        self.generator = generator_class(**generator_config)
        checkpoint = torch.load(str(checkpoint_path), map_location="cpu")
        state_dict = checkpoint.get("state_dict", checkpoint)
        generator_state = {}
        for key, value in state_dict.items():
            if key.startswith("generator."):
                generator_state[key[len("generator."):]] = value
        if not generator_state:
            generator_state = state_dict
        missing, unexpected = self.generator.load_state_dict(generator_state, strict=False)
        if len(missing) > 8 or len(unexpected) > 8:
            raise RuntimeError(
                f"Checkpoint không khớp kiến trúc LaMa gốc (missing={len(missing)}, unexpected={len(unexpected)})."
            )
        self.generator.eval()
        try:
            self.generator.to(self.device)
        except RuntimeError as error:
            if self.requested_device == "auto" and self.device.type == "cuda":
                emit("log", 0.02, f"CUDA không khởi tạo được ({error}). Đang chuyển sang CPU…")
                self.device = torch.device("cpu")
                self.generator.to(self.device)
            else:
                raise
        for parameter in self.generator.parameters():
            parameter.requires_grad_(False)
        emit("log", 0.03, "Đã nạp Big-LaMa từ checkpoint gốc.")

    def _run_generator(self, model_input: torch.Tensor) -> np.ndarray:
        with torch.no_grad():
            predicted = self.generator(model_input)
        return predicted[0].permute(1, 2, 0).detach().float().cpu().numpy()

    def _fallback_to_cpu(self, reason: Exception) -> None:
        if self.requested_device != "auto" or self.device.type != "cuda":
            raise reason
        emit("log", None, f"CUDA gặp lỗi ({reason}). Tự động chuyển sang CPU…")
        try:
            torch.cuda.empty_cache()
        except Exception:
            pass
        self.device = torch.device("cpu")
        self.generator.to(self.device)

    @torch.no_grad()
    def predict(self, bgr: np.ndarray, mask: np.ndarray, max_side: int) -> np.ndarray:
        if bgr.ndim != 3 or bgr.shape[2] != 3:
            raise ValueError("Ảnh đầu vào phải là BGR 3 kênh.")
        mask = np.clip(mask.astype(np.float32), 0, 1)
        if not np.any(mask > 0.001):
            return bgr.copy()

        y1, y2, x1, x2 = mask_crop(mask, padding=96)
        crop = bgr[y1:y2, x1:x2]
        crop_mask = mask[y1:y2, x1:x2]
        original_h, original_w = crop.shape[:2]
        scale = min(1.0, max_side / max(original_h, original_w))
        if scale < 1:
            size = (max(8, int(round(original_w * scale))), max(8, int(round(original_h * scale))))
            crop = cv2.resize(crop, size, interpolation=cv2.INTER_AREA)
            crop_mask = cv2.resize(crop_mask, size, interpolation=cv2.INTER_LINEAR)

        padded, padded_mask, unpad_h, unpad_w = pad_modulo(crop, crop_mask, 8)
        rgb = cv2.cvtColor(padded, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        image_tensor = torch.from_numpy(rgb.transpose(2, 0, 1)).unsqueeze(0).to(self.device)
        mask_tensor = torch.from_numpy(padded_mask).unsqueeze(0).unsqueeze(0).to(self.device)
        model_input = torch.cat([image_tensor * (1 - mask_tensor), mask_tensor], dim=1)
        try:
            predicted = self._run_generator(model_input)
        except RuntimeError as error:
            self._fallback_to_cpu(error)
            image_tensor = image_tensor.cpu()
            mask_tensor = mask_tensor.cpu()
            model_input = torch.cat([image_tensor * (1 - mask_tensor), mask_tensor], dim=1)
            predicted = self._run_generator(model_input)
        predicted = np.clip(predicted[:unpad_h, :unpad_w] * 255.0, 0, 255).astype(np.uint8)
        predicted = cv2.cvtColor(predicted, cv2.COLOR_RGB2BGR)
        if scale < 1:
            predicted = cv2.resize(predicted, (original_w, original_h), interpolation=cv2.INTER_CUBIC)

        result = bgr.copy()
        alpha = crop_mask[..., None]
        restored = np.clip(crop.astype(np.float32) * (1 - alpha) + predicted.astype(np.float32) * alpha, 0, 255).astype(np.uint8)
        result[y1:y2, x1:x2] = restored
        return result


def choose_device(requested: str) -> torch.device:
    if requested == "cuda":
        if not torch.cuda.is_available():
            raise RuntimeError("Không tìm thấy CUDA. Hãy cài profile CPU hoặc chọn Tự động.")
        return torch.device("cuda")
    if requested == "auto" and torch.cuda.is_available():
        return torch.device("cuda")
    return torch.device("cpu")


def pad_modulo(image: np.ndarray, mask: np.ndarray, modulo: int):
    height, width = image.shape[:2]
    pad_h = (-height) % modulo
    pad_w = (-width) % modulo
    image_padded = cv2.copyMakeBorder(image, 0, pad_h, 0, pad_w, cv2.BORDER_REFLECT_101)
    mask_padded = cv2.copyMakeBorder(mask, 0, pad_h, 0, pad_w, cv2.BORDER_CONSTANT, value=0)
    return image_padded, mask_padded, height, width


def mask_crop(mask: np.ndarray, padding: int) -> tuple[int, int, int, int]:
    ys, xs = np.where(mask > 0.001)
    if len(xs) == 0:
        return 0, mask.shape[0], 0, mask.shape[1]
    x1 = max(0, int(xs.min()) - padding)
    x2 = min(mask.shape[1], int(xs.max()) + padding + 1)
    y1 = max(0, int(ys.min()) - padding)
    y2 = min(mask.shape[0], int(ys.max()) + padding + 1)
    return y1, y2, x1, x2


def read_mask(path: str, width: int, height: int) -> np.ndarray:
    mask = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if mask is None:
        raise RuntimeError("Không đọc được mask đã xác nhận.")
    if mask.shape[:2] != (height, width):
        mask = cv2.resize(mask, (width, height), interpolation=cv2.INTER_LINEAR)
    return mask.astype(np.float32) / 255.0


def safe_imread(path: str) -> np.ndarray:
    data = np.fromfile(path, dtype=np.uint8)
    image = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError("Không đọc được ảnh đầu vào.")
    return image


def safe_imwrite(path: str, image: np.ndarray) -> None:
    extension = Path(path).suffix or ".png"
    success, encoded = cv2.imencode(extension, image)
    if not success:
        raise RuntimeError("Không mã hóa được ảnh kết quả.")
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.stem + ".partial" + target.suffix)
    encoded.tofile(str(temporary))
    os.replace(str(temporary), str(target))


def probe_video(path: str, ffprobe: str) -> dict:
    command = [
        ffprobe, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,duration,nb_frames",
        "-show_entries", "format=duration", "-of", "json", path,
    ]
    data = json.loads(subprocess.check_output(command, text=True, encoding="utf-8"))
    stream = data["streams"][0]
    rate = stream.get("avg_frame_rate") or stream.get("r_frame_rate") or "30/1"
    numerator, denominator = rate.split("/")
    fps = float(numerator) / max(float(denominator), 1.0)
    duration = float(stream.get("duration") or data.get("format", {}).get("duration") or 0)
    frames = int(stream.get("nb_frames") or round(duration * fps) or 1)
    return {
        "width": int(stream["width"]),
        "height": int(stream["height"]),
        "fps": fps,
        "duration": duration,
        "frames": frames,
    }


def read_exact(pipe, size: int) -> Optional[bytes]:
    chunks = []
    remaining = size
    while remaining:
        block = pipe.read(remaining)
        if not block:
            break
        chunks.append(block)
        remaining -= len(block)
    data = b"".join(chunks)
    return data if len(data) == size else None


def color_stabilize(candidate: np.ndarray, target: np.ndarray, known_ring: np.ndarray) -> np.ndarray:
    if np.count_nonzero(known_ring) < 32:
        return candidate
    ring = known_ring.astype(bool)
    target_mean = target[ring].astype(np.float32).mean(axis=0)
    candidate_mean = candidate[ring].astype(np.float32).mean(axis=0)
    delta = np.clip(target_mean - candidate_mean, -18, 18)
    return np.clip(candidate.astype(np.float32) + delta, 0, 255).astype(np.uint8)


def temporal_reconstruct(
    current: np.ndarray,
    neighbors: Iterable[np.ndarray],
    mask: np.ndarray,
    quality: str,
) -> tuple[np.ndarray, np.ndarray]:
    hard_mask = mask > 0.02
    if not np.any(hard_mask):
        return current.copy(), np.ones(mask.shape, np.float32)
    padding = 72 if quality == "beautiful" else 48
    y1, y2, x1, x2 = mask_crop(mask, padding)
    target = current[y1:y2, x1:x2]
    local_mask = hard_mask[y1:y2, x1:x2].astype(np.uint8)
    target_gray = cv2.cvtColor(target, cv2.COLOR_BGR2GRAY)
    ring = cv2.dilate(local_mask, np.ones((9, 9), np.uint8), iterations=1) - local_mask
    grid_x, grid_y = np.meshgrid(np.arange(target.shape[1], dtype=np.float32), np.arange(target.shape[0], dtype=np.float32))
    weighted = np.zeros_like(target, dtype=np.float32)
    weights = np.zeros(target.shape[:2], dtype=np.float32)
    neighbor_list = list(neighbors)

    for neighbor_full in neighbor_list:
        neighbor = neighbor_full[y1:y2, x1:x2]
        neighbor_gray = cv2.cvtColor(neighbor, cv2.COLOR_BGR2GRAY)
        flow_forward = cv2.calcOpticalFlowFarneback(target_gray, neighbor_gray, None, 0.5, 3, 19, 3, 5, 1.2, 0)
        map_x = grid_x + flow_forward[..., 0]
        map_y = grid_y + flow_forward[..., 1]
        candidate = cv2.remap(neighbor, map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT_101)
        candidate = color_stabilize(candidate, target, ring)

        flow_backward = cv2.calcOpticalFlowFarneback(neighbor_gray, target_gray, None, 0.5, 3, 19, 3, 5, 1.2, 0)
        back_x = cv2.remap(flow_backward[..., 0], map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
        back_y = cv2.remap(flow_backward[..., 1], map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
        consistency = np.sqrt((flow_forward[..., 0] + back_x) ** 2 + (flow_forward[..., 1] + back_y) ** 2)
        confidence = np.exp(-consistency / (2.5 if quality == "beautiful" else 3.5)).astype(np.float32)
        source_known = cv2.remap((1 - local_mask).astype(np.float32), map_x, map_y, cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT)
        confidence *= np.clip(source_known, 0, 1)
        confidence *= local_mask
        weighted += candidate.astype(np.float32) * confidence[..., None]
        weights += confidence

    fill = target.copy().astype(np.float32)
    valid = weights > (0.18 if quality == "beautiful" else 0.3)
    fill[valid] = weighted[valid] / np.maximum(weights[valid, None], 1e-6)
    confidence_full = np.zeros(mask.shape, np.float32)
    confidence_full[y1:y2, x1:x2] = np.clip(weights / max(1, len(neighbor_list)), 0, 1)
    result = current.copy()
    result[y1:y2, x1:x2] = np.clip(fill, 0, 255).astype(np.uint8)
    return result, confidence_full


def restore_frame(model: LamaModel, current: np.ndarray, neighbors: list[np.ndarray], mask: np.ndarray, quality: str) -> np.ndarray:
    if not np.any(mask > 0.001):
        return current.copy()
    temporal, confidence = temporal_reconstruct(current, neighbors, mask, quality)
    threshold = 0.18 if quality == "beautiful" else 0.32
    temporal_valid = confidence >= threshold
    unresolved = mask.copy()
    unresolved[temporal_valid] = 0
    unresolved = cv2.dilate((unresolved * 255).astype(np.uint8), np.ones((5, 5), np.uint8), iterations=1).astype(np.float32) / 255.0
    unresolved *= mask

    if np.any(unresolved > 0.01):
        model_max = 1024 if quality == "beautiful" else 768
        lama = model.predict(current, unresolved, model_max)
    else:
        lama = current

    temporal_weight = np.clip((confidence - threshold) / max(1e-6, 1 - threshold), 0, 1)[..., None]
    fill = lama.astype(np.float32) * (1 - temporal_weight) + temporal.astype(np.float32) * temporal_weight
    alpha = mask[..., None]
    result = current.astype(np.float32) * (1 - alpha) + fill * alpha
    result = np.clip(result, 0, 255).astype(np.uint8)
    result[mask <= 0.001] = current[mask <= 0.001]
    return result


def cmd_inpaint_image(args) -> None:
    image = safe_imread(args.input)
    mask = read_mask(args.mask, image.shape[1], image.shape[0])
    model = LamaModel(args.runtime, args.device)
    emit("progress", 0.15, "Đang xử lý ảnh bằng LaMa gốc…")
    result = model.predict(image, mask, 1024 if args.quality == "beautiful" else 768)
    safe_imwrite(args.output, result)
    emit("completed", 1, "Đã xử lý ảnh.", outputPath=args.output, width=image.shape[1], height=image.shape[0])


def cmd_process_video_segment(args) -> None:
    info = probe_video(args.input, args.ffprobe)
    width, height, fps = info["width"], info["height"], info["fps"]
    mask = read_mask(args.mask, width, height)
    model = LamaModel(args.runtime, args.device)
    lookahead = 2 if args.quality == "beautiful" else 1
    trim_start = max(0.0, args.trim_start)
    trim_duration = args.trim_duration if args.trim_duration is not None else args.duration
    trim_start_frame = max(0, int(round(trim_start * fps)))
    trim_frame_count = max(1, int(round(trim_duration * fps)))
    trim_end_frame = trim_start_frame + trim_frame_count
    expected = trim_frame_count

    decode = [
        args.ffmpeg, "-v", "error", "-ss", f"{args.start:.6f}", "-t", f"{args.duration:.6f}",
        "-i", args.input, "-an", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1",
    ]
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    writing = target.with_name(target.stem + ".writing" + target.suffix)
    if writing.exists():
        writing.unlink()
    preset = "medium" if args.quality == "beautiful" else "veryfast"
    crf = "18" if args.quality == "beautiful" else "21"
    encode = [
        args.ffmpeg, "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgr24",
        "-s", f"{width}x{height}", "-r", f"{fps:.8f}", "-i", "pipe:0", "-an",
        "-c:v", "libx264", "-preset", preset, "-crf", crf, "-pix_fmt", "yuv420p",
        "-movflags", "+faststart", str(writing),
    ]
    decoder = subprocess.Popen(decode, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    encoder = subprocess.Popen(encode, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    frame_size = width * height * 3
    history: collections.deque[np.ndarray] = collections.deque(maxlen=lookahead)
    future: collections.deque[np.ndarray] = collections.deque()

    def read_frame() -> Optional[np.ndarray]:
        raw = read_exact(decoder.stdout, frame_size)
        if raw is None:
            return None
        return np.frombuffer(raw, np.uint8).reshape(height, width, 3).copy()

    try:
        for _ in range(lookahead + 1):
            frame = read_frame()
            if frame is None:
                break
            future.append(frame)
        decoded_index = 0
        written = 0
        while future:
            current = future.popleft()
            neighbors = list(history) + list(future)[:lookahead]
            if trim_start_frame <= decoded_index < trim_end_frame:
                result = restore_frame(model, current, neighbors, mask, args.quality)
                encoder.stdin.write(result.tobytes())
                written += 1
                if written == 1 or written % max(1, int(round(fps / 2))) == 0:
                    emit("progress", min(written / expected, 0.99), f"Đang xử lý frame {written}/{expected}…")
            history.append(current)
            next_frame = read_frame()
            if next_frame is not None:
                future.append(next_frame)
            decoded_index += 1
        encoder.stdin.close()
        decoder.wait()
        encoder.wait()
        if decoder.returncode != 0:
            raise RuntimeError(decoder.stderr.read().decode("utf-8", "replace"))
        if encoder.returncode != 0:
            raise RuntimeError(encoder.stderr.read().decode("utf-8", "replace"))
        os.replace(str(writing), str(target))
        if written == 0:
            raise RuntimeError("Segment không có frame lõi để ghi.")
        emit("completed", 1, "Đã xử lý segment video.", outputPath=str(target), width=width, height=height, framesPerSecond=fps, durationSeconds=trim_duration)
    finally:
        for process in (decoder, encoder):
            try:
                if process.poll() is None:
                    process.kill()
            except Exception:
                pass
        if writing.exists() and not target.exists():
            try:
                writing.unlink()
            except OSError:
                pass


def sample_video_frames(path: str, samples: int) -> list[np.ndarray]:
    capture = cv2.VideoCapture(path)
    if not capture.isOpened():
        raise RuntimeError("Không mở được video để đề xuất mask.")
    count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    if count <= 0:
        count = samples
    indices = np.linspace(0, max(0, count - 1), samples).astype(int)
    frames = []
    for index in indices:
        capture.set(cv2.CAP_PROP_POS_FRAMES, int(index))
        ok, frame = capture.read()
        if ok and frame is not None:
            frames.append(frame)
    capture.release()
    if len(frames) < 3:
        raise RuntimeError("Video không đủ frame để đề xuất vùng cố định.")
    return frames


def propose_static_mask(frames: list[np.ndarray]) -> np.ndarray:
    base_h, base_w = frames[0].shape[:2]
    resized = [cv2.resize(frame, (base_w, base_h), interpolation=cv2.INTER_AREA) for frame in frames]
    grays = np.stack([cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY).astype(np.float32) for frame in resized])
    edges = np.stack([cv2.Canny(gray.astype(np.uint8), 70, 150) > 0 for gray in grays]).astype(np.float32)
    persistence = edges.mean(axis=0)
    variance = grays.var(axis=0)
    candidate = ((persistence > 0.72) & (variance < 45)).astype(np.uint8) * 255
    candidate = cv2.morphologyEx(candidate, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8), iterations=2)
    candidate = cv2.dilate(candidate, np.ones((7, 7), np.uint8), iterations=1)

    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(candidate, connectivity=8)
    output = np.zeros_like(candidate)
    frame_area = base_w * base_h
    scored = []
    for label in range(1, component_count):
        x, y, width, height, area = stats[label]
        if area < max(30, frame_area * 0.00002) or area > frame_area * 0.12:
            continue
        region = labels == label
        score = float(persistence[region].mean()) * math.log1p(area)
        scored.append((score, label))
    for _, label in sorted(scored, reverse=True)[:6]:
        output[labels == label] = 255
    return output


def cmd_suggest_video(args) -> None:
    emit("progress", 0.05, "Đang lấy mẫu nhiều frame…")
    frames = sample_video_frames(args.input, 12)
    mask = propose_static_mask(frames)
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(target), mask)
    emit(
        "completed", 1,
        "Đã tạo mask đề xuất. Hãy kiểm tra và xác nhận trước khi xử lý.",
        outputPath=str(target), maskPath=str(target), width=mask.shape[1], height=mask.shape[0],
    )


def cmd_selftest(args) -> None:
    model = LamaModel(args.runtime, args.device)
    image = np.zeros((128, 128, 3), np.uint8)
    image[:] = (38, 72, 110)
    image[:, 64:] = (140, 170, 205)
    mask = np.zeros((128, 128), np.float32)
    mask[40:88, 40:88] = 1
    result = model.predict(image, mask, 256)
    outside = mask == 0
    if not np.array_equal(result[outside], image[outside]):
        raise RuntimeError("Pixel ngoài mask bị thay đổi trong LaMa self-test.")
    emit("completed", 1, f"Original LaMa self-test đạt trên {model.device.type.upper()}.")


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(description="ERASA VIDEO original LaMa bridge")
    sub = root.add_subparsers(dest="command", required=True)

    image = sub.add_parser("inpaint-image")
    image.add_argument("--input", required=True)
    image.add_argument("--mask", required=True)
    image.add_argument("--output", required=True)
    image.add_argument("--runtime", required=True)
    image.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto")
    image.add_argument("--quality", choices=["fast", "beautiful"], default="beautiful")
    image.set_defaults(func=cmd_inpaint_image)

    video = sub.add_parser("process-video-segment")
    video.add_argument("--input", required=True)
    video.add_argument("--mask", required=True)
    video.add_argument("--output", required=True)
    video.add_argument("--runtime", required=True)
    video.add_argument("--ffmpeg", required=True)
    video.add_argument("--ffprobe", required=True)
    video.add_argument("--start", type=float, default=0)
    video.add_argument("--duration", type=float, required=True)
    video.add_argument("--trim-start", type=float, default=0)
    video.add_argument("--trim-duration", type=float)
    video.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto")
    video.add_argument("--quality", choices=["fast", "beautiful"], default="beautiful")
    video.set_defaults(func=cmd_process_video_segment)

    suggest = sub.add_parser("suggest-video")
    suggest.add_argument("--input", required=True)
    suggest.add_argument("--output", required=True)
    suggest.add_argument("--ffmpeg", required=True)
    suggest.add_argument("--ffprobe", required=True)
    suggest.set_defaults(func=cmd_suggest_video)

    selftest = sub.add_parser("selftest")
    selftest.add_argument("--runtime", required=True)
    selftest.add_argument("--device", choices=["auto", "cuda", "cpu"], default="cpu")
    selftest.set_defaults(func=cmd_selftest)
    return root


def main() -> int:
    args = parser().parse_args()
    try:
        args.func(args)
        return 0
    except KeyboardInterrupt:
        emit("failed", None, "Tác vụ đã bị hủy.", error="Tác vụ đã bị hủy.")
        return 130
    except Exception as error:
        emit("failed", None, str(error), error=str(error))
        traceback.print_exc(file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
