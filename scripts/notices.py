#!/usr/bin/env python3
"""Writes the third-party notices WinSight's Windows package carries: the license of every library they ship, from
the libraries' own license files (PDFium's from Vendor/, ffmpeg's, dav1d's and zlib's from the small
ffmpeg scripts/build-ffmpeg.sh makes, Swift's from the toolchain or scripts/licenses), and of every Rust crate the app
is built from (Cargo's own record of them, normal dependencies only: build tools and macros don't end up in the app).

    scripts/notices.py windows <out.txt>

Run after vendor/FFmpeg preparation and the locked Windows ML/native tokenizer build.
It stops if a license it needs is missing: the packages don't go out without their notices.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VENDOR = ROOT / "Vendor"
RULE = "=" * 80
LICENSE_FILE = re.compile(r"(?i)^(licen[cs]e|copying|copyright|notice|unlicense)([-_.].*)?$")


def read(path):
    return Path(path).read_text(encoding="utf-8", errors="replace").strip()


def need(path):
    path = Path(path)
    if not path.is_file():
        sys.exit(f"notices: missing {path} (run the vendor and ffmpeg scripts first)")
    return read(path)


def section(title, *texts):
    return "\n".join([RULE, title, RULE, "", *[t.strip() + "\n" for t in texts if t]])


def build_ffmpeg_versions():
    script = read(ROOT / "scripts/build-ffmpeg.sh")
    ffmpeg = re.search(r"^FFMPEG_VERSION=(\S+)", script, re.M)
    dav1d = re.search(r"^DAV1D_VERSION=(\S+)", script, re.M)
    if ffmpeg is None or dav1d is None:
        sys.exit("notices: scripts/build-ffmpeg.sh is missing pinned versions")
    return ffmpeg.group(1), dav1d.group(1)


def swift_license():
    """Windows' toolchain ships no license text; keep the upstream copy with the packaging scripts."""
    return need(ROOT / "scripts/licenses/swift-LICENSE.txt")


def stb_license():
    header = need(VENDOR / "stb/include/stb_image.h")
    start = header.rfind("This software is available under 2 licenses")
    if start < 0:
        sys.exit("notices: stb_image.h has no license block")
    block = header[start:].replace("*/", "")
    return "\n".join(line.rstrip() for line in block.splitlines()).strip()


def miniz_license():
    if (VENDOR / "miniz/LICENSE").is_file():
        return read(VENDOR / "miniz/LICENSE")
    sys.exit("notices: Vendor/miniz/LICENSE is missing (run scripts/fetch-vendor.sh)")


def rust_crates(directory, target="x86_64-pc-windows-msvc"):
    """The crates in the app's binary for the platform: Cargo's resolve graph from the app, through normal
    dependencies, without proc-macro crates (they run while compiling)."""
    metadata = json.loads(subprocess.run(
        ["cargo", "metadata", "--format-version", "1", "--offline", "--locked", "--filter-platform", target],
        cwd=directory, check=True, capture_output=True, encoding="utf-8").stdout)
    packages = {package["id"]: package for package in metadata["packages"]}
    nodes = {node["id"]: node for node in metadata["resolve"]["nodes"]}
    root = metadata["resolve"]["root"]
    seen, stack = set(), [root]
    while stack:
        current = stack.pop()
        if current in seen:
            continue
        seen.add(current)
        for dependency in nodes[current]["deps"]:
            if not any(kind["kind"] is None for kind in dependency["dep_kinds"]):
                continue
            package = packages[dependency["pkg"]]
            if any("proc-macro" in target_["kind"] for target_ in package["targets"]):
                continue
            stack.append(dependency["pkg"])
    seen.discard(root)
    return sorted((packages[i] for i in seen), key=lambda package: (package["name"], package["version"]))


def rust_section():
    arm = (ROOT / "shell/src-tauri/helper/inference/runtimes/win-arm64").is_dir()
    native_target = "aarch64-pc-windows-msvc" if arm else "x86_64-pc-windows-msvc"
    graphs = ((ROOT / "shell/src-tauri", "x86_64-pc-windows-msvc"),
              (ROOT / "native/winsight-tokenizer", native_target))
    merged = {crate["id"]: crate for directory, target in graphs for crate in rust_crates(directory, target)}
    crates = sorted(merged.values(), key=lambda crate: (crate["name"], crate["version"]))
    by_text = {}   # a license file's text, its spacing aside → the text and the crates that ship it (printed once)
    without = []
    for crate in crates:
        folder = Path(crate["manifest_path"]).parent
        files = sorted(path for path in folder.iterdir() if path.is_file() and LICENSE_FILE.match(path.name))
        label = f"{crate['name']} {crate['version']} ({crate.get('license') or 'see its files'})"
        if not files:
            without.append((label, crate))
        for path in files:
            text = read(path)
            by_text.setdefault(" ".join(text.split()), (text, []))[1].append(label)
    parts = [RULE, f"Rust crates in the app ({len(crates)}), each under its own license", RULE, "",
             "The app around WinSight's search on Windows is written in Rust with Tauri (https://tauri.app). "
             "These are the crates it is built from, with the license files they ship; a text several crates share "
             "is printed once, after their names.", ""]
    for text, labels in sorted(by_text.values(), key=lambda item: item[1][0]):
        parts += ["-" * 80, *labels, "-" * 80, "", text, ""]
    if without:
        parts += ["-" * 80, "Crates that ship no license file: their license, authors and source (the license texts are "
                  "above, as other crates ship them):", "-" * 80, ""]
        for label, crate in without:
            authors = ", ".join(crate.get("authors") or []) or "its authors"
            source = crate.get("repository") or f"https://crates.io/crates/{crate['name']}"
            parts += [f"{label}: copyright {authors}; source {source}"]
        parts += [""]
    return "\n".join(parts)


