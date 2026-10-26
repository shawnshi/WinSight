#!/usr/bin/env python3
"""Validate the native-only source layout without restoring packages or loading models."""
import ast
import copy
import hashlib
import json
import re
import struct
import tempfile
import tomllib
import unittest
from defusedxml import ElementTree as ET
from defusedxml.common import EntitiesForbidden
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODEL_REVISION = "daa72c51243991dfcaf9f9137d2c573d8f7790c0"


def confined(root, name):
    if not isinstance(name, str) or not name or "\\" in name or ":" in name:
        raise ValueError("Expected a repository-relative POSIX path")
    if any(part in ("", ".", "..") for part in name.split("/")):
        raise ValueError("Unsafe repository-relative path")
    path = (root / name).resolve()
    path.relative_to(root.resolve())
    return path


def check_files(root, files):
    seen = set()
    for item in files:
        name = item["path"]
        if name in seen:
            raise ValueError("Duplicate artifact path")
        seen.add(name)
        path = confined(root, name)
        digest = item["sha256"]
        if not isinstance(digest, str) or re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            raise ValueError("Invalid SHA-256")
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError("Artifact SHA-256 mismatch: " + name)
        if "bytes" in item and len(data) != item["bytes"]:
            raise ValueError("Artifact size mismatch: " + name)


def check_model_manifest(manifest):
    if manifest["repository"] != "onnx-community/embeddinggemma-2-ONNX":
        raise ValueError("Unexpected model repository")
    if manifest["revision"] != MODEL_REVISION:
        raise ValueError("Model revision requires an explicit profile-compatible change")
    files = manifest["files"]
    if len(files) != 11 or len({item["path"] for item in files}) != 11:
        raise ValueError("Incomplete or duplicate model file set")
    for item in files:
        confined(ROOT, item["path"])
        if type(item["size"]) is not int or item["size"] <= 0:
            raise ValueError("Invalid model artifact size")
        if re.fullmatch(r"[0-9a-f]{64}", item["sha256"]) is None:
            raise ValueError("Invalid model artifact SHA-256")
    if sum(item["size"] for item in files) != 505137637:
        raise ValueError("Model artifact set changed")


