from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import time
import types
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

CHUNK_SIZE = 1024 * 1024
USER_AGENT = "ERASA-VIDEO-2-generator-exporter/1.3"
UPSTREAM_COMMIT = "786f5936b27fb3dacd2b1ad799e4de968ea697e7"


def log(message: str) -> None:
    print(message, flush=True)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(CHUNK_SIZE), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_file(path: Path, expected_sha256: str | None = None) -> bool:
    if not path.is_file() or path.stat().st_size <= 0:
        return False
    return not expected_sha256 or sha256_file(path).lower() == expected_sha256.lower()


def download(url: str, target: Path, expected_sha256: str | None = None, retries: int = 4) -> Path:
    target.parent.mkdir(parents=True, exist_ok=True)
    if verify_file(target, expected_sha256):
        log(f"Dùng tệp đã có: {target.name}")
        return target
    partial = target.with_suffix(target.suffix + ".partial")
    partial.unlink(missing_ok=True)
    last_error: Exception | None = None
    for attempt in range(1, retries + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=180) as response, partial.open("wb") as output:
                total_header = response.headers.get("Content-Length")
                total = int(total_header) if total_header and total_header.isdigit() else 0
                received = 0
                reported = -10
                while True:
                    block = response.read(CHUNK_SIZE)
                    if not block:
                        break
                    output.write(block)
                    received += len(block)
                    if total:
                        percent = min(100, round(received * 100 / total))
                        if percent == 100 or percent // 10 > reported // 10:
                            log(f"Tải {target.name}: {percent}%")
                            reported = percent
            if not verify_file(partial, expected_sha256):
                raise RuntimeError(f"Checksum không khớp: {target.name}")
            os.replace(partial, target)
            return target
        except (OSError, urllib.error.URLError, RuntimeError) as error:
            last_error = error
            partial.unlink(missing_ok=True)
            if attempt < retries:
                delay = attempt * 4
                log(f"Tải {target.name} lỗi lần {attempt}: {error}; thử lại sau {delay}s")
                time.sleep(delay)
    raise RuntimeError(f"Không tải được {target.name}: {last_error}")


