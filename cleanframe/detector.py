
from __future__ import annotations

from pathlib import Path
from typing import List, Tuple

import cv2
import numpy as np


Rect = Tuple[float, float, float, float]


def _sample_frames(path: Path, count: int = 20) -> list[np.ndarray]:
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        return []
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 1
    indices = np.linspace(0, max(0, total - 1), count).astype(int)
    frames = []
    for idx in indices:
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(idx))
        ok, frame = cap.read()
        if ok:
            frames.append(frame)
    cap.release()
    return frames


def suggest_overlay_rects(path: Path) -> List[Rect]:
    """Brand-agnostic fixed-overlay suggestions.

    Scores corner zones using temporal persistence and edge density.
    Returns relative rectangles; user confirmation is required.
    """
    frames = _sample_frames(path)
    if len(frames) < 3:
        return [(0.82, 0.82, 0.16, 0.14)]

    h, w = frames[0].shape[:2]
    zones = [
        (0.00, 0.00, 0.25, 0.22),
        (0.75, 0.00, 0.25, 0.22),
        (0.00, 0.78, 0.25, 0.22),
        (0.75, 0.78, 0.25, 0.22),
    ]
    scored = []
    for rect in zones:
        x, y, rw, rh = rect
        x1, y1 = int(x * w), int(y * h)
        x2, y2 = int((x + rw) * w), int((y + rh) * h)
        patches = []
        edge_scores = []
        for frame in frames:
            patch = frame[y1:y2, x1:x2]
            gray = cv2.cvtColor(patch, cv2.COLOR_BGR2GRAY)
            patches.append(gray.astype(np.float32))
            edge_scores.append(float(cv2.Canny(gray, 45, 130).mean()))
        stack = np.stack(patches)
        temporal_std = float(stack.std(axis=0).mean())
        edge = float(np.mean(edge_scores))
        # Fixed overlays tend to have persistent edges and lower temporal change.
        score = edge * 1.5 - temporal_std
        scored.append((score, rect))

    scored.sort(reverse=True, key=lambda item: item[0])
    result = []
    for _, (x, y, rw, rh) in scored[:3]:
        # Inner proposal rather than entire corner zone.
        result.append((x + rw * 0.28, y + rh * 0.30, rw * 0.62, rh * 0.55))
    return result
