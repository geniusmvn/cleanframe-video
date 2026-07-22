from __future__ import annotations
import argparse
import atexit
import hashlib
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
from pathlib import Path

os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")
os.environ.setdefault("NUMEXPR_NUM_THREADS", "1")

import cv2
import numpy as np

for stream in (sys.stdout, sys.stderr):
    reconfigure = getattr(stream, "reconfigure", None)
    if callable(reconfigure):
        reconfigure(encoding="utf-8", errors="backslashreplace")

ACTIVE: list[subprocess.Popen] = []

def emit(kind: str, progress=None, message=None, output=None, mask=None, mask_raw=None, width=None, height=None, duration=None, fps=None):
    print(json.dumps({
        "kind": kind, "progress": progress, "message": message, "output": output, "mask": mask,
        "maskRaw": mask_raw, "width": width, "height": height, "duration": duration, "fps": fps
    }, ensure_ascii=True), flush=True)


def app_root() -> Path:
    configured = os.environ.get("ERASA_APP_ROOT")
    return Path(configured).resolve() if configured else Path(__file__).resolve().parent.parent


def runtime_path(*parts: str) -> Path:
    return app_root().joinpath("runtime", *parts)


def ffmpeg(name: str) -> str:
    bundled = app_root() / "tools" / "ffmpeg" / "bin" / f"{name}.exe"
    return str(bundled if bundled.exists() else name)


def configure_original_lama() -> None:
    lama_root = runtime_path("lama")
    if not lama_root.exists():
        raise FileNotFoundError(f"Thiếu mã nguồn LaMa gốc: {lama_root}")
    sys.path.insert(0, str(lama_root))


class OriginalLamaEngine:
    def __init__(self, requested_device: str):
        configure_original_lama()
        import torch
        import yaml
        from omegaconf import OmegaConf
        from saicinpainting.training.trainers import load_checkpoint

        self.torch = torch
        if requested_device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("Không tìm thấy NVIDIA CUDA. Hãy chọn Tự động hoặc CPU.")
        self.device = torch.device("cuda" if requested_device in ("auto", "cuda") and torch.cuda.is_available() else "cpu")
        model_root = runtime_path("models", "big-lama")
        config_path = model_root / "config.yaml"
        checkpoint_path = model_root / "models" / "best.ckpt"
        if not config_path.exists() or not checkpoint_path.exists():
            raise FileNotFoundError(f"Thiếu model LaMa gốc trong {model_root}")
        emit("log", 0, f"Đang nạp LaMa gốc trên {self.device.type.upper()}…")
        with config_path.open("r", encoding="utf-8") as handle:
            train_config = OmegaConf.create(yaml.safe_load(handle))
        train_config.training_model.predict_only = True
        train_config.visualizer.kind = "noop"
        self.model = load_checkpoint(train_config, str(checkpoint_path), strict=False, map_location="cpu")
        self.model.freeze()
        self.model.to(self.device)

    def inpaint(self, bgr: np.ndarray, mask: np.ndarray) -> np.ndarray:
        torch = self.torch
        with torch.no_grad():
            height, width = bgr.shape[:2]
            pad_h, pad_w = (-height) % 8, (-width) % 8
            image = cv2.copyMakeBorder(bgr, 0, pad_h, 0, pad_w, cv2.BORDER_REFLECT_101)
            padded_mask = cv2.copyMakeBorder(mask, 0, pad_h, 0, pad_w, cv2.BORDER_CONSTANT, value=0)
            rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
            batch = {
                "image": torch.from_numpy(rgb.transpose(2, 0, 1)).unsqueeze(0).to(self.device),
                "mask": torch.from_numpy((padded_mask > 0).astype(np.float32)).unsqueeze(0).unsqueeze(0).to(self.device),
            }
            batch = self.model(batch)
            result = batch["inpainted"][0].permute(1, 2, 0).detach().float().cpu().numpy()
            result = np.clip(result[:height, :width] * 255.0, 0, 255).astype(np.uint8)
            result = cv2.cvtColor(result, cv2.COLOR_RGB2BGR)
            # Preserve every raw pixel outside the confirmed mask before encode.
            m = (mask[:height, :width] > 0)[:, :, None]
            return np.where(m, result, bgr[:height, :width]).astype(np.uint8)


