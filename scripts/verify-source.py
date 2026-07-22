from __future__ import annotations

import ast
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]


def check_csharp_braces(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    text = re.sub(r'@?"(?:""|\\.|[^"\\])*"', '""', text)
    text = re.sub(r"'(?:\\.|[^'\\])'", "''", text)
    text = re.sub(r"//.*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    balance = 0
    for char in text:
        if char == "{":
            balance += 1
        elif char == "}":
            balance -= 1
        if balance < 0:
            raise AssertionError(f"Unbalanced C# braces in {path}")
    if balance != 0:
        raise AssertionError(f"Unbalanced C# braces in {path}: {balance}")


def main() -> int:
    result: dict[str, object] = {}

    python_files = sorted(ROOT.rglob("*.py"))
    for path in python_files:
        ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    result["python_ast_files"] = len(python_files)

    xml_files = sorted(ROOT.rglob("*.axaml")) + sorted(ROOT.rglob("*.csproj")) + sorted(ROOT.glob("*.props"))
    for path in xml_files:
        ET.parse(path)
    result["xml_files"] = len(xml_files)

    workflow_path = ROOT / ".github" / "workflows" / "build-windows.yml"
    workflow = yaml.safe_load(workflow_path.read_text(encoding="utf-8"))
    if not isinstance(workflow, dict) or "jobs" not in workflow:
        raise AssertionError("Invalid GitHub Actions workflow")
    result["workflow_yaml"] = True

    upstream = json.loads((ROOT / "upstream" / "UPSTREAM.json").read_text(encoding="utf-8"))
    expected = {
        "repository": "https://github.com/advimman/lama",
        "commit": "786f5936b27fb3dacd2b1ad799e4de968ea697e7",
        "model_sha256": "f1b358ca24093b93a106183b98a3dea6e8ed09f3b43ea7251eb2c81e7b4575f6",
    }
    for key, value in expected.items():
        if upstream.get(key) != value:
            raise AssertionError(f"Unexpected upstream {key}")
    result["upstream_pins"] = True

    worker = (ROOT / "worker" / "erasa_worker.py").read_text(encoding="utf-8")
    required_worker_tokens = [
        "from saicinpainting.training.trainers import load_checkpoint",
        'batch["inpainted"]',
        "np.where(m, result, bgr",
        "verify_video_contract",
        "prepare_resume_directory",
    ]
    for token in required_worker_tokens:
        if token not in worker:
            raise AssertionError(f"Missing worker contract: {token}")
    for token in ("onnxruntime", "simple_lama", "simple-lama", "lama_cleaner", "lama-cleaner", "torch.jit.load"):
        if token in worker:
            raise AssertionError(f"Converted or third-party LaMa runtime found: {token}")
    result["direct_original_lama"] = True

    app_source = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "src" / "Erasa.Video.App").rglob("*.cs"))
    handlers: list[str] = []
    for path in (ROOT / "src" / "Erasa.Video.App").rglob("*.axaml"):
        handlers.extend(
            re.findall(
                r'(?:^|\s)(?:Click|SelectionChanged|ValueChanged|Tapped|DoubleTapped|PointerPressed|PointerMoved|PointerReleased|Drop|DragOver)="([A-Za-z_]\w*)"',
                path.read_text(encoding="utf-8"),
                flags=re.M,
            )
        )
    missing_handlers = [name for name in handlers if not re.search(r"\b" + re.escape(name) + r"\s*\(", app_source)]
    if missing_handlers:
        raise AssertionError(f"Missing XAML event handlers: {missing_handlers}")
    result["xaml_event_handlers"] = len(handlers)

    csharp_files = sorted(ROOT.rglob("*.cs"))
    for path in csharp_files:
        check_csharp_braces(path)
        source = path.read_text(encoding="utf-8")
        if re.search(r"\?\s*255\s*\n\s*:\s*\(byte\)", source):
            raise AssertionError(f"Mixed int/byte conditional expression in {path}")
    result["csharp_brace_files"] = len(csharp_files)
    result["csharp_numeric_type_guard"] = True

    main_window = (ROOT / "src" / "Erasa.Video.App" / "MainWindow.axaml.cs").read_text(encoding="utf-8")
    for token in ("Editor.PanDelta", "Editor.SetZoom(_zoom)", 'AppLog.WriteAsync("ProcessItem"'):
        if token not in main_window:
            raise AssertionError(f"Missing UI resilience/tool contract: {token}")
    if "Environment.Exit" in main_window or "FailFast" in main_window:
        raise AssertionError("UI contains a process-killing failure path")
    result["ui_worker_error_isolation"] = True

    workflow_text = workflow_path.read_text(encoding="utf-8")
    for token in ("Original LaMa CPU self-test", "--duration 3", "Preview duration is", "Video integration test lost audio"):
        if token not in workflow_text:
            raise AssertionError(f"Missing CI contract: {token}")
    result["ci_original_lama_and_preview_contracts"] = True

    for name in ("erasa-brand.png", "erasa-icon.png", "erasa.ico", "erasa-splash.png"):
        path = ROOT / "src" / "Erasa.Video.App" / "Assets" / name
        if not path.exists() or path.stat().st_size == 0:
            raise AssertionError(f"Missing brand asset: {name}")
    result["brand_assets"] = True

    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
