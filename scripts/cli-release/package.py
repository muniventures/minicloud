#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Package only single-file CLI outputs; never publish or select releases here."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import zipfile

RIDS = ("linux-x64", "linux-arm64", "osx-arm64", "win-x64")
WORKFLOW = "https://github.com/muniventures/minicloud/.github/workflows/cli-release.yml"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe(name):
    return all(re.fullmatch(r"[a-zA-Z0-9._-]+", part) and not part.endswith(".")
               and not re.fullmatch(r"(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part)
               for part in name.split("/"))


def package(root, output, tag, source):
    if not re.fullmatch(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", tag):
        raise ValueError("Select an exact CLI v* semantic version, never server-v/latest.")
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("Full source SHA required.")
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Release output must be empty; never replace historical assets.")
    provenance = {"schemaVersion": 1, "tag": tag, "sourceRevision": source, "workflow": WORKFLOW, "artifacts": {}}
    checksums = {}
    for rid in RIDS:
        directory = root / rid
        executable = "minicloud.exe" if rid == "win-x64" else "minicloud"
        files = {}
        for path in directory.rglob("*"):
            if path.is_symlink() or not (path.is_dir() or path.is_file()):
                raise ValueError("No links/special files in CLI archives.")
            if path.is_file():
                name = path.relative_to(directory).as_posix()
                if not safe(name):
                    raise ValueError("Unsafe member.")
                files[name] = path.read_bytes()
        required = {executable, "LICENSE.txt", "SOURCE.txt", "licenses/YamlDotNet-MIT.txt",
                    "licenses/dotnet-LICENSE.TXT", "licenses/dotnet-THIRD-PARTY-NOTICES.TXT"}
        if not required <= files.keys() or any(name.endswith((".dll", ".pdb")) for name in files):
            raise ValueError("Missing license/single-file handoff; do not package a partial runtime.")
        files["cli-release.json"] = json.dumps({key: value for key, value in provenance.items() if key != "artifacts"}
                                              | {"rid": rid}, sort_keys=True).encode() + b"\n"
        name = f"minicloud-{tag[1:]}-{rid}." + ("zip" if rid == "win-x64" else "tar.gz")
        archive = output / name
        if rid == "win-x64":
            with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as target:
                for member, data in sorted(files.items()):
                    entry = zipfile.ZipInfo(member, (2026, 1, 1, 0, 0, 0))
                    entry.external_attr = 0o100644 << 16
                    entry.compress_type = zipfile.ZIP_DEFLATED
                    target.writestr(entry, data)
        else:
            with tarfile.open(archive, "w:gz", format=tarfile.USTAR_FORMAT) as target:
                for member, data in sorted(files.items()):
                    entry = tarfile.TarInfo(member)
                    entry.size = len(data)
                    entry.mode = 0o755 if member == executable else 0o644
                    target.addfile(entry, io.BytesIO(data))
        checksums[name] = digest(archive.read_bytes())
        provenance["artifacts"][rid] = {"name": name, "sha256": checksums[name],
                                        "files": {member: digest(data) for member, data in sorted(files.items())}}
    data = json.dumps(provenance, indent=2, sort_keys=True).encode() + b"\n"
    (output / "cli-provenance.json").write_bytes(data)
    checksums["cli-provenance.json"] = digest(data)
    (output / "checksums.txt").write_text("".join(f"{sha}  {name}\n" for name, sha in sorted(checksums.items())))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--source", required=True)
    args = parser.parse_args()
    package(args.root, args.output, args.tag, args.source)
