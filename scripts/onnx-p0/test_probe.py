#!/usr/bin/env python3
"""P0 verification only: synthetic inputs, pinned artifacts, no production index or device downloads."""
import copy
import hashlib
import json
import math
import subprocess
import tempfile
import unittest
from pathlib import Path

from acquire_models import MANIFEST, ROOT, WORK, validate_manifest, verify

EXE = ROOT / "inference/WinSight.InferenceHost/bin/Release/net10.0-windows10.0.26100.0/win-x64/WinSight.InferenceHost.exe"


def command(*args):
    return subprocess.run([str(EXE), *map(str, args)], capture_output=True, text=True,
                          encoding="utf-8", timeout=90, cwd=ROOT)


class P0Tests(unittest.TestCase):
    def test_pinned_artifacts_and_audio_presence(self):
        manifest = json.loads(MANIFEST.read_text())
        validate_manifest(manifest)
        self.assertEqual(len(manifest["files"]), 12)
        records = [verify(WORK / "models" / entry["path"], entry) for entry in manifest["files"]]
        self.assertTrue(all(record["verified"] for record in records))
        self.assertTrue(any(record["path"] == "onnx/audio_encoder_q4.onnx_data" for record in records))

    def test_manifest_cannot_drop_audio_or_escape_workspace(self):
        manifest = json.loads(MANIFEST.read_text())
        for mutate in (lambda value: value.update(revision="main"),
                       lambda value: value["files"][0].update(path="../../outside"),
                       lambda value: value["modalities"].update(audio=False)):
            changed = copy.deepcopy(manifest)
            mutate(changed)
            with self.assertRaises(ValueError):
                validate_manifest(changed)

    def test_corrupt_artifact_is_not_accepted(self):
        with tempfile.TemporaryDirectory(dir=WORK, prefix="corrupt-") as folder:
            path = Path(folder) / "artifact"
            path.write_bytes(b"broken")
            with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
                verify(path, {"path": "fixture", "size": 6, "sha256": "0" * 64})

    def test_windows_ml_and_app_local_runtime(self):
        result = command("environment")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["windowsMlCatalog"], "Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog")
        self.assertIn("CPUExecutionProvider", report["availableProviders"])
        self.assertFalse(report["providerDownloadsAllowed"])
        self.assertFalse(report["productionConnected"])
        self.assertEqual(len(report["loadedOrtModules"]), 1)
        self.assertTrue(Path(report["loadedOrtModules"][0]).resolve().is_relative_to(EXE.parent.resolve()))
        (WORK / "environment.json").write_text(result.stdout)

    def test_all_three_graph_sessions_and_audio_contract(self):
        result = command("inspect", WORK / "models")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(len(report["graphs"]), 3)
        self.assertTrue(all(graph["sessionCreated"] for graph in report["graphs"]))
        audio = next(graph for graph in report["graphs"] if graph["graph"] == "audio_encoder_q4.onnx")
        self.assertEqual([(item["name"], item["elementType"]) for item in audio["inputs"]],
                         [("input_features", "Single"), ("input_features_mask", "Boolean")])
        self.assertEqual(audio["outputs"][0]["dimensions"], [-1, 512])
        core = next(graph for graph in report["graphs"] if graph["graph"] == "model_q4.onnx")
        self.assertEqual({item["name"] for item in core["inputs"]},
                         {"input_ids", "attention_mask", "image_features", "video_features", "audio_features"})
        (WORK / "session-inspection.json").write_text(result.stdout)

    def test_missing_audio_fails_before_loading_other_graphs(self):
        with tempfile.TemporaryDirectory(dir=WORK, prefix="missing-audio-") as folder:
            graphs = Path(folder) / "onnx"
            graphs.mkdir()
            for name in ("model_q4.onnx", "vision_encoder_q4.onnx"):
                (graphs / name).write_bytes(b"not a model")
                (graphs / (name + "_data")).write_bytes(b"not data")
            result = command("inspect", folder)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(result.stdout.strip())
            error = json.loads(result.stderr)
            self.assertEqual(error["error"], "System.IO.FileNotFoundException")
            self.assertIn("audio_encoder_q4", error["message"])

    def test_synthetic_text_cpu_vectors(self):
        result = command("embed-tokens", WORK / "models", WORK / "text-fixture.json", WORK / "profiles")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["nodeExecutionProviders"], ["CPUExecutionProvider"])
        self.assertEqual(len(report["samples"]), 3)
        for sample in report["samples"]:
            self.assertEqual(sample["dimension"], 768)
            self.assertEqual(len(sample["vector"]), 768)
            self.assertTrue(all(math.isfinite(value) for value in sample["vector"]))
            self.assertAlmostEqual(sample["norm"], 1.0, places=3)
        self.assertFalse(report["productionConnected"])
        self.assertFalse(report["productionTokenizerAccepted"])
        self.assertFalse(report["audioInferenceVerified"])
        (WORK / "text-inference.json").write_text(result.stdout)

    def test_invalid_tokens_fail_explicitly(self):
        fixture = json.loads((WORK / "text-fixture.json").read_text(encoding="utf-8"))
        fixture["items"][0]["input_ids"] = [-1]
        with tempfile.TemporaryDirectory(dir=WORK, prefix="invalid-tokens-") as folder:
            path = Path(folder) / "fixture.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            result = command("embed-tokens", WORK / "models", path, Path(folder) / "profiles")
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(result.stdout.strip())
            error = json.loads(result.stderr)
            self.assertEqual(error["error"], "System.ArgumentException")
            self.assertIn("vocabulary ID", error["message"])

    def test_profile_output_location(self):
        # One targeted profile-location execution, not a rerun of the multimodal/model acceptance suite.
        profiles = WORK / "profiles"
        profiles.mkdir(parents=True, exist_ok=True)
        run_dir = Path(tempfile.mkdtemp(dir=profiles, prefix="path-regression-"))
        fixture = json.loads((WORK / "text-fixture.json").read_text(encoding="utf-8"))
        fixture["items"] = fixture["items"][:1]
        fixture["items"][0]["name"] = "profile-location-regression"
        fixture_path = run_dir / "fixture.json"
        fixture_path.write_text(json.dumps(fixture), encoding="utf-8")
        root_profiles_before = {path.name for path in ROOT.glob("onnxruntime_profile__*.json")}
        result = command("embed-tokens", WORK / "models", fixture_path, run_dir)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        path = Path(report["profilePath"])
        self.assertTrue(path.is_absolute())
        self.assertTrue(path.is_file())
        self.assertEqual(path.parent.resolve(), run_dir.resolve())
        events = json.loads(path.read_text(encoding="utf-8"))
        providers = {event["args"]["provider"] for event in events
                     if isinstance(event.get("args"), dict) and "provider" in event["args"]}
        self.assertEqual(providers, {"CPUExecutionProvider"})
        self.assertEqual(sum(event.get("name") == "model_run" for event in events), 1)
        self.assertEqual({path.name for path in ROOT.glob("onnxruntime_profile__*.json")}, root_profiles_before)
        (run_dir / "result.json").write_text(result.stdout, encoding="utf-8")
        (WORK / "profile-output-regression.json").write_text(json.dumps({
            "ok": True, "profile_path": path.relative_to(ROOT).as_posix(),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "node_execution_providers": sorted(providers), "model_runs": 1,
            "root_profile_files_unchanged": True, "full_model_suite_repeated": False
        }, indent=2) + "\n", encoding="utf-8")

    def test_production_files_unchanged(self):
        baseline = json.loads((WORK / "baseline.json").read_text())
        changed = [name for name, digest in baseline["production_sha256"].items()
                   if not (ROOT / name).is_file() or hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != digest]
        self.assertEqual(changed, [], f"Existing production/baseline files changed: {changed}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
