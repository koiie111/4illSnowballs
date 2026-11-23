import json
import os
import re
import subprocess
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

baseline = ET.parse("src/4illSnowballs.csproj").findtext(".//CounterStrikeSharpVersion")
with urllib.request.urlopen(
    "https://api.nuget.org/v3-flatcontainer/counterstrikesharp.api/index.json",
    timeout=30,
) as response:
    stable = [
        version for version in json.load(response)["versions"]
        if re.fullmatch(r"\d+\.\d+\.\d+", version)
    ]
latest = max(stable, key=lambda version: tuple(map(int, version.split("."))))

if os.environ["GITHUB_EVENT_NAME"] == "schedule":
    name = f'snowballs-{latest}-{os.environ["GITHUB_SHA"]}'
    result = subprocess.check_output([
        "gh", "api", "--paginate", f'repos/{os.environ["GITHUB_REPOSITORY"]}/actions/artifacts?name={name}',
        "--jq", ".artifacts[] | select(.expired == false) | .workflow_run.id"
    ], text=True)
    # Upload alone does not mark a cancelled or failed run as successful.
    processed = any(
        subprocess.check_output([
            "gh", "api", f'repos/{os.environ["GITHUB_REPOSITORY"]}/actions/runs/{run_id}',
            "--jq", ".conclusion"
        ], text=True).strip() == "success"
        for run_id in result.splitlines()
    )
    versions = [] if processed else [latest]
else:
    versions = list(dict.fromkeys([baseline, latest]))

with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
    output.write(f"matrix={json.dumps(versions)}\n")
