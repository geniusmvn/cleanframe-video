# Third-party notices

## LaMa

- Repository: `https://github.com/advimman/lama`
- Pinned commit: `786f5936b27fb3dacd2b1ad799e4de968ea697e7`
- License: Apache License 2.0

ERASA VIDEO downloads the upstream source at runtime and imports the original `FFCResNetGenerator` implementation. The upstream LICENSE remains inside the downloaded runtime.

## FFmpeg

FFmpeg is downloaded by GitHub Actions from the Gyan Windows builds and included in the Windows artifact. Users and redistributors must comply with the licensing terms of the particular FFmpeg build.

## PyTorch, OpenCV, NumPy, Kornia, PyYAML

These packages are installed into the application-local embedded Python runtime on first use. Their respective upstream licenses apply.

## Microsoft Windows App SDK

The desktop UI uses Microsoft Windows App SDK / WinUI 3 under Microsoft's applicable license terms.
