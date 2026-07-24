# Third-party notices

## LaMa

- Repository: `https://github.com/advimman/lama`
- Pinned commit: `786f5936b27fb3dacd2b1ad799e4de968ea697e7`
- License: Apache License 2.0

CI imports the upstream `FFCResNetGenerator` implementation directly. The trusted Big-LaMa checkpoint is converted in CI to a plain tensor-only `generator.safetensors` file after the original generator is instantiated and forward-tested. The upstream source and LICENSE remain inside the Windows runtime.

## Big-LaMa model mirror

- Mirror: `smartywu/big-lama` on Hugging Face
- Pinned archive SHA-256: `f1b358ca24093b93a106183b98a3dea6e8ed09f3b43ea7251eb2c81e7b4575f6`

The raw Lightning checkpoint is used only in the isolated Linux export job and is not shipped in the Windows artifact.

## Safetensors

The Windows runtime uses Safetensors to load a tensor-only generator state without Python object unpickling. Its upstream license applies.

## FFmpeg

FFmpeg is downloaded by GitHub Actions from the Gyan Windows builds and included in the Windows artifact. Users and redistributors must comply with the licensing terms of that FFmpeg build.

## PyTorch, OpenCV, NumPy, Kornia, PyYAML

These packages are installed into the application-local embedded Python runtime during GitHub Actions and bundled in the Windows artifact. They are not installed on the user's machine at first use. Their respective licenses apply.

## Microsoft Windows App SDK

The desktop UI uses Microsoft Windows App SDK / WinUI 3 under Microsoft's applicable license terms.
