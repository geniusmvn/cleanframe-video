from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Iterable

CHUNK_SIZE = 1024 * 1024
USER_AGENT = "ERASA-VIDEO-2-runtime-builder/1.2"


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
    if not expected_sha256:
        return True
    return sha256_file(path).lower() == expected_sha256.lower()


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
            with urllib.request.urlopen(request, timeout=120) as response, partial.open("wb") as output:
                total_header = response.headers.get("Content-Length")
                total = int(total_header) if total_header and total_header.isdigit() else 0
                received = 0
                while True:
                    block = response.read(CHUNK_SIZE)
                    if not block:
                        break
                    output.write(block)
                    received += len(block)
                    if total:
                        percent = min(100, round(received * 100 / total))
                        log(f"Tải {target.name}: {percent}%")
            if not verify_file(partial, expected_sha256):
                raise RuntimeError(f"Checksum không khớp: {target.name}")
            os.replace(partial, target)
            return target
        except (OSError, urllib.error.URLError, RuntimeError) as error:
            last_error = error
            partial.unlink(missing_ok=True)
            if attempt < retries:
                delay = attempt * 3
                log(f"Tải {target.name} thất bại lần {attempt}: {error}. Thử lại sau {delay}s…")
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


def recreate_directory(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def copy_tree_contents(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    for item in source.iterdir():
        target = destination / item.name
        if item.is_dir():
            shutil.copytree(item, target, dirs_exist_ok=True)
        else:
            shutil.copy2(item, target)


def extract_single_root_archive(archive: Path, destination: Path) -> None:
    recreate_directory(destination)
    temporary = destination.with_name(destination.name + ".extract")
    recreate_directory(temporary)
    try:
        safe_extract_zip(archive, temporary)
        children = list(temporary.iterdir())
        root = children[0] if len(children) == 1 and children[0].is_dir() else temporary
        copy_tree_contents(root, destination)
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


def extract_model_archive(archive: Path, destination: Path) -> None:
    recreate_directory(destination)
    temporary = destination.with_name(destination.name + ".extract")
    recreate_directory(temporary)
    try:
        safe_extract_zip(archive, temporary)
        configs = list(temporary.rglob("config.yaml"))
        checkpoints = list(temporary.rglob("best.ckpt"))
        if not configs:
            raise RuntimeError("Model archive không có config.yaml")
        if not checkpoints:
            raise RuntimeError("Model archive không có models/best.ckpt")
        shutil.copy2(configs[0], destination / "config.yaml")
        (destination / "models").mkdir(parents=True, exist_ok=True)
        shutil.copy2(checkpoints[0], destination / "models" / "best.ckpt")
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


def enable_embedded_site(python_directory: Path) -> None:
    candidates = sorted(python_directory.glob("python*._pth"))
    if not candidates:
        raise RuntimeError("Python embedded thiếu file python*._pth")
    pth = candidates[0]
    lines = pth.read_text(encoding="utf-8-sig").splitlines()
    updated: list[str] = []
    site_present = False
    for line in lines:
        if line.strip() in {"#import site", "import site"}:
            updated.append("import site")
            site_present = True
        else:
            updated.append(line)
    if not site_present:
        updated.append("import site")
    pth.write_text("\n".join(updated) + "\n", encoding="utf-8", newline="\n")
    (python_directory / "Lib" / "site-packages").mkdir(parents=True, exist_ok=True)


def run(command: Iterable[str], cwd: Path | None = None) -> None:
    args = [str(part) for part in command]
    log("RUN: " + subprocess.list2cmdline(args))
    environment = os.environ.copy()
    environment["PYTHONUTF8"] = "1"
    environment["PYTHONIOENCODING"] = "utf-8"
    process = subprocess.Popen(
        args,
        cwd=str(cwd) if cwd else None,
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="backslashreplace",
    )
    assert process.stdout is not None
    for line in process.stdout:
        print(line.rstrip(), flush=True)
    code = process.wait()
    if code != 0:
        raise RuntimeError(f"Lệnh thất bại với mã {code}: {subprocess.list2cmdline(args)}")


def runtime_is_complete(runtime: Path) -> bool:
    required = [
        runtime / "python" / "python.exe",
        runtime / "lama-source" / "saicinpainting" / "training" / "modules" / "ffc.py",
        runtime / "model" / "config.yaml",
        runtime / "model" / "models" / "best.ckpt",
        runtime / "runtime.ready.json",
    ]
    return all(path.is_file() and path.stat().st_size > 0 for path in required)


def validate_original_source(source: Path) -> None:
    ffc = source / "saicinpainting" / "training" / "modules" / "ffc.py"
    license_file = source / "LICENSE"
    if not ffc.is_file() or "class FFCResNetGenerator" not in ffc.read_text(encoding="utf-8"):
        raise RuntimeError("Source tải về không đúng cấu trúc advimman/lama")
    if not license_file.is_file():
        raise RuntimeError("Source advimman/lama thiếu LICENSE")


def build_runtime(runtime: Path, manifest_path: Path, bridge: Path, profile: str, force: bool) -> None:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected_marker = {
        "version": manifest["version"],
        "profile": f"{profile}-bundled",
        "upstream": "advimman/lama",
        "commit": "786f5936b27fb3dacd2b1ad799e4de968ea697e7",
        "validated": True,
    }

    if not force and runtime_is_complete(runtime):
        try:
            marker = json.loads((runtime / "runtime.ready.json").read_text(encoding="utf-8-sig"))
            if all(marker.get(key) == value for key, value in expected_marker.items()):
                log("Runtime cache đã đầy đủ và đúng phiên bản.")
                return
        except (OSError, json.JSONDecodeError):
            pass

    runtime.mkdir(parents=True, exist_ok=True)
    downloads = runtime / "downloads"
    downloads.mkdir(parents=True, exist_ok=True)

    python_item = manifest["python"]
    python_zip = download(python_item["url"], downloads / "python.zip", python_item.get("sha256"))
    python_directory = runtime / "python"
    recreate_directory(python_directory)
    safe_extract_zip(python_zip, python_directory)
    enable_embedded_site(python_directory)
    python_exe = python_directory / "python.exe"
    if not python_exe.is_file():
        raise RuntimeError("Không tạo được Python embedded")

    pip_item = manifest["getPip"]
    get_pip = download(pip_item["url"], downloads / "get-pip.py", pip_item.get("sha256"))
    run([
        python_exe,
        "-X", "utf8",
        get_pip,
        "--no-warn-script-location",
        "pip==24.0",
        "setuptools==69.5.1",
        "wheel==0.43.0",
    ])

    torch_package = manifest["torchCuda"] if profile == "cuda" else manifest["torchCpu"]
    run([
        python_exe, "-X", "utf8", "-m", "pip", "install",
        "--no-cache-dir", "--disable-pip-version-check", "--no-warn-script-location",
        torch_package, "-f", manifest["torchFindLinks"],
    ])

    run([
        python_exe, "-X", "utf8", "-m", "pip", "install",
        "--no-cache-dir", "--disable-pip-version-check", "--no-warn-script-location",
        "--only-binary=:all:", *manifest["basePackages"],
    ])

    source_item = manifest["lamaSource"]
    source_zip = download(source_item["url"], downloads / "lama-source.zip", source_item.get("sha256"))
    source_directory = runtime / "lama-source"
    extract_single_root_archive(source_zip, source_directory)
    validate_original_source(source_directory)

    model_item = manifest["model"]
    model_zip = download(model_item["url"], downloads / "big-lama.zip", model_item.get("sha256"))
    extract_model_archive(model_zip, runtime / "model")

    # Validate the exact embedded runtime before writing the ready marker.
    run([
        python_exe, "-X", "utf8", "-I", bridge,
        "selftest", "--runtime", runtime, "--device", "cpu",
    ])

    marker = dict(expected_marker)
    marker["installedAt"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    (runtime / "runtime.ready.json").write_text(
        json.dumps(marker, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    # Downloads are not needed in the shipped artifact.
    shutil.rmtree(downloads, ignore_errors=True)
    if not runtime_is_complete(runtime):
        raise RuntimeError("Runtime chưa đầy đủ sau khi chuẩn bị")
    log("Runtime LaMa gốc đã được chuẩn bị và self-test thành công.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Prepare a tested, portable original-LaMa runtime for ERASA VIDEO 2.")
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--bridge", required=True, type=Path)
    parser.add_argument("--profile", choices=("cpu", "cuda"), default="cuda")
    parser.add_argument("--force", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    build_runtime(
        args.runtime.resolve(),
        args.manifest.resolve(),
        args.bridge.resolve(),
        args.profile,
        args.force,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
