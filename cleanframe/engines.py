
from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Optional, Tuple

import cv2
import numpy as np
import onnxruntime as ort


MODEL_SHA256 = "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6"


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


class LamaEngine:
    """LaMa ONNX wrapper.

    Contract:
      image: RGB float32 [1,3,512,512] normalized /255
      mask:  float32 [1,1,512,512], 1 = erase
      output: RGB float32 [1,3,512,512] in 0..255
    """

    def __init__(self, model_path: Path):
        if not model_path.exists():
            raise FileNotFoundError(f"Không thấy model: {model_path}")
        digest = sha256_file(model_path)
        if digest.lower() != MODEL_SHA256:
            raise RuntimeError(
                "Model LaMa không đúng checksum. "
                f"Expected {MODEL_SHA256}, got {digest}"
            )
        providers = ["CPUExecutionProvider"]
        self.session = ort.InferenceSession(str(model_path), providers=providers)
        inputs = {i.name: i for i in self.session.get_inputs()}
        self.image_name = "image" if "image" in inputs else self.session.get_inputs()[0].name
        self.mask_name = "mask" if "mask" in inputs else self.session.get_inputs()[1].name
        self.output_name = self.session.get_outputs()[0].name

    def inpaint(self, rgb: np.ndarray, mask: np.ndarray) -> np.ndarray:
        if rgb.shape[:2] != mask.shape[:2]:
            raise ValueError("Ảnh và mask phải cùng kích thước")
        original_h, original_w = rgb.shape[:2]
        image_512 = cv2.resize(rgb, (512, 512), interpolation=cv2.INTER_AREA)
        mask_512 = cv2.resize(mask, (512, 512), interpolation=cv2.INTER_NEAREST)
        image_tensor = image_512.astype(np.float32).transpose(2, 0, 1)[None] / 255.0
        mask_tensor = (mask_512.astype(np.float32) / 255.0)[None, None]
        out = self.session.run(
            [self.output_name],
            {self.image_name: image_tensor, self.mask_name: mask_tensor},
        )[0]
        out = np.clip(out[0].transpose(1, 2, 0), 0, 255).astype(np.uint8)
        return cv2.resize(out, (original_w, original_h), interpolation=cv2.INTER_CUBIC)


def relative_rect_to_pixels(
    rect: Tuple[float, float, float, float], width: int, height: int
) -> Tuple[int, int, int, int]:
    x, y, w, h = rect
    x1 = int(round(max(0.0, min(1.0, x)) * width))
    y1 = int(round(max(0.0, min(1.0, y)) * height))
    x2 = int(round(max(0.0, min(1.0, x + w)) * width))
    y2 = int(round(max(0.0, min(1.0, y + h)) * height))
    x1, x2 = sorted((max(0, min(width - 1, x1)), max(1, min(width, x2))))
    y1, y2 = sorted((max(0, min(height - 1, y1)), max(1, min(height, y2))))
    return x1, y1, max(1, x2 - x1), max(1, y2 - y1)


def refine_mask_from_crop(
    crop_bgr: np.ndarray,
    *,
    expand_px: int = 2,
    feather_px: int = 2,
) -> np.ndarray:
    """Generic tight mask for bright/dark semi-transparent overlays.

    This is intentionally brand-agnostic. It combines local contrast and edges,
    then keeps components near the crop center.
    """
    gray = cv2.cvtColor(crop_bgr, cv2.COLOR_BGR2GRAY)
    blur = cv2.GaussianBlur(gray, (0, 0), 5)
    contrast = cv2.absdiff(gray, blur)
    edges = cv2.Canny(gray, 40, 120)
    score = cv2.max(contrast, edges)
    _, binary = cv2.threshold(score, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    binary = cv2.morphologyEx(
        binary, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8), iterations=1
    )
    binary = cv2.dilate(binary, np.ones((3, 3), np.uint8), iterations=1)

    n, labels, stats, centers = cv2.connectedComponentsWithStats(binary, 8)
    result = np.zeros_like(binary)
    h, w = binary.shape
    cx, cy = w / 2, h / 2
    max_dist = (w * w + h * h) ** 0.5 * 0.48
    for i in range(1, n):
        x, y, cw, ch, area = stats[i]
        px, py = centers[i]
        dist = ((px - cx) ** 2 + (py - cy) ** 2) ** 0.5
        if 3 <= area <= 0.65 * w * h and dist <= max_dist:
            result[labels == i] = 255

    if not np.any(result):
        result[:] = 255

    if expand_px > 0:
        k = max(1, expand_px * 2 + 1)
        result = cv2.dilate(result, np.ones((k, k), np.uint8), iterations=1)
    if feather_px > 0:
        k = feather_px * 2 + 1
        result = cv2.GaussianBlur(result, (k, k), 0)
    return result


def inpaint_frame_crop(
    frame_bgr: np.ndarray,
    rect: Tuple[float, float, float, float],
    engine: LamaEngine,
    *,
    padding_ratio: float = 1.8,
    expand_px: int = 2,
    feather_px: int = 2,
    previous_patch: Optional[np.ndarray] = None,
) -> tuple[np.ndarray, np.ndarray]:
    h, w = frame_bgr.shape[:2]
    x, y, rw, rh = relative_rect_to_pixels(rect, w, h)
    pad_x = max(12, int(rw * padding_ratio))
    pad_y = max(12, int(rh * padding_ratio))
    x1, y1 = max(0, x - pad_x), max(0, y - pad_y)
    x2, y2 = min(w, x + rw + pad_x), min(h, y + rh + pad_y)

    crop = frame_bgr[y1:y2, x1:x2].copy()
    local_x, local_y = x - x1, y - y1
    target_crop = crop[local_y:local_y + rh, local_x:local_x + rw]
    tight = refine_mask_from_crop(
        target_crop, expand_px=expand_px, feather_px=feather_px
    )
    mask = np.zeros(crop.shape[:2], np.uint8)
    mask[local_y:local_y + rh, local_x:local_x + rw] = tight

    rgb = cv2.cvtColor(crop, cv2.COLOR_BGR2RGB)
    filled_rgb = engine.inpaint(rgb, mask)
    filled = cv2.cvtColor(filled_rgb, cv2.COLOR_RGB2BGR)

    # Mild temporal smoothing only when the generated patch is similar.
    if previous_patch is not None and previous_patch.shape == filled.shape:
        delta = float(cv2.absdiff(previous_patch, filled).mean())
        if delta < 28.0:
            filled = cv2.addWeighted(filled, 0.78, previous_patch, 0.22, 0)

    alpha = (mask.astype(np.float32) / 255.0)[..., None]
    blended = (crop * (1.0 - alpha) + filled * alpha).astype(np.uint8)
    result = frame_bgr.copy()
    result[y1:y2, x1:x2] = blended
    return result, filled