def read_mask(path: str, width: int, height: int) -> np.ndarray:
    mask = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if mask is None:
        raise RuntimeError("Không đọc được mask.")
    if mask.shape[:2] != (height, width):
        mask = cv2.resize(mask, (width, height), interpolation=cv2.INTER_NEAREST)
    return (mask > 8).astype(np.uint8) * 255


def probe(path: str) -> dict:
    command = [ffmpeg("ffprobe"), "-v", "error", "-show_streams", "-show_format", "-of", "json", path]
    data = json.loads(subprocess.check_output(command, encoding="utf-8", errors="replace"))
    streams = data.get("streams", [])
    video = next((stream for stream in streams if stream.get("codec_type") == "video"), None)
    if not video:
        raise RuntimeError("Tệp không có luồng hình ảnh.")
    rate = video.get("avg_frame_rate") or video.get("r_frame_rate") or "0/1"
    num, den = rate.split("/")
    fps = float(num) / max(float(den), 1.0)
    duration = float(video.get("duration") or data.get("format", {}).get("duration") or 0)
    return {
        "width": int(video.get("width", 0)),
        "height": int(video.get("height", 0)),
        "fps": fps,
        "duration": duration,
        "has_audio": any(stream.get("codec_type") == "audio" for stream in streams),
    }


def verify_video_contract(source_metadata: dict, output_path: str | Path, expected_duration: float) -> dict:
    output_metadata = probe(str(output_path))
    if output_metadata["width"] != source_metadata["width"] or output_metadata["height"] != source_metadata["height"]:
        raise RuntimeError("Video kết quả không giữ nguyên độ phân giải.")
    fps_tolerance = max(0.02, source_metadata["fps"] * 0.001)
    if abs(output_metadata["fps"] - source_metadata["fps"]) > fps_tolerance:
        raise RuntimeError(f"Video kết quả sai FPS: {output_metadata['fps']:.6f} thay vì {source_metadata['fps']:.6f}.")
    duration_tolerance = max(0.20, 2.0 / max(source_metadata["fps"], 1.0))
    if abs(output_metadata["duration"] - expected_duration) > duration_tolerance:
        raise RuntimeError(
            f"Video kết quả sai thời lượng: {output_metadata['duration']:.3f}s thay vì {expected_duration:.3f}s."
        )
    if source_metadata.get("has_audio") and not output_metadata.get("has_audio"):
        raise RuntimeError("Video nguồn có audio nhưng video kết quả bị mất audio.")
    return output_metadata


def cmd_preview_frame(args):
    target = Path(args.output); target.parent.mkdir(parents=True, exist_ok=True)
    metadata = probe(args.input)
    command = [ffmpeg("ffmpeg"), "-y", "-v", "error", "-ss", str(max(args.time, 0)), "-i", args.input,
               "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", str(target)]
    subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    image = cv2.imread(str(target), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError("Không tạo được ảnh xem trước.")
    emit("metadata", 1, "Đã đọc tệp.", str(target), width=metadata["width"] or image.shape[1],
         height=metadata["height"] or image.shape[0], duration=metadata["duration"], fps=metadata["fps"])


def sample_video_frames(path: str, count: int = 12, max_width: int = 960) -> tuple[list[np.ndarray], dict]:
    metadata = probe(path)
    duration = max(metadata["duration"], 0.1)
    frames = []
    for index, time in enumerate(np.linspace(0, max(duration - 0.05, 0), count)):
        command = [ffmpeg("ffmpeg"), "-v", "error", "-ss", f"{time:.4f}", "-i", path, "-frames:v", "1",
                   "-vf", f"scale='min({max_width},iw)':-2", "-f", "image2pipe", "-vcodec", "png", "pipe:1"]
        encoded = subprocess.check_output(command)
        frame = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_COLOR)
        if frame is not None:
            frames.append(frame)
        emit("progress", (index + 1) / count * .7, f"Đang lấy mẫu frame {index + 1}/{count}…")
    if len(frames) < 4:
        raise RuntimeError("Không lấy đủ frame để đề xuất vùng tĩnh.")
    return frames, metadata


