# Preserved reference inputs

Nine binary media fixtures were moved byte-for-byte from the retired root Swift tests. [provenance.json](provenance.json) records original paths, source commit, byte lengths and SHA-256 values.

- `Multimodal/`: photo, screenshot and short speech input.
- `Media/Pictures/`: orientation/format samples.
- `Media/Videos/`: the existing transport-stream sample.

These are retained reference inputs, not private user files or newly downloaded assets. Preservation alone does not prove native format support, OCR quality, model equivalence or all legacy test expectations. Current native regression executables keep their existing assertions; the old Swift tests were retired rather than silently counted as passing C# tests.

`Tokenizer/abi-tokenizer.json` is an additional tiny, synthetic WordLevel fixture for Rust ABI/UTF-8/special-token unit tests in CI. It is not a model download, a production tokenizer or evidence of parity with the pinned model vocabulary. Production still requires the separate SHA-256-pinned model manifest. The original Rust test assertions are unchanged; explicit model-tokenizer validation is recorded separately.

Do not scan user photo libraries or replace these inputs with real data during validation.
