<!-- Link the agreed issue/scope. Report source-only checks separately from native build, model and release acceptance. -->

Issue: #

What changed and why:

Native source/tokenizer checks run (PowerShell, repository root; prepare the dependencies in `docs/windows-development.md` first):

```powershell
python scripts/check-windows-source.py
$env:WINSIGHT_TEST_TOKENIZER = (Resolve-Path -LiteralPath 'desktop/tests/Fixtures/Tokenizer/abi-tokenizer.json').Path
cargo test --locked --manifest-path native/winsight-tokenizer/Cargo.toml
```

Applicable .NET build/regression results, including skipped or blocked checks (the above checks do not establish native UI/model/installation acceptance):

If search behavior changes: reproducible before/after evidence and fixture scope; identify real-model qualification separately:
