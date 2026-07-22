from __future__ import annotations
import argparse, atexit, hashlib, json, os, shutil, signal, subprocess, sys, tempfile
from pathlib import Path
import cv2
import numpy as np
import torch

ACTIVE: list[subprocess.Popen] = []

def emit(kind: str, progress: float | None = None, message: str | None = None, output: str | None = None):
    print(json.dumps({"kind": kind, "progress": progress, "message": message, "output": output}, ensure_ascii=False), flush=True)

def app_root() -> Path:
    return Path(__file__).resolve().parent.parent

def ffmpeg_exe(name: str) -> str:
    bundled = app_root() / "tools" / "ffmpeg" / "bin" / f"{name}.exe"
    return str(bundled if bundled.exists() else name)

def model_path() -> Path:
    return app_root() / "models" / "big-lama.pt"

def choose_device(requested: str) -> torch.device:
    if requested == "cuda":
        if not torch.cuda.is_available(): raise RuntimeError("Không tìm thấy NVIDIA CUDA. Hãy chọn Tự động hoặc CPU.")
        return torch.device("cuda")
    if requested == "auto" and torch.cuda.is_available(): return torch.device("cuda")
    return torch.device("cpu")

def load_model(device: torch.device):
    path = model_path()
    if not path.exists(): raise FileNotFoundError(f"Thiếu model: {path}")
    emit("log", 0, f"Đang nạp LaMa trên {device.type.upper()}…")
    return torch.jit.load(str(path), map_location=device).eval()

def read_mask(path: str, width: int, height: int) -> np.ndarray:
    mask = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if mask is None: raise RuntimeError("Không đọc được mask.")
    if mask.shape[1] != width or mask.shape[0] != height:
        mask = cv2.resize(mask, (width, height), interpolation=cv2.INTER_NEAREST)
    return (mask > 8).astype(np.float32)

def pad8(image: np.ndarray, mask: np.ndarray):
    h, w = image.shape[:2]
    ph, pw = (-h) % 8, (-w) % 8
    image_p = cv2.copyMakeBorder(image, 0, ph, 0, pw, cv2.BORDER_REFLECT_101)
    mask_p = cv2.copyMakeBorder(mask, 0, ph, 0, pw, cv2.BORDER_CONSTANT, value=0)
    return image_p, mask_p, h, w

@torch.inference_mode()
def inpaint_frame(model, device: torch.device, bgr: np.ndarray, mask: np.ndarray) -> np.ndarray:
    image_p, mask_p, h, w = pad8(bgr, mask)
    rgb = cv2.cvtColor(image_p, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    image_t = torch.from_numpy(rgb.transpose(2,0,1)).unsqueeze(0).to(device)
    mask_t = torch.from_numpy(mask_p).unsqueeze(0).unsqueeze(0).to(device)
    predicted = model(image_t, mask_t)
    if isinstance(predicted, (tuple, list)): predicted = predicted[0]
    out = predicted[0].permute(1,2,0).detach().float().cpu().numpy()
    out = np.clip(out[:h,:w] * 255.0, 0, 255).astype(np.uint8)
    out = cv2.cvtColor(out, cv2.COLOR_RGB2BGR)
    m = mask[:h,:w,None]
    return np.clip(bgr[:h,:w] * (1.0 - m) + out * m, 0, 255).astype(np.uint8)

def cmd_preview_frame(args):
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    command = [ffmpeg_exe("ffmpeg"), "-y", "-ss", "0", "-i", args.input, "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", args.output]
    subprocess.run(command, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    emit("completed", 1, "Đã lấy frame xem trước.", args.output)

def cmd_image(args):
    image = cv2.imread(args.input, cv2.IMREAD_COLOR)
    if image is None: raise RuntimeError("Không đọc được ảnh.")
    mask = read_mask(args.mask, image.shape[1], image.shape[0])
    device = choose_device(args.device); model = load_model(device)
    emit("progress", .15, "Đang xử lý ảnh…")
    result = inpaint_frame(model, device, image, mask)
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    target = Path(args.output)
    temp = str(target.with_name(target.stem + ".partial" + target.suffix))
    if not cv2.imwrite(temp, result): raise RuntimeError("Không ghi được ảnh kết quả.")
    os.replace(temp, args.output)
    emit("completed", 1, "Đã xử lý ảnh.", args.output)

def probe_video(path: str):
    command = [ffmpeg_exe("ffprobe"), "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate,nb_frames,duration", "-of", "json", path]
    data = json.loads(subprocess.check_output(command, text=True, encoding="utf-8"))
    stream = data["streams"][0]
    num, den = (stream.get("r_frame_rate") or "30/1").split("/")
    fps = float(num) / max(float(den), 1)
    duration = float(stream.get("duration") or 0)
    frames = int(stream.get("nb_frames") or round(duration * fps) or 1)
    return int(stream["width"]), int(stream["height"]), fps, duration, frames

def read_exact(pipe, size: int):
    chunks=[]; remaining=size
    while remaining:
        block=pipe.read(remaining)
        if not block: break
        chunks.append(block); remaining-=len(block)
    data=b"".join(chunks)
    return data if len(data)==size else None

def stop_children():
    for p in ACTIVE:
        try:
            if p.poll() is None: p.kill()
        except Exception: pass

atexit.register(stop_children)
signal.signal(signal.SIGTERM, lambda *_: (stop_children(), sys.exit(130)))

def cmd_video(args):
    width, height, fps, full_duration, total_frames = probe_video(args.input)
    duration = min(float(args.duration), full_duration) if args.duration else full_duration
    expected = max(1, round(duration * fps)) if duration > 0 else total_frames
    mask = read_mask(args.mask, width, height)
    device = choose_device(args.device); model = load_model(device)
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    partial = str(Path(args.output).with_suffix(".partial.mp4"))
    decode = [ffmpeg_exe("ffmpeg"), "-v", "error"]
    if args.start: decode += ["-ss", str(args.start)]
    decode += ["-i", args.input]
    if args.duration: decode += ["-t", str(args.duration)]
    decode += ["-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1"]
    encode = [ffmpeg_exe("ffmpeg"), "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{width}x{height}", "-r", f"{fps:.8f}", "-i", "pipe:0"]
    if args.start: encode += ["-ss", str(args.start)]
    if args.duration: encode += ["-t", str(args.duration)]
    encode += ["-i", args.input, "-map", "0:v:0", "-map", "1:a?", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-shortest", partial]
    dec = subprocess.Popen(decode, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    enc = subprocess.Popen(encode, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    ACTIVE[:] = [dec, enc]
    frame_bytes = width * height * 3; index=0
    try:
        while True:
            raw = read_exact(dec.stdout, frame_bytes)
            if raw is None: break
            frame = np.frombuffer(raw, np.uint8).reshape((height,width,3))
            result = inpaint_frame(model, device, frame, mask)
            enc.stdin.write(result.tobytes())
            index += 1
            if index == 1 or index % max(1, round(fps/2)) == 0:
                emit("progress", min(index / expected, .99), f"Đang xử lý frame {index}/{expected}…")
        enc.stdin.close(); dec.wait(); enc.wait()
        if dec.returncode != 0: raise RuntimeError(dec.stderr.read().decode("utf-8", "replace"))
        if enc.returncode != 0: raise RuntimeError(enc.stderr.read().decode("utf-8", "replace"))
        os.replace(partial, args.output)
        emit("completed", 1, "Đã xử lý video.", args.output)
    finally:
        stop_children(); ACTIVE.clear()
        if os.path.exists(partial) and not os.path.exists(args.output):
            try: os.remove(partial)
            except OSError: pass

def cmd_selftest(args):
    device = choose_device(args.device); model = load_model(device)
    image = np.zeros((64,64,3), np.uint8); image[:]=(32,64,96); image[:,32:]=(120,150,180)
    mask = np.zeros((64,64), np.float32); mask[20:44,20:44]=1
    out = inpaint_frame(model, device, image, mask)
    outside = mask == 0
    if not np.array_equal(out[outside], image[outside]): raise RuntimeError("Pixel ngoài mask đã bị thay đổi.")
    emit("completed", 1, f"LaMa self-test đạt trên {device.type}.")

def parser():
    p=argparse.ArgumentParser(); sub=p.add_subparsers(dest="command", required=True)
    q=sub.add_parser("preview-frame"); q.add_argument("--input",required=True); q.add_argument("--output",required=True); q.set_defaults(func=cmd_preview_frame)
    for name, func in [("image",cmd_image),("video",cmd_video)]:
        q=sub.add_parser(name); q.add_argument("--input",required=True); q.add_argument("--mask",required=True); q.add_argument("--output",required=True); q.add_argument("--device",choices=["auto","cuda","cpu"],default="auto")
        if name=="video": q.add_argument("--start",type=float,default=0); q.add_argument("--duration",type=float)
        q.set_defaults(func=func)
    q=sub.add_parser("selftest"); q.add_argument("--device",choices=["auto","cuda","cpu"],default="cpu"); q.set_defaults(func=cmd_selftest)
    return p

def main():
    args=parser().parse_args()
    try: args.func(args)
    except Exception as ex:
        emit("failed", None, str(ex)); print(str(ex), file=sys.stderr); return 1
    return 0

if __name__=="__main__": raise SystemExit(main())
