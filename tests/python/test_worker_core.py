import contextlib
import importlib.util
import io
import pathlib
import tempfile
import types
import unittest

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("erasa_worker", ROOT / "worker" / "erasa_worker.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FakeEngine:
    def inpaint(self, image, mask):
        changed = image.copy()
        changed[mask > 0] = (255, 0, 0)
        return np.where((mask > 0)[:, :, None], changed, image)


class WorkerCoreTests(unittest.TestCase):
    def test_static_detector_does_not_return_random_full_frame(self):
        frames = []
        for index in range(12):
            frame = np.zeros((160, 240, 3), np.uint8)
            frame[:, :] = (30 + index * 2, 40, 50)
            frame[18:35, 150:220] = (245, 245, 245)
            frames.append(frame)
        mask = MODULE.propose_static_mask(frames)
        self.assertGreater(np.count_nonzero(mask), 0)
        self.assertLess(np.count_nonzero(mask), mask.size * .2)

    def test_detector_rejects_frames_without_stable_overlay(self):
        rng = np.random.default_rng(42)
        frames = [rng.integers(0, 255, (120, 180, 3), dtype=np.uint8) for _ in range(12)]
        with self.assertRaises(RuntimeError):
            MODULE.propose_static_mask(frames)

    def test_composite_contract_keeps_outside_mask(self):
        image = np.full((32, 32, 3), 70, np.uint8)
        mask = np.zeros((32, 32), np.uint8)
        mask[8:24, 8:24] = 255
        output = FakeEngine().inpaint(image, mask)
        self.assertTrue(np.array_equal(output[mask == 0], image[mask == 0]))

    def test_resume_signature_is_stable_and_changes_with_mask(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "source.mp4"
            mask = root / "mask.png"
            source.write_bytes(b"fake-video")
            mask.write_bytes(b"mask-a")
            args = types.SimpleNamespace(
                input=str(source), mask=str(mask), start=0, duration=None,
                crf=18, segment_seconds=2.0,
            )
            metadata = {"width": 1280, "height": 720, "fps": 24.0}
            first = MODULE.resume_signature(args, metadata)
            second = MODULE.resume_signature(args, metadata)
            self.assertEqual(first, second)
            mask.write_bytes(b"mask-b")
            self.assertNotEqual(first, MODULE.resume_signature(args, metadata))

    def test_completed_segment_is_reused_for_resume(self):
        with tempfile.TemporaryDirectory() as directory:
            state = pathlib.Path(directory)
            segment = state / "segment_000000.mp4"
            segment.write_bytes(b"already-complete")
            args = types.SimpleNamespace(start=0, segment_seconds=2.0)
            with contextlib.redirect_stdout(io.StringIO()):
                reused = MODULE.process_video_segment(
                    object(), args, np.zeros((8, 8), np.uint8), 8, 8, 4.0,
                    0, 2, 0.0, 1.0, state,
                )
            self.assertEqual(segment, reused)
            self.assertEqual(b"already-complete", reused.read_bytes())

    def test_prepare_resume_directory_removes_partial_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            source = root / "source.mp4"
            mask = root / "mask.png"
            state = root / "state"
            source.write_bytes(b"fake-video")
            mask.write_bytes(b"mask")
            state.mkdir()
            (state / "segment_000000.partial.mp4").write_bytes(b"partial")
            args = types.SimpleNamespace(
                input=str(source), mask=str(mask), start=0, duration=None,
                crf=18, segment_seconds=2.0, state_dir=str(state),
            )
            metadata = {"width": 1280, "height": 720, "fps": 24.0}
            state_dir, resume_state = MODULE.prepare_resume_directory(args, metadata, 4)
            self.assertEqual(state, state_dir)
            self.assertFalse((state / "segment_000000.partial.mp4").exists())
            self.assertEqual(4, resume_state["total_segments"])
            self.assertTrue((state / "state.json").exists())


if __name__ == "__main__":
    unittest.main()
