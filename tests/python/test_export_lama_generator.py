from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "export_lama_generator.py"
spec = importlib.util.spec_from_file_location("export_lama_generator", SCRIPT)
assert spec and spec.loader
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)

try:
    import torch
except ImportError:
    torch = None


class UnsafeCheckpointMetadata:
    def __init__(self, label: str):
        self.label = label


class ExportGeneratorStaticTests(unittest.TestCase):
    def test_exporter_uses_weights_only_and_safetensors(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("weights_only=True", source)
        self.assertIn("get_unsafe_globals_in_checkpoint", source)
        self.assertIn("from safetensors.torch import load_file, save_file", source)
        self.assertNotIn("pytorch_lightning", source)

    @unittest.skipUnless(
        torch is not None and hasattr(torch.serialization, "get_unsafe_globals_in_checkpoint"),
        "requires modern torch weights_only support",
    )
    def test_checkpoint_loader_isolates_unsafe_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            checkpoint = Path(directory) / "checkpoint.ckpt"
            payload = {
                "state_dict": {
                    **{f"generator.layer{i}": torch.ones(1) * i for i in range(120)},
                },
                "metadata": UnsafeCheckpointMetadata("training-only"),
            }
            torch.save(payload, checkpoint)
            loaded, unsafe = exporter.load_checkpoint_weights(checkpoint)
            state = exporter.extract_generator_state(loaded)
            self.assertEqual(120, len(state))
            self.assertTrue(any("UnsafeCheckpointMetadata" in name for name in unsafe))

    @unittest.skipUnless(torch is not None, "torch is installed only in the exporter CI job")
    def test_extract_generator_state_removes_prefix(self) -> None:
        payload = {
            "state_dict": {
                **{f"generator.layer{i}": torch.ones(1) * i for i in range(120)},
                "discriminator.ignore": torch.ones(1),
            }
        }
        state = exporter.extract_generator_state(payload)
        self.assertEqual(120, len(state))
        self.assertIn("layer0", state)
        self.assertNotIn("generator.layer0", state)


if __name__ == "__main__":
    unittest.main()
