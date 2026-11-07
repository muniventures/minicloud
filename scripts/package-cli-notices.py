#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0

import argparse
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("repository")
    parser.add_argument("tag")
    args = parser.parse_args()
    output = args.output
    licenses = output / "licenses"
    licenses.mkdir(exist_ok=True)
    shutil.copyfile(ROOT / "LICENSES/MPL-2.0.txt", output / "LICENSE.txt")
    shutil.copyfile(ROOT / "LICENSES/YamlDotNet-MIT.txt", licenses / "YamlDotNet-MIT.txt")

    assets = json.loads((ROOT / "modules/cli/obj/project.assets.json").read_text(encoding="utf-8"))
    downloads = [
        dependency
        for framework in assets["project"]["frameworks"].values()
        for dependency in framework.get("downloadDependencies", [])
        if dependency["name"].startswith("Microsoft.NETCore.App.Runtime.")
    ]
    if len(downloads) != 1:
        raise RuntimeError("Expected one self-contained .NET runtime pack.")
    dependency = downloads[0]
    version = re.fullmatch(r"\[([^,]+), \1\]", dependency["version"])
    if version is None:
        raise RuntimeError("Expected an exact .NET runtime version.")
    package = Path(dependency["name"].lower(), version.group(1))
    pack = next((Path(folder) / package for folder in assets["packageFolders"]
                 if (Path(folder) / package).is_dir()), None)
    if pack is None:
        raise RuntimeError("Could not locate the .NET runtime pack.")
    for filename in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        shutil.copyfile(pack / filename, licenses / f"dotnet-{filename}")

    (output / "SOURCE.txt").write_text(
        "Minicloud CLI and its client contracts are available under MPL-2.0.\n"
        f"Corresponding source: https://github.com/{args.repository}/tree/{args.tag}\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    main()
