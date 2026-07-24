from __future__ import annotations

import ast
import json
import re
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
        'Erasa.Video2.Core.sln',
        'src/Erasa.Video2.App/MainWindow.xaml',
        'src/Erasa.Video2.App/MainWindow.xaml.cs',
        'src/Erasa.Video2.App/Controls/MaskEditor.xaml.cs',
        'src/Erasa.Video2.Worker.Core/Services/WorkerCommandExecutor.cs',
        'src/Erasa.Video2.Worker.Core/Python/lama_bridge.py',
        'src/Erasa.Video2.Worker.Core/Runtime/runtime-manifest.json',
        'scripts/export_lama_generator.py',
        'scripts/prepare_lama_runtime.py',
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

    workflow_path = ROOT / '.github/workflows/build-windows.yml'
    with workflow_path.open(encoding='utf-8') as stream:
        parsed_workflow = yaml.safe_load(stream)
    if not isinstance(parsed_workflow, dict) or 'jobs' not in parsed_workflow:
        raise AssertionError('Workflow YAML is invalid.')
    result['workflow_yaml'] = True

    python_files = [
        ROOT / 'scripts/export_lama_generator.py',
        ROOT / 'scripts/prepare_lama_runtime.py',
        ROOT / 'src/Erasa.Video2.Worker.Core/Python/lama_bridge.py',
    ]
    for path in python_files:
        ast.parse(path.read_text(encoding='utf-8'), filename=str(path))
    result['python_ast'] = len(python_files)

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

    all_source = '\n'.join(
        path.read_text(encoding='utf-8-sig', errors='replace')
        for base in (ROOT / 'src', ROOT / 'tests')
        for path in base.rglob('*')
        if path.is_file() and path.suffix.lower() in {'.cs', '.py', '.xaml', '.json', '.props', '.csproj'}
    )
    banned = ['PySide6', 'PyInstaller', 'Avalonia', 'onnxruntime', 'simple-lama', 'lama-cleaner', 'delogo=']
    found = [token for token in banned if token.lower() in all_source.lower()]
    if found:
        raise AssertionError(f'Legacy or banned tokens found: {found}')
    result['banned_tokens'] = 'absent'

    manifest = json.loads((ROOT / 'src/Erasa.Video2.Worker.Core/Runtime/runtime-manifest.json').read_text(encoding='utf-8'))
    if manifest['version'] != '1.3.0':
        raise AssertionError('Runtime manifest version is not 1.3.0.')
    if manifest['upstream']['commit'] != '786f5936b27fb3dacd2b1ad799e4de968ea697e7':
        raise AssertionError('Original LaMa commit is not pinned.')
    serialized_manifest = json.dumps(manifest).lower()
    for forbidden in ('pytorch-lightning', 'torchmetrics', 'tensorboard', 'future=='):
        if forbidden in serialized_manifest:
            raise AssertionError(f'Old checkpoint dependency leaked into Windows manifest: {forbidden}')
    if 'safetensors==0.4.5' not in manifest['basePackages']:
        raise AssertionError('safetensors is not pinned for Windows runtime.')
    result['plain_generator_manifest'] = True

    bridge = (ROOT / 'src/Erasa.Video2.Worker.Core/Python/lama_bridge.py').read_text(encoding='utf-8')
    for token in (
        'from saicinpainting.training.modules.ffc import FFCResNetGenerator',
        'from safetensors.torch import load_file',
        'generator.safetensors',
        'cv2.calcOpticalFlowFarneback',
        'source_known',
        'current.astype(np.float32) * (1 - alpha)',
    ):
        if token not in bridge:
            raise AssertionError(f'Missing original LaMa/temporal token: {token}')
    if 'torch.load(' in bridge or 'best.ckpt' in bridge or 'pytorch_lightning' in bridge:
        raise AssertionError('Windows bridge still loads the raw Lightning checkpoint.')
    result['original_source_plain_state_wiring'] = True

    exporter = (ROOT / 'scripts/export_lama_generator.py').read_text(encoding='utf-8')
    for token in ('weights_only=True', 'get_unsafe_globals_in_checkpoint', 'save_file(canonical', 'validate_and_canonicalize', 'validated_forward_shape'):
        if token not in exporter:
            raise AssertionError(f'Exporter proof missing: {token}')
    if 'pytorch_lightning' in exporter:
        raise AssertionError('Exporter must not import the old Lightning stack.')
    result['linux_checkpoint_exporter'] = True

    builder = (ROOT / 'scripts/prepare_lama_runtime.py').read_text(encoding='utf-8')
    for token in ('validate_export(export)', 'generator.safetensors', '--only-binary=:all:', 'selftest'):
        if token not in builder:
            raise AssertionError(f'Runtime builder missing: {token}')
    if 'best.ckpt' in builder or 'pytorch_lightning' in builder or 'originalModel' in builder:
        raise AssertionError('Windows runtime builder still handles the raw checkpoint.')
    result['windows_runtime_without_lightning'] = True

    workflow = workflow_path.read_text(encoding='utf-8')
    for job in ('source-checks:', 'core-tests:', 'worker-windows:', 'lama-export-linux:', 'lama-runtime-windows:', 'winui-windows:'):
        if job not in workflow:
            raise AssertionError(f'Missing layered CI job: {job}')
    for step in (
        'Build and test Any CPU',
        'Worker FFmpeg utility self-test',
        'Export and validate original generator',
        'Prepare Windows runtime from safetensors export',
        'Original LaMa Worker.Core self-test',
        'Original LaMa video integration test',
        'Bundled runtime status test',
        'WinUI startup smoke test',
    ):
        if step not in workflow:
            raise AssertionError(f'Missing CI proof step: {step}')
    if 'runtime-install' in workflow:
        raise AssertionError('CI still installs runtime through worker.')
    result['layered_ci_jobs'] = 6

    test_project = (ROOT / 'tests/Erasa.Video2.Tests/Erasa.Video2.Tests.csproj').read_text(encoding='utf-8')
    if 'Erasa.Video2.Worker.Core' not in test_project or 'Erasa.Video2.Worker.Host' in test_project:
        raise AssertionError('Tests must reference Worker.Core only.')
    host_files = sorted(path.name for path in (ROOT / 'src/Erasa.Video2.Worker.Host').glob('*.cs'))
    if host_files != ['Program.cs']:
        raise AssertionError(f'Worker host is not thin: {host_files}')
    result['worker_host_is_thin'] = True

    protocol = (ROOT / 'src/Erasa.Video2.Core/Protocol/WorkerProtocol.cs').read_text(encoding='utf-8')
    executor = (ROOT / 'src/Erasa.Video2.Worker.Core/Services/WorkerCommandExecutor.cs').read_text(encoding='utf-8')
    if 'RuntimeInstall' in protocol or 'WorkerCommands.RuntimeInstall' in executor or 'RuntimeInstallAsync' in executor or 'InstallAsync(' in executor:
        raise AssertionError('Runtime installation command still exists in shipped worker.')
    app_paths = (ROOT / 'src/Erasa.Video2.App/Services/AppPaths.cs').read_text(encoding='utf-8')
    if 'LocalRuntimeDirectory' in app_paths or 'generator.safetensors' not in app_paths:
        raise AssertionError('App may use a stale local runtime or does not require safetensors.')
    result['bundled_runtime_only'] = True

    for token in ('ConfirmMask_Click', 'Preview_Click', 'ProcessQueueAsync', 'EnsureRuntimeAsync', 'keepPreview: true', 'JobStateMachine.CanPreview'):
        if token not in code:
            raise AssertionError(f'Missing UI workflow token: {token}')
    if 'Environment.Exit' in code or 'FailFast' in code:
        raise AssertionError('UI contains a process-killing path.')
    result['ui_state_wiring'] = True

    mask_editor = (ROOT / 'src/Erasa.Video2.App/Controls/MaskEditor.xaml.cs').read_text(encoding='utf-8')
    for tool in ('MaskTool.Brush', 'MaskTool.Eraser', 'MaskTool.Rectangle', 'MaskTool.Ellipse', 'MaskTool.Pan'):
        if tool not in code and tool not in mask_editor:
            raise AssertionError(f'Missing mask tool: {tool}')
    result['mask_tools'] = 5

    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
