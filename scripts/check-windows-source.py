#!/usr/bin/env python3
"""Check the Windows source layout and scripts without downloading or building native dependencies."""
import ast
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import tomllib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHELL = ROOT / "shell/src-tauri"


def run(*args, **kwargs):
    return subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", timeout=30, **kwargs)


def bash_path():
    candidates = [Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Git/bin/bash.exe"]
    if found := shutil.which("bash"):
        candidates.append(Path(found))
    for candidate in candidates:
        if candidate.is_file() and "WindowsApps" not in str(candidate):
            return str(candidate)
    raise RuntimeError("Git Bash is required for shell syntax and host-guard checks")


class WindowsSourceTests(unittest.TestCase):
    def test_layout(self):
        for name in (
            "Package.swift", "Sources/winsight/main.swift", "Sources/winsight/WorkerService.swift",
            "Sources/WinSightKit/Platform/Portable/Docs+Portable.swift", "Sources/WinSightKit/WindowsMLEmbedder.swift",
            "inference/WinSight.InferenceHost/StdioServer.cs", "Sources/CPDFium/module.modulemap",
            "Tests/WindowsMLRuntimeTests/Fixtures/speech.wav", "Tests/WinSightKitTests/Pictures/pattern.avif",
            "Tests/WinSightKitTests/Videos/shots.ts", "shell/src-tauri/windows/hooks.nsh",
            "scripts/licenses/swift-LICENSE.txt", "scripts/build-inference-host.ps1",
        ):
            self.assertTrue((ROOT / name).is_file(), name)
        for name in (
            "Sources/DigUpApp", "Sources/WinSightKit/Platform/Apple", "Sources/CLlama", "Sources/CLlamaShared",
            "Sources/LlamaRuntime", "Sources/CLlamaWindows", "scripts/build-llama.sh",
            "worker", "Package.resolved", "build.sh", "appcast.xml", "Resources/AppIcon.icns",
            "scripts/package-linux.sh", "scripts/release.sh", ".github/workflows/homebrew.yml",
            "shell/src-tauri/tauri.linux.conf.json", "shell/src-tauri/tauri.macos.conf.json",
            "shell/src-tauri/src/wayland.rs", "updates/linux-x86_64.json",
        ):
            self.assertFalse((ROOT / name).exists(), name)
        swift = (ROOT / "Package.swift").read_text(encoding="utf-8")
        self.assertIn("#if !os(Windows)", swift)
        for value in ("Sparkle", "DigUpApp", "CLlama", "LlamaRuntime", ".macOS("):
            self.assertNotIn(value, swift)
        source = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "Sources").rglob("*.swift"))
        for value in ("WINSIGHT_WORKER_DIR", "WorkerConfig", "EmbeddingWorker", "LlamaEmbedder", "LlamaModels", "WINSIGHT_EMBEDDING_BACKEND"):
            self.assertFalse(value in source, f"Retired worker reference remains: {value}")

    def test_manifests(self):
        paths = [*SHELL.glob("*.json"), *(SHELL / "capabilities").glob("*.json"),
                 *(ROOT / "evals").glob("*.json"), *(ROOT / "updates").glob("*.json"),
                 *(ROOT / "Tests").rglob("*.json")]
        for path in paths:
            json.loads(path.read_text(encoding="utf-8"))
        for name in ("Cargo.toml", "Cargo.lock", ".cargo/config.toml"):
            tomllib.loads((SHELL / name).read_text(encoding="utf-8"))
        manifest = tomllib.loads((SHELL / "Cargo.toml").read_text(encoding="utf-8"))
        self.assertEqual(set(manifest["target"]), {"cfg(windows)"})
        self.assertNotIn("macos-private-api", manifest["dependencies"]["tauri"]["features"])
        config = json.loads((SHELL / "tauri.conf.json").read_text(encoding="utf-8"))
        self.assertEqual(config["bundle"]["targets"], ["nsis"])
        self.assertTrue(config["plugins"]["updater"]["requireSignedVersion"])
        self.assertTrue(config["app"]["security"]["csp"])
        for icon in config["bundle"]["icon"]:
            self.assertTrue((SHELL / icon).is_file(), icon)
        hook = config["bundle"]["windows"]["nsis"]["installerHooks"]
        self.assertTrue((SHELL / hook).is_file())

    def test_winsight_identity_and_icon(self):
        config = json.loads((SHELL / "tauri.conf.json").read_text(encoding="utf-8"))
        self.assertEqual(config["productName"], "WinSight")
        self.assertEqual(config["identifier"], "com.winsight.desktop")
        self.assertEqual(config["plugins"]["updater"]["pubkey"], "")
        self.assertEqual(config["plugins"]["updater"]["endpoints"], [])
        windows = json.loads((SHELL / "tauri.windows.conf.json").read_text(encoding="utf-8"))
        self.assertEqual(windows["mainBinaryName"], "WinSight")
        manifest = tomllib.loads((SHELL / "Cargo.toml").read_text(encoding="utf-8"))
        self.assertEqual(manifest["package"]["name"], "winsight-shell")
        swift = (ROOT / "Package.swift").read_text(encoding="utf-8")
        self.assertIn('.executable(name: "winsight", targets: ["winsight"])', swift)
        self.assertNotIn("DigUpKit", swift)
        for folder in (ROOT / "Sources", SHELL / "src"):
            for path in folder.rglob("*"):
                if path.suffix in {".swift", ".rs"}:
                    text = path.read_text(encoding="utf-8")
                    self.assertFalse('"DIGUP_' in text, f"Stale environment variable in {path.name}")
                    self.assertFalse("import DigUpKit" in text, f"Stale Swift module in {path.name}")
        icon = SHELL / "icons/WinSight.ico"
        payload = icon.read_bytes()
        reserved, kind, count = struct.unpack_from("<HHH", payload)
        self.assertEqual((reserved, kind), (0, 1))
        self.assertGreaterEqual(count, 1)
        for index in range(count):
            length, offset = struct.unpack_from("<II", payload, 6 + index * 16 + 8)
            self.assertGreater(length, 0)
            self.assertLessEqual(offset + length, len(payload))
        baseline = json.loads((ROOT / "docs/winsight-rename-baseline.json").read_text(encoding="utf-8"))
        receipt = json.loads((ROOT / "docs/winsight-icon-assets.json").read_text(encoding="utf-8"))
        self.assertEqual(hashlib.sha256(payload).hexdigest(), baseline["icon_input_sha256"])
        self.assertEqual(hashlib.sha256(payload).hexdigest(), receipt["input_sha256"])
        self.assertIn("icons/WinSight.ico", config["bundle"]["icon"])
        for name, sha in receipt["assets_sha256"].items():
            data = (ROOT / name).read_bytes()
            self.assertEqual(hashlib.sha256(data).hexdigest(), sha, name)
            self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n", name)
        self.assertEqual((ROOT / "shell/ui/icon.png").read_bytes(), (ROOT / "docs/logo.png").read_bytes())

    def test_native_windows_branding_and_model_path(self):
        production = (SHELL / "src/main.rs").read_text(encoding="utf-8").split("#[cfg(test)]", 1)[0]
        for marker in ('"Set Up DigUp"', '"DigUp Settings"', '"Open DigUp"', '"Quit DigUp"',
                       '.tooltip("DigUp")', '.join("DigUp")', 'with_id("digup")', 'tray_by_id("digup")', '"digup-shell:'):
            with self.subTest(marker=marker):
                self.assertFalse(marker in production, f"Retired native name/model path remains: {marker}")

    def test_compile_time_assets(self):
        for path in (SHELL / "src").glob("*.rs"):
            text = path.read_text(encoding="utf-8")
            for target in re.findall(r'include_(?:bytes|str)!\("([^"\n]+)"\)', text):
                self.assertTrue((path.parent / target).is_file(), f"{path.name}: {target}")
            for module in re.findall(r"^mod (\w+);", text, re.M):
                self.assertTrue((path.parent / f"{module}.rs").is_file(), module)
        self.assertFalse("wayland::" in (SHELL / "src/main.rs").read_text(encoding="utf-8"),
                         "Removed Wayland module is still referenced")

    def test_script_syntax(self):
        for path in (ROOT / "scripts").rglob("*.py"):
            ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for path in (ROOT / "shell/ui").glob("*.js"):
            result = run("node", "--check", str(path))
            self.assertEqual(result.returncode, 0, result.stderr)
        bash = bash_path()
        for path in (ROOT / "scripts").glob("*.sh"):
            result = run(bash, "-n", path.as_posix())
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_unsupported_hosts(self):
        bash = bash_path()
        for path in (ROOT / "scripts").glob("*.sh"):
            text = path.read_text(encoding="utf-8")
            # Execute only preflight, never dependency fetching, builds, installs or application launch.
            guard = text.split('\ncd "$(dirname "$0")/.."', 1)[0]
            self.assertIn("Windows x64 only", guard, path.name)
            result = run(bash, "-c", 'uname() { printf "Unsupported\\n"; }\n' + guard)
            self.assertEqual(result.returncode, 1, f"{path.name}: {result.stderr}")
            self.assertIn("Windows x64 only", result.stderr)

    def test_update_feed(self):
        script = str(ROOT / "scripts/update-feed.py")
        env = dict(os.environ, DOWNLOAD_BASE="https://example.invalid/releases/")
        with tempfile.TemporaryDirectory(prefix="winsight-windows-feed-") as folder:
            base = Path(folder)
            packages, output = base / "packages", base / "output"
            packages.mkdir()
            installer = packages / "WinSight_0.7.0-test_x64-setup.exe"
            installer.write_bytes(b"fixture, never executable")
            missing = run(sys.executable, script, str(packages), str(output), env=env)
            self.assertNotEqual(missing.returncode, 0)
            self.assertIn("has no", missing.stderr)
            self.assertFalse(output.exists())
            installer.with_name(installer.name + ".sig").write_text("fixture-signature", encoding="utf-8")
            unconfigured_env = {key: value for key, value in env.items() if key != "DOWNLOAD_BASE"}
            unconfigured = run(sys.executable, script, str(packages), str(output), env=unconfigured_env)
            self.assertNotEqual(unconfigured.returncode, 0)
            self.assertIn("set DOWNLOAD_BASE", unconfigured.stderr)
            self.assertFalse(output.exists())
            result = run(sys.executable, script, str(packages), str(output), env=env)
            self.assertEqual(result.returncode, 0, result.stderr)
            feed = json.loads((output / "windows-x86_64.json").read_text(encoding="utf-8"))
            self.assertEqual(set(feed["platforms"]), {"windows-x86_64"})
            self.assertEqual(feed["version"], "0.7.0-test")
            self.assertEqual(feed["platforms"]["windows-x86_64"]["signature"], "fixture-signature")
            self.assertEqual(feed["platforms"]["windows-x86_64"]["url"], env["DOWNLOAD_BASE"] + installer.name)
            self.assertEqual({p.name for p in output.iterdir()}, {"windows-x86_64.json"})
            installer.unlink()
            (packages / "WinSight_0.7.0-test_amd64.AppImage").write_bytes(b"unsupported platform fixture")
            unsupported = run(sys.executable, script, str(packages), str(base / "unsupported"), env=env)
            self.assertNotEqual(unsupported.returncode, 0)
            self.assertIn("no package", unsupported.stderr)
            self.assertFalse((base / "unsupported").exists())

    def test_notices_rejects_other_platforms(self):
        result = run(sys.executable, str(ROOT / "scripts/notices.py"), "linux", "unused-notices.txt")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("notices.py windows", result.stderr)
        self.assertFalse((ROOT / "unused-notices.txt").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
