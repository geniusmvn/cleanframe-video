from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "prepare_lama_runtime.py"
spec = importlib.util.spec_from_file_location("prepare_lama_runtime", SCRIPT)
assert spec and spec.loader
runtime_builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime_builder)


class PrepareRuntimeTests(unittest.TestCase):
    def test_safe_extract_rejects_parent_escape(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "bad.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("../escape.txt", "bad")
            with self.assertRaises(RuntimeError):
                runtime_builder.safe_extract_zip(archive, root / "out")

    def test_single_root_archive_is_flattened(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "source.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                bundle.writestr("lama-sha/LICENSE", "Apache")
                bundle.writestr("lama-sha/saicinpainting/training/modules/ffc.py", "class FFCResNetGenerator: pass")
            destination = root / "lama-source"
            runtime_builder.extract_single_root_archive(archive, destination)
            self.assertTrue((destination / "LICENSE").is_file())
            self.assertTrue((destination / "saicinpainting" / "training" / "modules" / "ffc.py").is_file())

    def test_runtime_complete_requires_safetensors_export(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            required = [
                runtime / "python" / "python.exe",
                runtime / "lama-source" / "saicinpainting" / "training" / "modules" / "ffc.py",
                runtime / "model" / "config.yaml",
                runtime / "model" / "generator.safetensors",
                runtime / "model" / "export-metadata.json",
                runtime / "runtime.ready.json",
            ]
            for path in required:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
            self.assertTrue(runtime_builder.runtime_is_complete(runtime))
            required[3].unlink()
            self.assertFalse(runtime_builder.runtime_is_complete(runtime))

    def test_manifest_has_no_lightning_runtime_dependencies(self) -> None:
        manifest = json.loads(
            (ROOT / "src" / "Erasa.Video2.Worker.Core" / "Runtime" / "runtime-manifest.json").read_text(encoding="utf-8")
        )
        self.assertEqual("1.3.0", manifest["version"])
        self.assertIn("advimman/lama/archive/786f5936b27fb3dacd2b1ad799e4de968ea697e7.zip", manifest["lamaSource"]["url"])
        serialized = json.dumps(manifest).lower()
        self.assertNotIn("pytorch-lightning", serialized)
        self.assertNotIn("torchmetrics", serialized)
        self.assertNotIn("tensorboard", serialized)
        self.assertIn("safetensors==0.4.5", manifest["basePackages"])

    def test_builder_consumes_export_instead_of_raw_checkpoint(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("validate_export(export)", source)
        self.assertIn("generator.safetensors", source)
        self.assertNotIn("best.ckpt", source)
        self.assertNotIn("pytorch_lightning", source)


if __name__ == "__main__":
    unittest.main()
