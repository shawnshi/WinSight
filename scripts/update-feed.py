#!/usr/bin/env python3
"""Writes the Windows x64 update feed from a signed NSIS installer; never publishes it.

    scripts/update-feed.py <folder> <updates folder> [--notes notes.txt] [--tag v<version>] [--also-latest]

The input folder must contain one WinSight_<version>_x64-setup.exe and its .sig file. Output is
windows-x86_64.json; --also-latest also writes the legacy latest.json name. DOWNLOAD_BASE is required
and may contain {tag}, replaced by --tag or v<version>. No upstream release URL is assumed.
"""
import argparse
import datetime
import json
import os
import re
import sys
from pathlib import Path

# Tauri substitutes this Windows target key when looking up the installer.
PLATFORMS = {
    "windows-x86_64": re.compile(r"^WinSight_(?P<version>[^_]+)_x64-setup\.exe$"),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder")
    parser.add_argument("updates", help="the folder the feeds are written to (updates/ in the repository)")
    parser.add_argument("--notes", help="a plain text file: what's new, for the update's dialog")
    parser.add_argument("--tag", help="the release's tag (default: v<version>)")
    parser.add_argument("--also-latest", action="store_true",
                        help="write latest.json too, for copies from before the per-platform feeds")
    args = parser.parse_args()

    folder = Path(args.folder)
    platforms, versions = {}, set()
    for key, pattern in PLATFORMS.items():
        found = [path for path in folder.iterdir() if pattern.match(path.name)]
        if not found:
            continue
        if len(found) > 1:
            sys.exit(f"update-feed: more than one {pattern.pattern} in {folder}")
        package = found[0]
        signature = package.with_name(package.name + ".sig")
        if not signature.is_file():
            sys.exit(f"update-feed: {package.name} has no {signature.name}: build the packages with the signing key")
        match = pattern.match(package.name)
        if match is None:
            sys.exit(f"update-feed: invalid Windows installer name: {package.name}")
        versions.add(match["version"])
        platforms[key] = {"package": package.name, "signature": signature.read_text().strip()}
    if not platforms:
        sys.exit(f"update-feed: no package in {folder} (a Windows installer with its .sig)")
    if len(versions) != 1:
        sys.exit(f"update-feed: the packages are of different versions: {sorted(versions)}")
    version = versions.pop()
    tag = args.tag or f"v{version}"
    base = os.environ.get("DOWNLOAD_BASE")
    if not base:
        sys.exit("update-feed: set DOWNLOAD_BASE to your WinSight release download URL")
    base = base.replace("{tag}", tag)
    notes = Path(args.notes).read_text().strip() if args.notes else ""
    date = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

    def feed(keys):
        return {"version": version, "notes": notes, "pub_date": date,
                "platforms": {key: {"signature": platforms[key]["signature"], "url": base + platforms[key]["package"]}
                              for key in keys}}

    out = Path(args.updates)
    out.mkdir(parents=True, exist_ok=True)
    written = {key: out / f"{key}.json" for key in platforms}
    if args.also_latest:
        if len(platforms) < len(PLATFORMS):
            sys.exit("update-feed: latest.json needs the Windows installer's package")
        written["latest"] = out / "latest.json"
    for name, path in written.items():
        keys = list(platforms) if name == "latest" else [name]
        path.write_text(json.dumps(feed(keys), indent=2, ensure_ascii=False) + "\n")
        print(f"{path}: WinSight {version}, " + ", ".join(f"{key} → {base + platforms[key]['package']}" for key in keys))


if __name__ == "__main__":
    main()
