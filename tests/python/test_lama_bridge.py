import importlib.util
import sys
import types
import unittest
from pathlib import Path

import cv2
import numpy as np

try:
    import torch  # noqa: F401
except ImportError:
    torch_stub = types.ModuleType("torch")
    torch_stub.is_tensor = lambda value: False
    torch_stub.no_grad = lambda: (lambda function: function)
    torch_stub.device = lambda value: value
    torch_stub.cuda = types.SimpleNamespace(is_available=lambda: False)
    sys.modules["torch"] = torch_stub

ROOT = Path(__file__).resolve().parents[2]
BRIDGE = ROOT / "src" / "Erasa.Video2.Worker.Core" / "Python" / "lama_bridge.py"
spec = importlib.util.spec_from_file_location("lama_bridge", BRIDGE)
bridge = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(bridge)


class FakeModel:
    def predict(self, image, mask, max_side):
        result = image.copy()
        result[mask > 0] = (200, 210, 220)
        return result


class DetectorTests(unittest.TestCase):
    def test_random_frames_do_not_create_random_mask(self):
        rng = np.random.default_rng(42)
        frames = [rng.integers(0, 256, (180, 320, 3), dtype=np.uint8) for _ in range(12)]
        mask = bridge.propose_static_mask(frames)
        self.assertLess(np.count_nonzero(mask), mask.size * 0.005)

    def test_fixed_overlay_is_proposed(self):
        rng = np.random.default_rng(8)
        frames = []
        for _ in range(12):
            frame = rng.integers(0, 120, (180, 320, 3), dtype=np.uint8)
            cv2.rectangle(frame, (238, 142), (307, 169), (245, 245, 245), 2)
            cv2.putText(frame, "FIX", (244, 162), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (250, 250, 250), 1, cv2.LINE_AA)
            frames.append(frame)
        mask = bridge.propose_static_mask(frames)
        self.assertGreater(np.count_nonzero(mask[135:175, 230:315]), 20)
        self.assertLess(np.count_nonzero(mask), mask.size * 0.12)


class TemporalTests(unittest.TestCase):
    def test_restore_frame_preserves_every_pixel_outside_mask(self):
        current = np.zeros((96, 128, 3), np.uint8)
        current[:] = (20, 40, 60)
        current[:, 64:] = (80, 100, 120)
        neighbor = np.roll(current, 1, axis=1)
        mask = np.zeros((96, 128), np.float32)
        mask[30:60, 40:85] = 1
        result = bridge.restore_frame(FakeModel(), current, [neighbor], mask, "fast")
        outside = mask == 0
        self.assertTrue(np.array_equal(result[outside], current[outside]))

    def test_temporal_confidence_shape_is_stable(self):
        frame = np.zeros((64, 96, 3), np.uint8)
        cv2.circle(frame, (40, 32), 12, (150, 160, 170), -1)
        neighbor = np.roll(frame, 2, axis=1)
        mask = np.zeros((64, 96), np.float32)
        mask[24:42, 32:52] = 1
        _, confidence = bridge.temporal_reconstruct(frame, [neighbor], mask, "beautiful")
        self.assertEqual(confidence.shape, mask.shape)
        self.assertTrue(np.isfinite(confidence).all())


if __name__ == "__main__":
    unittest.main()
