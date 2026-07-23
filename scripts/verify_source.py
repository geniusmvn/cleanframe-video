from __future__ import annotations

import ast
import json
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

import yaml

ROOT = Path(__file__).resolve().parents[1]


def strip_csharp(text: str) -> str:
    text = re.sub(r'@?"(?:""|\\.|[^"\\])*"', '""', text, flags=re.S)
    text = re.sub(r"'(?:\\.|[^'\\])'", "''", text)
    text = re.sub(r'//.*', '', text)
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return text


def check_balanced(path: Path) -> None:
    source = strip_csharp(path.read_text(encoding='utf-8-sig'))
    pairs = {'{': '}', '(': ')', '[': ']'}
    stack: list[tuple[str, int]] = []
    for index, char in enumerate(source):
        if char in pairs:
            stack.append((char, index))
        elif char in pairs.values():
            if not stack or pairs[stack[-1][0]] != char:
                raise AssertionError(f'Unbalanced {char} in {path} at {index}')
            stack.pop()
    if stack:
        raise AssertionError(f'Unclosed {stack[-1][0]} in {path}')


def main() -> int:
    result: dict[str, object] = {}
    required = [
        'Erasa.Video2.sln',
        'src/Erasa.Video2.App/MainWindow.xaml',
        'src/Erasa.Video2.App/MainWindow.xaml.cs',
        'src/Erasa.Video2.App/Controls/MaskEditor.xaml.cs',
        'src/Erasa.Video2.Worker.Core/Services/WorkerCommandExecutor.cs',
        'src/Erasa.Video2.Worker.Core/Python/lama_bridge.py',
        'src/Erasa.Video2.Worker.Core/Runtime/runtime-manifest.json',
        'tests/Erasa.Video2.Tests/MaskRasterizerTests.cs',
        '.github/workflows/build-windows.yml',
    ]
    for relative in required:
        if not (ROOT / relative).is_file():
            raise AssertionError(f'Missing required file: {relative}')
    result['required_files'] = len(required)

    xml_files = sorted(list(ROOT.rglob('*.xaml')) + list(ROOT.rglob('*.csproj')) + [ROOT / 'Directory.Build.props', ROOT / 'Directory.Packages.props'])
    for path in xml_files:
        ElementTree.parse(path)
    result['xml_files'] = len(xml_files)

    with (ROOT / '.github/workflows/build-windows.yml').open(encoding='utf-8') as stream:
        workflow = yaml.safe_load(stream)
    if not isinstance(workflow, dict) or 'jobs' not in workflow:
        raise AssertionError('Workflow YAML is invalid.')
    result['workflow_yaml'] = True

    bridge_path = ROOT / 'src/Erasa.Video2.Worker.Core/Python/lama_bridge.py'
    ast.parse(bridge_path.read_text(encoding='utf-8'), filename=str(bridge_path))
    result['python_ast'] = True

    csharp_files = sorted(ROOT.rglob('*.cs'))
    for path in csharp_files:
        check_balanced(path)
    result['csharp_files'] = len(csharp_files)

    xaml = (ROOT / 'src/Erasa.Video2.App/MainWindow.xaml').read_text(encoding='utf-8')
    code = (ROOT / 'src/Erasa.Video2.App/MainWindow.xaml.cs').read_text(encoding='utf-8')
    handler_names = set(re.findall(r'\b(?:Click|SelectionChanged|ValueChanged|DragOver|Drop)="([A-Za-z_][A-Za-z0-9_]*)"', xaml))
    missing_handlers = [name for name in sorted(handler_names) if not re.search(rf'\b{name}\s*\(', code)]
    if missing_handlers:
        raise AssertionError(f'Missing XAML handlers: {missing_handlers}')
    result['xaml_handlers'] = len(handler_names)

    all_source = '\n'.join(path.read_text(encoding='utf-8-sig', errors='replace') for base in (ROOT / 'src', ROOT / 'tests') for path in base.rglob('*') if path.is_file() and path.suffix.lower() in {'.cs', '.py', '.xaml', '.json', '.props', '.csproj'})
    banned = ['PySide6', 'PyInstaller', 'Avalonia', 'onnxruntime', 'simple-lama', 'lama-cleaner', 'delogo=']
    found = [token for token in banned if token.lower() in all_source.lower()]
    if found:
        raise AssertionError(f'Legacy or banned tokens found: {found}')
    result['banned_tokens'] = 'absent'

    bridge = bridge_path.read_text(encoding='utf-8')
    required_bridge_tokens = [
        'from saicinpainting.training.modules.ffc import FFCResNetGenerator',
        'generator_state[key[len("generator."):]]',
        'torch.load(str(checkpoint_path)',
        'cv2.calcOpticalFlowFarneback',
        'source_known',
        'current.astype(np.float32) * (1 - alpha)',
    ]
    for token in required_bridge_tokens:
        if token not in bridge:
            raise AssertionError(f'Missing original LaMa/temporal implementation token: {token}')
    result['original_lama_wiring'] = True

    main_tokens = [
        'ConfirmMask_Click',
        'Preview_Click',
        'ProcessQueueAsync',
        'EnsureRuntimeAsync',
        'keepPreview: true',
        'JobStateMachine.CanPreview',
    ]
    for token in main_tokens:
        if token not in code:
            raise AssertionError(f'Missing UI workflow token: {token}')
    if 'Environment.Exit' in code or 'FailFast' in code:
        raise AssertionError('UI contains a process-killing path.')
    result['ui_state_wiring'] = True

    mask_editor = (ROOT / 'src/Erasa.Video2.App/Controls/MaskEditor.xaml.cs').read_text(encoding='utf-8')
    for tool in ['MaskTool.Brush', 'MaskTool.Eraser', 'MaskTool.Rectangle', 'MaskTool.Ellipse', 'MaskTool.Pan']:
        if tool not in code and tool not in mask_editor:
            raise AssertionError(f'Missing mask tool: {tool}')
    result['mask_tools'] = 5

    workflow_text = (ROOT / '.github/workflows/build-windows.yml').read_text(encoding='utf-8')
    for step in ['Build and test Any CPU', 'Worker FFmpeg utility self-test', 'WinUI startup smoke test', 'Original LaMa CPU self-test', 'Original LaMa video integration test']:
        if step not in workflow_text:
            raise AssertionError(f'Missing CI proof step: {step}')
    result['ci_proof_steps'] = 5

    manifest = json.loads((ROOT / 'src/Erasa.Video2.Worker.Core/Runtime/runtime-manifest.json').read_text(encoding='utf-8'))
    if 'advimman/lama/archive/786f5936b27fb3dacd2b1ad799e4de968ea697e7.zip' not in manifest['lamaSource']['url']:
        raise AssertionError('Original LaMa commit is not pinned.')
    result['pinned_upstream_commit'] = '786f5936b27fb3dacd2b1ad799e4de968ea697e7'


    # Architecture 1.1 guards: tests never reference the executable host and CI is layered.
    test_project = (ROOT / "tests" / "Erasa.Video2.Tests" / "Erasa.Video2.Tests.csproj").read_text(encoding="utf-8")
    assert "Erasa.Video2.Worker.Core" in test_project
    assert "Erasa.Video2.Worker.Host" not in test_project
    host_files = sorted(path.name for path in (ROOT / "src" / "Erasa.Video2.Worker.Host").glob("*.cs"))
    assert host_files == ["Program.cs"], host_files
    assert (ROOT / "src" / "Erasa.Video2.Worker.Core" / "Services" / "WorkerProcessHost.cs").exists()
    workflow = (ROOT / ".github" / "workflows" / "build-windows.yml").read_text(encoding="utf-8")
    for job in ("source-checks:", "core-tests:", "worker-windows:", "lama-cpu:", "winui-windows:"):
        assert job in workflow, job
    core_test_block = workflow.split("core-tests:", 1)[1].split("worker-windows:", 1)[0]
    assert "-p:Platform=x64" not in core_test_block
    assert "--no-build" not in core_test_block
    assert "Build complete solution" not in workflow
    assert "Console.Error.OutputEncoding" not in (ROOT / "src" / "Erasa.Video2.Worker.Host" / "Program.cs").read_text(encoding="utf-8")
    result["layered_ci_jobs"] = 5
    result["worker_host_is_thin"] = True

    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