def safe_extract_zip(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    root = destination.resolve()
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.infolist():
            candidate = (destination / member.filename).resolve()
            if candidate != root and root not in candidate.parents:
                raise RuntimeError(f"ZIP chứa đường dẫn không an toàn: {member.filename}")
        bundle.extractall(destination)


def extract_single_root_archive(archive: Path, destination: Path) -> None:
    shutil.rmtree(destination, ignore_errors=True)
    temporary = destination.with_name(destination.name + ".extract")
    shutil.rmtree(temporary, ignore_errors=True)
    temporary.mkdir(parents=True)
    try:
        safe_extract_zip(archive, temporary)
        children = list(temporary.iterdir())
        root = children[0] if len(children) == 1 and children[0].is_dir() else temporary
        shutil.copytree(root, destination, dirs_exist_ok=True)
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


def extract_original_model(archive: Path, destination: Path) -> tuple[Path, Path]:
    shutil.rmtree(destination, ignore_errors=True)
    destination.mkdir(parents=True)
    temporary = destination / "extract"
    safe_extract_zip(archive, temporary)
    configs = list(temporary.rglob("config.yaml"))
    checkpoints = list(temporary.rglob("best.ckpt"))
    if not configs or not checkpoints:
        raise RuntimeError("Archive Big-LaMa không có config.yaml và models/best.ckpt")
    config = destination / "config.yaml"
    checkpoint = destination / "best.ckpt"
    shutil.copy2(configs[0], config)
    shutil.copy2(checkpoints[0], checkpoint)
    shutil.rmtree(temporary, ignore_errors=True)
    return config, checkpoint


class _IgnoredCheckpointGlobal:
    def __new__(cls, *args, **kwargs):
        return super().__new__(cls)

    def __init__(self, *args, **kwargs):
        pass

    def __setstate__(self, state):
        self.__dict__["_ignored_state"] = state


def _safe_global_entries(torch_module, numpy_module, checkpoint: Path):
    unsafe = sorted(torch_module.serialization.get_unsafe_globals_in_checkpoint(checkpoint))
    known = {
        "numpy.core.multiarray.scalar": numpy_module.core.multiarray.scalar,
        "numpy._core.multiarray.scalar": numpy_module.core.multiarray.scalar,
        "numpy.dtype": numpy_module.dtype,
    }
    entries = []
    for index, full_name in enumerate(unsafe):
        if full_name in known:
            entries.append((known[full_name], full_name))
            continue
        placeholder = type(f"IgnoredCheckpointGlobal{index}", (_IgnoredCheckpointGlobal,), {})
        entries.append((placeholder, full_name))
    # NumPy dtypes may be constructed dynamically and not appear in the static scan.
    entries.extend([
        type(numpy_module.dtype(numpy_module.float32)),
        type(numpy_module.dtype(numpy_module.float64)),
    ])
    return unsafe, entries


def load_checkpoint_weights(checkpoint: Path):
    import numpy as np
    import torch

    try:
        return torch.load(checkpoint, map_location="cpu", weights_only=True), []
    except Exception as first_error:
        unsafe, entries = _safe_global_entries(torch, np, checkpoint)
        log("Checkpoint chứa metadata ngoài tensor; bỏ qua metadata và chỉ đọc weights.")
        if unsafe:
            log("Unsafe globals bị cô lập: " + ", ".join(unsafe))
        try:
            with torch.serialization.safe_globals(entries):
                payload = torch.load(checkpoint, map_location="cpu", weights_only=True)
            return payload, unsafe
        except Exception as second_error:
            raise RuntimeError(
                "Không thể đọc checkpoint bằng weights_only. "
                f"Lỗi đầu: {first_error}; lỗi sau allowlist cô lập: {second_error}"
            ) from second_error


def extract_generator_state(payload) -> dict[str, object]:
    import torch

    if not isinstance(payload, dict):
        raise RuntimeError(f"Checkpoint phải là dict, nhận {type(payload)!r}")
    state = payload.get("state_dict", payload)
    if not isinstance(state, dict):
        raise RuntimeError("Checkpoint không có state_dict dạng dict")
    generator = {
        key[len("generator."):]: value.detach().cpu().contiguous()
        for key, value in state.items()
        if isinstance(key, str) and key.startswith("generator.") and torch.is_tensor(value)
    }
    if not generator:
        # Some mirrors already contain a plain generator state dict.
        generator = {
            key: value.detach().cpu().contiguous()
            for key, value in state.items()
            if isinstance(key, str) and torch.is_tensor(value)
        }
    if len(generator) < 100:
        raise RuntimeError(f"Generator state quá nhỏ: chỉ có {len(generator)} tensor")
    return generator


def _get_shape(value):
    import torch
    if torch.is_tensor(value):
        return tuple(value.shape)
    if isinstance(value, dict):
        return {name: _get_shape(item) for name, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_get_shape(item) for item in value]
    if isinstance(value, (int, float)):
        return type(value)
    raise ValueError(f"unexpected type {type(value)}")


def import_original_generator(source: Path):
    source_text = str(source.resolve())
    if source_text not in sys.path:
        sys.path.insert(0, source_text)
    compatibility = types.ModuleType("saicinpainting.utils")
    compatibility.get_shape = _get_shape
    sys.modules["saicinpainting.utils"] = compatibility
    from saicinpainting.training.modules.ffc import FFCResNetGenerator  # type: ignore
    module_file = Path(sys.modules[FFCResNetGenerator.__module__].__file__).resolve()
    expected = (source / "saicinpainting" / "training" / "modules" / "ffc.py").resolve()
    if module_file != expected:
        raise RuntimeError(f"Import sai source LaMa: {module_file}")
    return FFCResNetGenerator


def lookup_path(root: dict, path: str):
    value = root
    for part in path.split("."):
        value = value[part]
    return value


def resolve_config(value, root: dict):
    if isinstance(value, dict):
        return {key: resolve_config(item, root) for key, item in value.items()}
    if isinstance(value, list):
        return [resolve_config(item, root) for item in value]
    if isinstance(value, str) and value.startswith("${") and value.endswith("}"):
        return resolve_config(lookup_path(root, value[2:-1]), root)
    return value


def validate_and_canonicalize(source: Path, config_path: Path, state: dict[str, object]):
    import torch
    import yaml

    generator_class = import_original_generator(source)
    config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    generator_config = resolve_config(config["generator"], config)
    generator_config.pop("kind", None)
    generator = generator_class(**generator_config)
    missing, unexpected = generator.load_state_dict(state, strict=False)
    if missing or unexpected:
        raise RuntimeError(
            f"State không khớp FFCResNetGenerator gốc: missing={missing[:12]}, unexpected={unexpected[:12]}"
        )
    generator.eval()
    with torch.no_grad():
        sample = torch.zeros((1, 4, 64, 64), dtype=torch.float32)
        sample[:, :3] = 0.25
        sample[:, 3:, 20:44, 20:44] = 1
        output = generator(sample)
    if tuple(output.shape) != (1, 3, 64, 64) or not torch.isfinite(output).all():
        raise RuntimeError(f"Forward test của generator gốc không đạt: shape={tuple(output.shape)}")
    return {name: tensor.detach().cpu().contiguous() for name, tensor in generator.state_dict().items()}


def export_generator(manifest_path: Path, output: Path, workspace: Path) -> None:
    from safetensors.torch import load_file, save_file

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    source_item = manifest["lamaSource"]
    model_item = manifest["originalModel"]
    downloads = workspace / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)

    source_zip = download(source_item["url"], downloads / "lama-source.zip", source_item.get("sha256"))
    source = workspace / "lama-source"
    extract_single_root_archive(source_zip, source)
    ffc = source / "saicinpainting" / "training" / "modules" / "ffc.py"
    if not ffc.is_file() or "class FFCResNetGenerator" not in ffc.read_text(encoding="utf-8"):
        raise RuntimeError("Source không đúng advimman/lama")

    model_zip = download(model_item["url"], downloads / "big-lama.zip", model_item.get("sha256"))
    config, checkpoint = extract_original_model(model_zip, workspace / "original-model")
    payload, ignored_globals = load_checkpoint_weights(checkpoint)
    extracted = extract_generator_state(payload)
    canonical = validate_and_canonicalize(source, config, extracted)

    output.mkdir(parents=True, exist_ok=True)
    state_path = output / "generator.safetensors"
    metadata = {
        "format": "ERASA original LaMa generator state",
        "upstream": "advimman/lama",
        "upstream_commit": UPSTREAM_COMMIT,
        "source_model_archive_sha256": sha256_file(model_zip),
        "source_checkpoint_sha256": sha256_file(checkpoint),
        "tensor_count": str(len(canonical)),
    }
    save_file(canonical, state_path, metadata=metadata)
    reloaded = load_file(state_path, device="cpu")
    if set(reloaded) != set(canonical):
        raise RuntimeError("Safetensors reload không giữ nguyên danh sách tensor")
    shutil.copy2(config, output / "config.yaml")
    export_metadata = {
        **metadata,
        "generator_sha256": sha256_file(state_path),
        "config_sha256": sha256_file(output / "config.yaml"),
        "ignored_checkpoint_globals": ignored_globals,
        "validated_forward_shape": [1, 3, 64, 64],
    }
    (output / "export-metadata.json").write_text(
        json.dumps(export_metadata, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    log(f"Đã xuất {len(canonical)} tensor sang {state_path.name} và kiểm tra forward bằng source LaMa gốc.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Export the original Big-LaMa generator to a plain safetensors state.")
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--workspace", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    workspace = args.workspace or Path(tempfile.mkdtemp(prefix="erasa-lama-export-"))
    workspace.mkdir(parents=True, exist_ok=True)
    export_generator(args.manifest.resolve(), args.output.resolve(), workspace.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
