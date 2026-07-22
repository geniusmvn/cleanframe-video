param([string]$Destination = "artifacts/runtime")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$destinationPath = Join-Path $root $Destination
New-Item -ItemType Directory -Force $destinationPath | Out-Null

$lamaCommit = "786f5936b27fb3dacd2b1ad799e4de968ea697e7"
$modelUrl = "https://huggingface.co/smartywu/big-lama/resolve/05cb2be7f8dbe6ca7c6e78f4fc827a4b2baaa4a9/big-lama.zip?download=true"
$modelSha = "f1b358ca24093b93a106183b98a3dea6e8ed09f3b43ea7251eb2c81e7b4575f6"

# Original upstream source, pinned to one immutable commit.
$lamaPath = Join-Path $destinationPath "lama"
if (Test-Path $lamaPath) { Remove-Item $lamaPath -Recurse -Force }
git init $lamaPath
git -C $lamaPath remote add origin https://github.com/advimman/lama.git
git -C $lamaPath fetch --depth 1 origin $lamaCommit
git -C $lamaPath checkout --detach FETCH_HEAD
if ((git -C $lamaPath rev-parse HEAD).Trim() -ne $lamaCommit) { throw "LaMa commit mismatch" }
Remove-Item (Join-Path $lamaPath ".git") -Recurse -Force

# Official Python embeddable runtime. No Python installation is required on the user's PC.
$pythonPath = Join-Path $destinationPath "python"
New-Item -ItemType Directory -Force $pythonPath | Out-Null
$pythonZip = Join-Path $env:RUNNER_TEMP "python-embed.zip"
Invoke-WebRequest "https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip" -OutFile $pythonZip
Expand-Archive $pythonZip -DestinationPath $pythonPath -Force
@("python38.zip", ".", "Lib\site-packages", "import site") | Set-Content (Join-Path $pythonPath "python38._pth") -Encoding ASCII
$getPip = Join-Path $env:RUNNER_TEMP "get-pip.py"
Invoke-WebRequest "https://bootstrap.pypa.io/pip/3.8/get-pip.py" -OutFile $getPip
& (Join-Path $pythonPath "python.exe") -X utf8 $getPip "pip==24.3.1" "setuptools==59.8.0" "wheel==0.38.4"
& (Join-Path $pythonPath "python.exe") -X utf8 -m pip install --no-warn-script-location torch==1.8.0+cpu torchvision==0.9.0+cpu -f https://download.pytorch.org/whl/torch_stable.html
& (Join-Path $pythonPath "python.exe") -X utf8 -m pip install --no-warn-script-location -r (Join-Path $root "worker/requirements-inference.txt")

# Original big-lama checkpoint package referenced by upstream README.
$modelZip = Join-Path $env:RUNNER_TEMP "big-lama.zip"
Invoke-WebRequest $modelUrl -OutFile $modelZip
$actual = (Get-FileHash $modelZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $modelSha) { throw "big-lama SHA-256 mismatch: $actual" }
$modelParent = Join-Path $destinationPath "models"
New-Item -ItemType Directory -Force $modelParent | Out-Null
Expand-Archive $modelZip -DestinationPath $modelParent -Force
if (-not (Test-Path (Join-Path $modelParent "big-lama/config.yaml"))) { throw "Invalid big-lama archive" }

# Retain upstream license and pin metadata in the artifact.
Copy-Item (Join-Path $lamaPath "LICENSE") (Join-Path $destinationPath "LAMA-LICENSE.txt")
Copy-Item (Join-Path $root "upstream/UPSTREAM.json") (Join-Path $destinationPath "UPSTREAM.json")