def propose_static_mask(frames: list[np.ndarray]) -> np.ndarray:
    size = frames[0].shape[:2]
    frames = [cv2.resize(frame, (size[1], size[0])) for frame in frames]
    grays = np.stack([cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY) for frame in frames]).astype(np.float32)
    median = np.median(grays, axis=0)
    mad = np.median(np.abs(grays - median), axis=0)
    edges = np.stack([cv2.Canny(gray.astype(np.uint8), 60, 150) > 0 for gray in grays])
    persistence = edges.mean(axis=0)
    stable_threshold = max(3.0, float(np.percentile(mad, 38)))
    candidate = ((persistence >= .58) & (mad <= stable_threshold)).astype(np.uint8) * 255
    kernel3 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    kernel7 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))
    candidate = cv2.morphologyEx(candidate, cv2.MORPH_CLOSE, kernel7, iterations=2)
    candidate = cv2.dilate(candidate, kernel3, iterations=2)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(candidate)
    image_area = candidate.shape[0] * candidate.shape[1]
    scored = []
    for label in range(1, count):
        x, y, w, h, area = stats[label]
        ratio = area / image_area
        if ratio < .00008 or ratio > .12 or w < 4 or h < 4:
            continue
        component = labels == label
        score = float(persistence[component].mean()) * (1.0 - min(float(mad[component].mean()) / 30.0, .9))
        # Prefer compact overlays but do not restrict the search to the four corners.
        fill = area / max(w * h, 1)
        score *= .65 + .35 * fill
        scored.append((score, label))
    if not scored:
        raise RuntimeError("Không tìm thấy vùng tĩnh đủ tin cậy. Hãy chọn vùng thủ công.")
    scored.sort(reverse=True)
    output = np.zeros_like(candidate)
    for _, label in scored[:3]:
        output[labels == label] = 255
    output = cv2.dilate(output, kernel3, iterations=1)
    return output


def cmd_suggest_video(args):
    frames, metadata = sample_video_frames(args.input)
    small = propose_static_mask(frames)
    mask = cv2.resize(small, (metadata["width"], metadata["height"]), interpolation=cv2.INTER_NEAREST)
    target = Path(args.output); target.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(target), mask):
        raise RuntimeError("Không ghi được mask đề xuất.")
    raw_path = target.with_suffix(".raw")
    raw_path.write_bytes(mask.tobytes())
    overlay_path = target.with_name(target.stem + "_overlay.png")
    overlay = np.zeros((mask.shape[0], mask.shape[1], 4), np.uint8)
    overlay[:, :, 2] = 255
    overlay[:, :, 1] = 80
    overlay[:, :, 3] = (mask.astype(np.float32) * 0.52).astype(np.uint8)
    if not cv2.imwrite(str(overlay_path), overlay):
        raise RuntimeError("Không ghi được lớp xem trước mask.")
    emit("suggestion", 1, "Đã tạo đề xuất. Người dùng phải kiểm tra mask.", output=str(overlay_path),
         mask=str(target), mask_raw=str(raw_path), width=metadata["width"], height=metadata["height"],
         duration=metadata["duration"], fps=metadata["fps"])


