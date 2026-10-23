#!/usr/bin/env python3
"""Fetch only the pinned public P0 artifacts through huggingface_hub, without using credentials."""
import argparse
import hashlib
import json
import os
from pathlib import Path

from huggingface_hub import hf_hub_download

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / ".dev/onnx-p0"
MANIFEST = ROOT / "docs/onnx-p0/model-artifacts.json"
REVISION = "daa72c51243991dfcaf9f9137d2c573d8f7790c0"
REQUIRED = {"README.md", "config.json", "tokenizer.json", "tokenizer_config.json",
            "processor_config.json", "preprocessor_config.json"} | {
    f"onnx/{name}{suffix}" for name in ("model_q4", "vision_encoder_q4", "audio_encoder_q4")
    for suffix in (".onnx", ".onnx_data")
}


def validate_manifest(manifest):
    if manifest["repository"] != "onnx-community/embeddinggemma-2-ONNX" or manifest["revision"] != REVISION:
        raise ValueError("P0 manifest repository or revision is not pinned")
    paths = [entry["path"] for entry in manifest["files"]]
    if len(paths) != len(REQUIRED) or set(paths) != REQUIRED:
        raise ValueError("P0 requires exactly the allowlisted text, vision, audio and tokenizer files")
    if manifest["modalities"] != {"text": True, "vision": True, "audio": True}:
        raise ValueError("P0 must retain audio as well as text and vision")


def verify(path, entry):
    if path.stat().st_size != entry["size"]:
        raise ValueError(f"Artifact size mismatch: {entry['path']}")
    sha256 = hashlib.sha256()
    # SHA1 here is Git's object identifier, not a new security primitive; record SHA256 for every file as well.
    git_blob = hashlib.sha1(f"blob {entry['size']}\0".encode(), usedforsecurity=False)
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            sha256.update(block)
            git_blob.update(block)
    digest = sha256.hexdigest()
    if entry["sha256"]:
        if digest != entry["sha256"]:
            raise ValueError(f"Artifact SHA256 mismatch: {entry['path']}")
    elif git_blob.hexdigest() != entry["gitBlobSha1"]:
        raise ValueError(f"Pinned Git blob mismatch: {entry['path']}")
    return {"path": entry["path"], "size": entry["size"], "sha256": digest, "verified": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("group", choices=("text", "vision", "audio"))
    args = parser.parse_args()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    validate_manifest(manifest)
    prefixes = {"text": "onnx/model_q4", "vision": "onnx/vision_encoder_q4", "audio": "onnx/audio_encoder_q4"}
    entries = [entry for entry in manifest["files"]
               if entry["path"].startswith(prefixes[args.group])
               or (args.group == "text" and not entry["path"].startswith("onnx/"))]
    output = WORK / "models"
    if not output.resolve().is_relative_to(WORK.resolve()):
        raise ValueError("Model output resolves outside the P0 workspace")
    receipt = WORK / "artifact-verification.json"
    records = json.loads(receipt.read_text()) if receipt.exists() else {}
    for entry in entries:
        destination = output / entry["path"]
        if not destination.exists():
            hf_hub_download(repo_id=manifest["repository"], filename=entry["path"],
                            revision=manifest["revision"], token=False, local_dir=output,
                            endpoint="https://huggingface.co", cache_dir=WORK / "hf-cache")
        records[entry["path"]] = verify(destination, entry)
        temporary = receipt.with_suffix(".tmp.json")
        temporary.write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, receipt)
        print(f"verified {entry['path']} ({entry['size']} bytes)", flush=True)


if __name__ == "__main__":
    main()
