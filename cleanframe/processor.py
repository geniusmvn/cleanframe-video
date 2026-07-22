
from __future__ import annotations

import os
import subprocess
import threading
from pathlib import Path
from typing import Callable, Tuple

import cv2
import imageio_ffmpeg

from .engines import LamaEngine, inpaint_frame_crop


ProgressFn = Callable[[int, str], None]


class CancelledError(RuntimeError):
    pass


class VideoProcessor:
    def __init__(self, model_path: Path):
        self.engine = LamaEngine(model_path)
        self.cancel_event = threading.Event()

    def cancel(self) -> None:
        self.cancel_event.set()

    def process(
        self,
        input_path: Path,
        output_path: Path,
        rect: Tuple[float, float, float, float],
        progress: ProgressFn,
        *,
        crf: int = 18,
    ) -> None:
        self.cancel_event.clear()
        cap = cv2.VideoCapture(str(input_path))
        if not cap.isOpened():
            raise RuntimeError(f"Không mở được video: {input_path}")
        width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
        height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 1

        output_path.parent.mkdir(parents=True, exist_ok=True)
        temp_path = output_path.with_suffix(".processing.mp4")
        if temp_path.exists():
            temp_path.unlink()

        ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
        cmd = [
            ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgr24",
            "-s", f"{width}x{height}", "-r", f"{fps:.6f}", "-i", "-",
            "-i", str(input_path),
            "-map", "0:v:0", "-map", "1:a?",
            "-c:v", "libx264", "-preset", "medium", "-crf", str(crf),
            "-pix_fmt", "yuv420p", "-c:a", "copy",
            "-movflags", "+faststart", str(temp_path),
        ]
        proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
        previous_patch = None
        index = 0
        try:
            while True:
                if self.cancel_event.is_set():
                    raise CancelledError("Đã huỷ")
                ok, frame = cap.read()
                if not ok:
                    break
                result, previous_patch = inpaint_frame_crop(
                    frame, rect, self.engine, previous_patch=previous_patch
                )
                assert proc.stdin is not None
                proc.stdin.write(result.tobytes())
                index += 1
                pct = min(99, int(index * 100 / total))
                progress(pct, f"{input_path.name}: frame {index}/{total}")
        finally:
            cap.release()
            if proc.stdin:
                proc.stdin.close()
        code = proc.wait()
        if code != 0:
            if temp_path.exists():
                temp_path.unlink()
            raise RuntimeError(f"FFmpeg kết thúc với mã lỗi {code}")
        if self.cancel_event.is_set():
            if temp_path.exists():
                temp_path.unlink()
            raise CancelledError("Đã huỷ")
        os.replace(temp_path, output_path)
        progress(100, f"Hoàn thành: {output_path.name}")
