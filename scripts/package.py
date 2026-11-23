import hashlib
import json
import sys
import zipfile
from pathlib import Path

version, commit = sys.argv[1:]
output = Path("artifacts")
output.mkdir(exist_ok=True)
archive = output / f"4illSnowballs-css{version}-{commit[:12]}.zip"
dll = Path("src/bin/Release/net10.0/4illSnowballs.dll")
target = "addons/counterstrikesharp/plugins/4illSnowballs"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
    package.write(dll, f"{target}/{dll.name}")
    package.writestr("build.json", json.dumps({
        "commit": commit,
        "counterStrikeSharpApi": version,
        "dllSha256": hashlib.sha256(dll.read_bytes()).hexdigest()
    }, indent=2))
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