def cmd_image(args):
    image = cv2.imread(args.input, cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError("Không đọc được ảnh.")
    mask = read_mask(args.mask, image.shape[1], image.shape[0])
    engine = OriginalLamaEngine(args.device)
    emit("progress", .2, "Đang xử lý ảnh bằng LaMa gốc…")
    result = engine.inpaint(image, mask)
    target = Path(args.output); target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.stem + ".partial" + target.suffix)
    if not cv2.imwrite(str(partial), result):
        raise RuntimeError("Không ghi được ảnh kết quả.")
    os.replace(partial, target)
    written = cv2.imread(str(target), cv2.IMREAD_COLOR)
    if written is None or written.shape[:2] != image.shape[:2]:
        try: target.unlink()
        except OSError: pass
        raise RuntimeError("Ảnh kết quả không giữ nguyên kích thước.")
    emit("completed", 1, "Đã xử lý ảnh.", str(target), width=image.shape[1], height=image.shape[0])


def read_exact(pipe, size: int):
    chunks = []; remaining = size
    while remaining:
        block = pipe.read(remaining)
        if not block:
            break
        chunks.append(block); remaining -= len(block)
    data = b"".join(chunks)
    return data if len(data) == size else None


def stop_children():
    for process in ACTIVE:
        try:
            if process.poll() is None:
                process.kill()
        except Exception:
            pass


atexit.register(stop_children)
if hasattr(signal, "SIGTERM"):
    signal.signal(signal.SIGTERM, lambda *_: (stop_children(), sys.exit(130)))


def sha256_file(path: str | Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def resume_signature(args, metadata: dict) -> str:
    source = Path(args.input)
    payload = {
        "input": str(source.resolve()),
        "input_size": source.stat().st_size,
        "input_mtime_ns": source.stat().st_mtime_ns,
        "mask_sha256": sha256_file(args.mask),
        "width": metadata["width"],
        "height": metadata["height"],
        "fps": round(metadata["fps"], 10),
        "start": round(float(args.start), 6),
        "duration": None if args.duration is None else round(float(args.duration), 6),
        "crf": int(args.crf),
        "segment_seconds": round(float(args.segment_seconds), 4),
    }
    encoded = json.dumps(payload, sort_keys=True, ensure_ascii=True).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def write_state(path: Path, data: dict) -> None:
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, indent=2, ensure_ascii=True), encoding="utf-8")
    os.replace(temporary, path)


def prepare_resume_directory(args, metadata: dict, total_segments: int) -> tuple[Path, dict]:
    state_dir = Path(args.state_dir) if args.state_dir else Path(tempfile.mkdtemp(prefix="erasa-video-"))
    signature = resume_signature(args, metadata)
    state_path = state_dir / "state.json"
    if state_path.exists():
        try:
            existing = json.loads(state_path.read_text(encoding="utf-8"))
        except Exception:
            existing = {}
        if existing.get("signature") != signature:
            shutil.rmtree(state_dir, ignore_errors=True)
    state_dir.mkdir(parents=True, exist_ok=True)
    for partial in state_dir.glob("*.partial.mp4"):
        try: partial.unlink()
        except OSError: pass
    state = {
        "signature": signature,
        "input": str(Path(args.input).resolve()),
        "total_segments": total_segments,
        "completed_segments": sorted(int(path.stem.split("_")[-1]) for path in state_dir.glob("segment_*.mp4") if path.stat().st_size > 0),
    }
    write_state(state_path, state)
    return state_dir, state