def windows_ml_section():
    """Read the exact installed locked NuGet packages; never fetch or invent missing license material."""
    import os
    cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    lock = json.loads(need(ROOT / "inference/WinSight.InferenceHost/packages.lock.json"))
    packages = {}
    for group in lock["dependencies"].values():
        for name, dependency in group.items():
            if dependency.get("resolved"):
                packages[name.lower()] = dependency["resolved"]
    sections = []
    for name, version in sorted(packages.items()):
        folder = cache / name / version.lower()
        files = sorted(path for path in folder.iterdir()
                       if path.is_file() and (LICENSE_FILE.match(path.name)
                                              or path.name.lower() == "thirdpartynotices.txt"))
        if not files:
            sys.exit(f"notices: no license/notice files for locked NuGet package {name} {version}")
        sections.append(section(f"{name} {version}", *[need(path) for path in files]))
    return section("Windows ML / ONNX native and managed dependencies", *sections,
                   need(ROOT / "docs/media-attribution.md"))


def main():
    if len(sys.argv) != 3 or sys.argv[1] != "windows":
        sys.exit(__doc__)
    out = Path(sys.argv[2])
    ffmpeg, dav1d = build_ffmpeg_versions()
    pdfium = VENDOR / "pdfium"
    ffmpeg_licenses = VENDOR / "ffmpeg/licenses"

    parts = [
        "WinSight for Windows includes code from these projects, under their own licenses. Thank you.",
        "",
        "WinSight is derived from DigUp (https://github.com/ARahim3/DigUp), whose MIT license is the first one below. The model it uses, "
        "EmbeddingGemma 2 ONNX weights are not bundled; their upstream model-card terms apply separately. WinSight downloads them from Hugging "
        "Face on first launch.",
        "",
        section("DigUp (upstream)", need(ROOT / "desktop" / "licenses" / "DigUp-MIT.txt")),
        windows_ml_section(),
        section("PDFium (https://pdfium.googlesource.com/pdfium/), built by Benoit Blanchon's pdfium-binaries "
                "(https://github.com/bblanchon/pdfium-binaries), reads PDFs; it includes the libraries whose licenses "
                "follow it",
                need(pdfium / "LICENSE"),
                *[f"{path.name}:\n\n{read(path)}" for path in sorted((pdfium / "licenses").glob("*")) if path.is_file()]),
        section(f"FFmpeg {ffmpeg} (https://ffmpeg.org), reads audio, video and some pictures, as the separate programs "
                f"ffmpeg and ffprobe",
                "FFmpeg is licensed under the GNU Lesser General Public License, version 2.1 or later. This build uses "
                "no GPL or nonfree parts. Its source is FFmpeg's release tarball, "
                f"https://ffmpeg.org/releases/ffmpeg-{ffmpeg}.tar.xz, unmodified, configured and built by "
                "the Windows scripts/build-ffmpeg.sh adapted from DigUp (https://github.com/ARahim3/DigUp), which also builds "
                "dav1d and links zlib. ffmpeg and ffprobe run as programs of their own: you can replace them in WinSight's "
                "helper folder with your own build.",
                f"What this build says about itself:\n\n{need(ffmpeg_licenses / 'ffmpeg-version.txt')}",
                need(ffmpeg_licenses / "ffmpeg-LICENSE.md"),
                need(ffmpeg_licenses / "ffmpeg-COPYING.LGPLv2.1")),
        section(f"dav1d {dav1d} (https://code.videolan.org/videolan/dav1d), decodes AV1 inside ffmpeg",
                need(ffmpeg_licenses / "dav1d-COPYING")),
        section("zlib (https://zlib.net), linked into ffmpeg", need(pdfium / "licenses/zlib.txt")),
        section("SQLite (https://sqlite.org), the index",
                "SQLite is in the public domain: \"The author disclaims copyright to this source code. In place of a "
                "legal notice, here is a blessing: May you do good and not evil. May you find forgiveness for yourself "
                "and forgive others. May you share freely, never taking more than you give.\""),
        section("stb (https://github.com/nothings/stb), reads and writes pictures", stb_license()),
        section("miniz (https://github.com/richgel999/miniz), reads docx and odt files", miniz_license()),
        section("Swift's runtime, Foundation and libdispatch (https://swift.org), which WinSight's helper is written with",
                swift_license()),
        section("ICU (https://icu.unicode.org), in Swift's Foundation and in PDFium (the same license, printed with "
                "PDFium's above)", need(pdfium / "licenses/icu.txt")),
    ]
    parts += [section("Microsoft Visual C++ runtime and WebView2",
                      "WinSight's helper ships Microsoft's Visual C++ runtime libraries, which their license allows "
                      "redistributing, and the app's window uses the Microsoft Edge WebView2 runtime that Windows "
                      "provides (its loader is linked into the app under the WebView2 SDK's license).")]
    parts.append(rust_section())
    out.write_text("\n".join(parts) + "\n", encoding="utf-8")
    print(f"{out}: {out.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
