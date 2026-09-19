"""Package a tested publish directory and Git-visible source files. No upload occurs."""
import hashlib
from pathlib import Path
import subprocess
import zipfile

root = Path(__file__).resolve().parents[1]
artifacts = root / "artifacts"
published = artifacts / "publish/win-x64"
if not (published / "SehtMcp.exe").exists():
    raise SystemExit("Run scripts/build.ps1 first.")
binary = artifacts / "SehtMCP-0.1.0-win-x64.zip"
source = artifacts / "SehtMCP-0.1.0-source.zip"
with zipfile.ZipFile(binary, "w", zipfile.ZIP_DEFLATED) as zip:
    for file in sorted(published.rglob("*")):
        if file.is_file():
            zip.write(file, "SehtMCP/" + file.relative_to(published).as_posix())
files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=root).decode("utf-8").split("\0")
with zipfile.ZipFile(source, "w", zipfile.ZIP_DEFLATED) as zip:
    for name in sorted(set(files) - {""}):
        file = root / name
        if file.is_file():
            zip.write(file, "SehtMCP/" + name)
checksums = []
for file in [binary, source]:
    with file.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    checksums.append(f"{digest}  {file.name}")
(artifacts / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n")
print("\n".join(checksums))
