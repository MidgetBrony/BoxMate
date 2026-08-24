from __future__ import annotations

import hashlib
import io
import tarfile
import zipfile
from pathlib import Path


root = Path(__file__).resolve().parent.parent
dist = root / "dist"
dist.mkdir(exist_ok=True)

windows_source = root / "bin/Release/net10.0/win-x64/publish"
linux_source = root / "bin/Release/net10.0/linux-x64/publish"
windows_archive = dist / "BoxMate-windows-x64.zip"
linux_archive = dist / "BoxMate-linux-x64.tar.gz"

with zipfile.ZipFile(windows_archive, "w", zipfile.ZIP_DEFLATED) as archive:
    for name in ("BoxMate.exe", "av_libglesv2.dll", "libSkiaSharp.dll"):
        archive.write(windows_source / name, name)

with tarfile.open(linux_archive, "w:gz") as archive:
    for name in ("BoxMate", "libHarfBuzzSharp.so", "libSkiaSharp.so"):
        data = (linux_source / name).read_bytes()
        info = tarfile.TarInfo(name)
        info.size = len(data)
        info.mode = 0o755 if name == "BoxMate" else 0o644
        archive.addfile(info, io.BytesIO(data))

checksums = []
for path in (windows_archive, linux_archive):
    checksums.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}")
(dist / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="utf-8")
