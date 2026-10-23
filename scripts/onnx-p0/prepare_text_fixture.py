#!/usr/bin/env python3
"""Generate synthetic token-ID probe inputs with the pinned HF tokenizer (development only)."""
import hashlib
import json
from pathlib import Path

from tokenizers import Tokenizer

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / ".dev/onnx-p0"


def main():
    manifest = json.loads((ROOT / "docs/onnx-p0/model-artifacts.json").read_text())
    tokenizer_path = WORK / "models/tokenizer.json"
    expected = next(item["sha256"] for item in manifest["files"] if item["path"] == "tokenizer.json")
    actual = hashlib.sha256(tokenizer_path.read_bytes()).hexdigest()
    if actual != expected:
        raise ValueError("Pinned tokenizer SHA256 mismatch")
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    samples = [
        ("query-zh", "task: search result | query: 如何在本地查找PDF文档？"),
        ("document-zh", "title: none | text: 本地文件检索支持查找PDF文档、图片和录音。"),
        ("query-code", "task: code retrieval | query: retry a request with exponential backoff"),
    ]
    items = [{"name": name, "text": text, "input_ids": tokenizer.encode(text, add_special_tokens=True).ids}
             for name, text in samples]
    fixture = {"repository": manifest["repository"], "revision": manifest["revision"],
               "tokenizer_sha256": actual, "stage": "P0 token-ID smoke; not production tokenizer acceptance",
               "items": items}
    (WORK / "text-fixture.json").write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("Synthetic token inputs:", [(item["name"], len(item["input_ids"])) for item in items])


if __name__ == "__main__":
    main()