class NativeSourceTests(unittest.TestCase):
    def test_native_entry_points_and_independent_tokenizer(self):
        for name in ("Core", "Infrastructure", "App", "Cli"):
            self.assertTrue((ROOT / f"desktop/src/WinSight.{name}/WinSight.{name}.csproj").is_file())
        self.assertTrue((ROOT / "inference/WinSight.InferenceHost/Program.cs").is_file())
        cargo = tomllib.loads((ROOT / "native/winsight-tokenizer/Cargo.toml").read_text(encoding="utf-8"))
        dependency = cargo["dependencies"]["tokenizers"]
        self.assertEqual(dependency["version"], "=0.23.2")
        self.assertEqual(dependency["git"], "https://github.com/huggingface/tokenizers.git")
        self.assertEqual(dependency["rev"], "301277072263fb3830eb66eec7e47adadf76f1dc")
        self.assertFalse(dependency["default-features"])
        self.assertEqual(dependency["features"], ["onig"])
        identity = (ROOT / "inference/WinSight.InferenceHost/NativeTokenizer.cs").read_text(encoding="utf-8")
        self.assertIn("tokenizers-git-v0.23.3;rust=0.23.2;rev=" + dependency["rev"], identity)
        profile = (ROOT / "inference/WinSight.InferenceHost/ModelManifest.cs").read_text(encoding="utf-8")
        self.assertIn("Append(NativeTokenizer.RuntimeIdentity)", profile)
        self.assertTrue((ROOT / "native/winsight-tokenizer/Cargo.lock").is_file())

    def test_retired_runtime_entry_points_are_absent(self):
        for name in ("Package.swift", "shell/src-tauri/Cargo.toml", "shell/src-tauri/Cargo.lock", "shell/src-tauri/tauri.conf.json", "shell/ui/index.html"):
            self.assertFalse((ROOT / name).exists(), name)
        # Ignored build caches may remain locally; only source files are retired.
        for base in (ROOT / "Sources", ROOT / "Tests"):
            self.assertEqual(list(base.rglob("*.swift")), [])

    def test_retired_build_publish_and_oracle_generators_are_absent(self):
        for name in ("build-ffmpeg.sh", "fetch-vendor.sh", "dev-run.sh", "package-windows.sh", "check-windows-package.sh", "notices.py", "update-feed.py", "check-brand-ui.py", "native-ui-inventory.py", "native-ui-freeze.py"):
            self.assertFalse((ROOT / "scripts" / name).exists(), name)

    def test_native_project_references_and_direct_package_locks(self):
        projects = list((ROOT / "desktop/src").glob("*/*.csproj")) + list((ROOT / "inference").glob("*/*.csproj"))
        projects.append(ROOT / "inference/WinSight.OcrHost/vendor/RapidOcrNet/RapidOcrNet.WinSight.csproj")
        # 9 = 5 desktop + InferenceHost + OcrHost + isolated Foundry Local + isolated Windows ML NPU host.
        self.assertEqual(len(projects), 9)
        for project in projects:
            tree = ET.parse(project).getroot()
            if tree is None:
                raise ValueError("Empty MSBuild project: " + str(project))
            for item in tree.iter("ProjectReference"):
                target = (project.parent / item.attrib["Include"].replace("\\", "/")).resolve()
                self.assertTrue(target.is_file(), str(target))
            lock = json.loads(project.with_name("packages.lock.json").read_text(encoding="utf-8"))
            for item in tree.iter("PackageReference"):
                name, requested = item.attrib["Include"], item.attrib["Version"]
                self.assertRegex(requested, r"^\[[0-9][^,]*\]$")
                versions = {entries[name]["resolved"] for entries in lock["dependencies"].values() if name in entries}
                self.assertEqual(versions, {requested[1:-1]}, name)
        # This is not a CLI RID restore oracle; its known locked-build issue remains disclosed.

    def test_ocr_isolated_dependency_and_runtime_identity(self):
        base = ROOT / "inference/WinSight.OcrHost"
        manifest = json.loads((base / "model-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual((manifest["engine"], manifest["engineVersion"], manifest["cpuThreads"]),
                         ("RapidOcrNet", "4.2.0-winsight.1", 4))
        self.assertEqual(manifest["sourceCommit"], "708cae2fcb88720e1d891a81b5ee3e8b2bcc139e")
        self.assertEqual(len(manifest["files"]), 4)
        self.assertEqual(sum(item["bytes"] for item in manifest["files"]), 32257432)
        self.assertEqual(len({item["path"] for item in manifest["files"]}), 4)
        for item in manifest["files"]:
            confined(base, item["path"])
            self.assertRegex(item["sha256"], r"^[0-9a-f]{64}$")
        host = ET.parse(ROOT / "inference/WinSight.InferenceHost/WinSight.InferenceHost.csproj").getroot()
        assert host is not None
        self.assertFalse(any("RapidOcr" in item.attrib["Include"] or "SkiaSharp" in item.attrib["Include"]
                             for item in host.iter("PackageReference")))
        fork = ET.parse(base / "vendor/RapidOcrNet/RapidOcrNet.WinSight.csproj").getroot()
        assert fork is not None
        self.assertEqual(fork.findtext(".//AssemblyName"), "RapidOcrNet.WinSight")
        self.assertEqual(fork.findtext(".//Version"), "4.2.0-winsight.1")
        records = json.loads((base / "provenance/revision-source-hashes.json").read_text(encoding="utf-8"))
        self.assertEqual({row["file"] for row in records if row["modified"]},
                         {"TextRecognizer.cs", "TextDetector.cs", "TextClassifier.cs"})
        self.assertTrue((base / "vendor/RapidOcrNet/LICENSE.txt").is_file())
        self.assertTrue((base / "vendor/RapidOcrNet/NOTICE").is_file())
        bridge = (ROOT / "inference/WinSight.InferenceHost/IsolatedOcr.cs").read_text(encoding="utf-8")
        self.assertIn("NativeMediaOperation.InspectOcr", bridge)
        self.assertIn('OcrIdentity.ModelProfile', bridge)

    def test_native_tokenizer_artifact_binding_and_linked_identity(self):
        host = ET.parse(ROOT / "inference/WinSight.InferenceHost/WinSight.InferenceHost.csproj").getroot()
        assert host is not None
        binding = next(item for item in host.iter("Target") if item.attrib["Name"] == "RequireNativeTokenizer")
        self.assertEqual(len(list(binding.iter("GetFileHash"))), 4)
        self.assertTrue(any(item.attrib.get("File", "").endswith(".source.sha256") for item in binding.iter("ReadLinesFromFile")))
        self.assertTrue((ROOT / "scripts/build-native-tokenizer.ps1").is_file())
        subset = ET.parse(ROOT / "desktop/tests/WinSight.Indexing.Tests/WinSight.Indexing.Tests.csproj").getroot()
        assert subset is not None
        self.assertTrue(any(item.attrib.get("Include", "").endswith("/OcrIdentity.cs") for item in subset.iter("Compile")))
        self.assertTrue(any(item.attrib.get("LogicalName") == "WinSight.OcrHost.model-manifest.json" for item in subset.iter("EmbeddedResource")))

    def test_project_xml_rejects_entity_expansion(self):
        payload = b'<!DOCTYPE Project [<!ENTITY secret SYSTEM "file:///must-not-read">]><Project>&secret;</Project>'
        with self.assertRaises(EntitiesForbidden):
            ET.fromstring(payload)

    def test_pinned_model_manifest_without_opening_model_files(self):
        manifest = json.loads((ROOT / "inference/WinSight.InferenceHost/model-manifest.json").read_text(encoding="utf-8"))
        check_model_manifest(manifest)

    def test_model_manifest_rejects_duplicate_and_revision_drift(self):
        manifest = json.loads((ROOT / "inference/WinSight.InferenceHost/model-manifest.json").read_text(encoding="utf-8"))
        duplicate = copy.deepcopy(manifest)
        duplicate["files"][1] = duplicate["files"][0]
        with self.assertRaises(ValueError):
            check_model_manifest(duplicate)
        manifest["revision"] = "main"
        with self.assertRaises(ValueError):
            check_model_manifest(manifest)

    def test_canonical_ico_and_retained_png_bytes(self):
        receipt = json.loads((ROOT / "docs/winsight-icon-assets.json").read_text(encoding="utf-8"))
        self.assertEqual(receipt["input_path"], "Resources/AppIcon/WinSight.ico")
        check_files(ROOT, [{"path": receipt["input_path"], "sha256": receipt["input_sha256"]}])
        check_files(ROOT, [{"path": name, "sha256": digest} for name, digest in receipt["assets_sha256"].items()])
        data = confined(ROOT, receipt["input_path"]).read_bytes()
        reserved, kind, count = struct.unpack_from("<HHH", data)
        self.assertEqual((reserved, kind), (0, 1))
        sizes = sorted((data[6 + 16 * i] or 256, data[7 + 16 * i] or 256) for i in range(count))
        self.assertEqual(sizes, [tuple(pair) for pair in receipt["ico_sizes"]])

    def test_preserved_media_fixture_provenance(self):
        receipt = json.loads((ROOT / "desktop/tests/Fixtures/provenance.json").read_text(encoding="utf-8"))
        self.assertEqual(receipt["sourceCommit"], "400c5b501e664f7aaaf2c2ec5e1c270cf0425a87")
        self.assertEqual(len(receipt["files"]), 9)
        check_files(ROOT, receipt["files"])

    def test_artifact_checks_reject_changed_bytes_and_duplicate_paths(self):
        with tempfile.TemporaryDirectory(prefix="winsight-source-check-") as directory:
            base = Path(directory)
            path = base / "fixture.bin"
            path.write_bytes(b"original")
            item = {"path": "fixture.bin", "sha256": hashlib.sha256(b"original").hexdigest(), "bytes": 8}
            check_files(base, [item])
            with self.assertRaises(ValueError):
                check_files(base, [item, item])
            path.write_bytes(b"modified")
            with self.assertRaises(ValueError):
                check_files(base, [item])

    def test_artifact_paths_reject_escape(self):
        for name in ("../outside", "/outside", "C:/outside", "a/../outside", "a\\outside"):
            with self.assertRaises(ValueError):
                confined(ROOT, name)

    def test_license_attribution_is_retained(self):
        for name in ("LICENSE", "NOTICE", "licenses/DigUp-MIT.txt", "licenses/DigUp-Acknowledgements.txt", "desktop/licenses/DigUp-MIT.txt", "licenses/historical/swift-LICENSE.txt"):
            self.assertTrue((ROOT / name).is_file(), name)

    def test_ci_targets_native_tokenizer_not_retired_shell(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn("working-directory: native/winsight-tokenizer", workflow)
        self.assertNotIn("working-directory: shell/", workflow)
        self.assertIn("cargo test --locked", workflow)
        self.assertIn("python scripts/check-windows-source.py", workflow)

    def test_python_helpers_parse_without_importing_optional_packages(self):
        for path in (ROOT / "scripts").rglob("*.py"):
            if "__pycache__" not in path.parts:
                ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))

    def test_retirement_does_not_claim_parity_or_release_acceptance(self):
        status = json.loads((ROOT / "docs/native-migration/status.json").read_text(encoding="utf-8"))
        retirement = status["sourceRetirement"]
        self.assertEqual(retirement["mode"], "native-only-no-legacy-compatibility")
        self.assertFalse(retirement["fullParityAccepted"])
        self.assertFalse(retirement["releaseAccepted"])
        self.assertFalse(retirement["legacyCLICompatibility"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
