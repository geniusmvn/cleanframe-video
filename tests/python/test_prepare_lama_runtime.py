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

    def test_runtime_complete_requires_all_core_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary)
            required = [
                runtime / "python" / "python.exe",
                runtime / "lama-source" / "saicinpainting" / "training" / "modules" / "ffc.py",
                runtime / "model" / "config.yaml",
                runtime / "model" / "models" / "best.ckpt",
                runtime / "runtime.ready.json",
            ]
            for path in required:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"x")
            self.assertTrue(runtime_builder.runtime_is_complete(runtime))
            required[-1].unlink()
            self.assertFalse(runtime_builder.runtime_is_complete(runtime))

    def test_manifest_is_pinned_to_original_lama(self) -> None:
        manifest = json.loads(
            (ROOT / "src" / "Erasa.Video2.Worker.Core" / "Runtime" / "runtime-manifest.json").read_text(encoding="utf-8")
        )
        self.assertIn("advimman/lama/archive/786f5936b27fb3dacd2b1ad799e4de968ea697e7.zip", manifest["lamaSource"]["url"])
        required = {
            "pytorch-lightning==1.2.9",
            "torchmetrics==0.2.0",
            "tensorboard==2.4.1",
            "protobuf==3.20.3",
        }
        self.assertTrue(required.issubset(set(manifest["lightningPackages"])))

    def test_runtime_import_probe_runs_before_model_download(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        probe = source.index("Runtime imports OK:")
        model = source.index('model_item = manifest["model"]')
        self.assertLess(probe, model)
        self.assertIn('*manifest["lightningPackages"]', source)


if __name__ == "__main__":
    unittest.main()