def process_video_segment(engine: OriginalLamaEngine, args, mask: np.ndarray, width: int, height: int,
                          fps: float, segment_index: int, total_segments: int, segment_start: float,
                          segment_duration: float, state_dir: Path) -> Path:
    segment = state_dir / f"segment_{segment_index:06d}.mp4"
    if segment.exists() and segment.stat().st_size > 0:
        emit("progress", (segment_index + 1) / total_segments, f"Đã khôi phục đoạn {segment_index + 1}/{total_segments} từ lần trước.")
        return segment
    partial = state_dir / f"segment_{segment_index:06d}.partial.mp4"
    if partial.exists(): partial.unlink()
    absolute_start = float(args.start) + segment_start
    decode = [ffmpeg("ffmpeg"), "-v", "error", "-ss", f"{absolute_start:.6f}", "-i", args.input,
              "-t", f"{segment_duration:.6f}", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"]
    key_interval = max(1, round(fps * max(float(args.segment_seconds), 1.0)))
    encode = [ffmpeg("ffmpeg"), "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgr24",
              "-s", f"{width}x{height}", "-r", f"{fps:.10f}", "-i", "pipe:0", "-an",
              "-c:v", "libx264", "-preset", "medium", "-crf", str(args.crf),
              "-g", str(key_interval), "-keyint_min", str(key_interval), "-sc_threshold", "0",
              "-pix_fmt", "yuv420p", str(partial)]
    decoder = subprocess.Popen(decode, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    encoder = subprocess.Popen(encode, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    ACTIVE[:] = [decoder, encoder]
    frame_bytes = width * height * 3
    expected_frames = max(1, round(segment_duration * fps))
    frame_index = 0
    try:
        while True:
            raw = read_exact(decoder.stdout, frame_bytes)
            if raw is None: break
            frame = np.frombuffer(raw, np.uint8).reshape((height, width, 3))
            result = engine.inpaint(frame, mask)
            encoder.stdin.write(result.tobytes())
            frame_index += 1
            if frame_index == 1 or frame_index % max(1, round(fps / 2)) == 0:
                segment_progress = min(frame_index / expected_frames, .99)
                overall = (segment_index + segment_progress) / total_segments
                emit("progress", overall, f"Đoạn {segment_index + 1}/{total_segments} • frame {frame_index}/{expected_frames}")
        encoder.stdin.close()
        decoder.wait()
        encoder.wait()
        if decoder.returncode != 0:
            raise RuntimeError(decoder.stderr.read().decode("utf-8", "replace"))
        if encoder.returncode != 0:
            raise RuntimeError(encoder.stderr.read().decode("utf-8", "replace"))
        if frame_index == 0:
            raise RuntimeError(f"Không giải mã được frame cho đoạn {segment_index + 1}.")
        os.replace(partial, segment)
        return segment
    finally:
        stop_children()
        ACTIVE.clear()
        if partial.exists():
            try: partial.unlink()
            except OSError: pass


def concat_video_segments(args, segments: list[Path], duration: float, target: Path, state_dir: Path) -> None:
    concat_path = state_dir / "concat.txt"
    concat_path.write_text("".join(f"file '{str(path.resolve()).replace(chr(39), chr(39) * 2)}'\n" for path in segments), encoding="utf-8")
    partial = target.with_name(target.stem + ".partial.mp4")
    if partial.exists(): partial.unlink()
    command = [ffmpeg("ffmpeg"), "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", str(concat_path)]
    if args.start > 0: command += ["-ss", str(args.start)]
    command += ["-i", args.input, "-t", f"{duration:.6f}", "-map", "0:v:0", "-map", "1:a?",
                "-map_metadata", "1", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart", str(partial)]
    try:
        subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        os.replace(partial, target)
    finally:
        if partial.exists():
            try: partial.unlink()
            except OSError: pass


def cmd_video(args):
    metadata = probe(args.input)
    width, height, fps = metadata["width"], metadata["height"], metadata["fps"]
    if width <= 0 or height <= 0 or fps <= 0:
        raise RuntimeError("Metadata video không hợp lệ.")
    full_duration = metadata["duration"]
    available = max(full_duration - float(args.start), 0)
    duration = min(float(args.duration), available) if args.duration else available
    if duration <= 0:
        raise RuntimeError("Khoảng video cần xử lý có thời lượng bằng 0.")
    segment_seconds = max(.5, float(args.segment_seconds))
    total_segments = max(1, int(np.ceil(duration / segment_seconds)))
    mask = read_mask(args.mask, width, height)
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    state_dir, state = prepare_resume_directory(args, metadata, total_segments)
    state_path = state_dir / "state.json"
    engine = OriginalLamaEngine(args.device)
    segments = []
    try:
        for segment_index in range(total_segments):
            segment_start = segment_index * segment_seconds
            segment_duration = min(segment_seconds, duration - segment_start)
            segment = process_video_segment(engine, args, mask, width, height, fps, segment_index,
                                            total_segments, segment_start, segment_duration, state_dir)
            segments.append(segment)
            completed = sorted(set(state.get("completed_segments", [])) | {segment_index})
            state["completed_segments"] = completed
            write_state(state_path, state)
            emit("checkpoint", (segment_index + 1) / total_segments,
                 f"Đã lưu điểm tiếp tục {segment_index + 1}/{total_segments}.")
        concat_video_segments(args, segments, duration, target, state_dir)
        try:
            verified = verify_video_contract(metadata, target, duration)
        except Exception:
            try: target.unlink()
            except OSError: pass
            raise
        emit(
            "completed", 1, "Đã xử lý video, giữ thông số và ghép audio.", str(target),
            width=verified["width"], height=verified["height"],
            duration=verified["duration"], fps=verified["fps"]
        )
        shutil.rmtree(state_dir, ignore_errors=True)
    except Exception:
        # Keep completed segments so Pause/Retry/Resume can continue instead of restarting the whole video.
        raise


def cmd_selftest(args):
    engine = OriginalLamaEngine(args.device)
    image = np.zeros((128, 128, 3), np.uint8)
    image[:] = (30, 60, 90); image[:, 64:] = (130, 160, 190)
    mask = np.zeros((128, 128), np.uint8); mask[40:88, 40:88] = 255
    output = engine.inpaint(image, mask)
    outside = mask == 0
    if not np.array_equal(output[outside], image[outside]):
        raise RuntimeError("Self-test thất bại: pixel ngoài mask bị thay đổi.")
    if output.shape != image.shape:
        raise RuntimeError("Self-test thất bại: sai kích thước output.")
    emit("completed", 1, f"LaMa gốc self-test đạt trên {engine.device.type.upper()}.")


def parser():
    root = argparse.ArgumentParser()
    sub = root.add_subparsers(dest="command", required=True)
    p = sub.add_parser("preview-frame"); p.add_argument("--input", required=True); p.add_argument("--output", required=True); p.add_argument("--time", type=float, default=0); p.set_defaults(func=cmd_preview_frame)
    p = sub.add_parser("suggest-video"); p.add_argument("--input", required=True); p.add_argument("--output", required=True); p.set_defaults(func=cmd_suggest_video)
    p = sub.add_parser("image"); p.add_argument("--input", required=True); p.add_argument("--mask", required=True); p.add_argument("--output", required=True); p.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto"); p.set_defaults(func=cmd_image)
    p = sub.add_parser("video"); p.add_argument("--input", required=True); p.add_argument("--mask", required=True); p.add_argument("--output", required=True); p.add_argument("--device", choices=["auto", "cuda", "cpu"], default="auto"); p.add_argument("--start", type=float, default=0); p.add_argument("--duration", type=float); p.add_argument("--crf", type=int, default=18); p.add_argument("--state-dir"); p.add_argument("--segment-seconds", type=float, default=2.0); p.set_defaults(func=cmd_video)
    p = sub.add_parser("selftest"); p.add_argument("--device", choices=["auto", "cuda", "cpu"], default="cpu"); p.set_defaults(func=cmd_selftest)
    return root


def main() -> int:
    args = parser().parse_args()
    try:
        args.func(args); return 0
    except KeyboardInterrupt:
        emit("cancelled", None, "Đã hủy."); return 130
    except Exception as exc:
        emit("failed", None, str(exc)); print(str(exc), file=sys.stderr); return 1


if __name__ == "__main__":
    raise SystemExit(main())
