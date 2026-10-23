# WinSight native tokenizer

ABI v1 is declared in `winsight_tokenizer.h`. Implementation uses exactly Rust `tokenizers = 0.23.1`, with `onig`, no HTTP feature and a retained Cargo.lock. JSON bytes are supplied by the C# caller after pinned-manifest validation; native code does not choose or download model files. Encoding delegates normalization, added tokens, BOS/EOS and postprocessing to that JSON. No C# tokenizer approximation exists.

The caller owns valid input/output-pointer memory. Returned handles, token-ID slices and UTF8 error strings must each be released once through their matching exported free function. Encoding failure initializes outputs to null/zero and returns 1; Rust panic is caught and returns 2 with an allocated error string. OOM/native pointer misuse is process-fatal, not a successful empty token list. C# wraps the handle in SafeHandle and releases every returned buffer in finally.

The intended native build command is `cargo build --release --offline --locked --manifest-path native/winsight-tokenizer/Cargo.toml`. It already ran before the source-only steering. Do not rerun it under the current no-runtime-validation scope. Packaging copies the resulting DLL to the host's private `runtimes/win-x64/native/`; P/Invoke loads that exact path, without PATH or system fallback. Build output is ignored, not source-controlled.

Dependency licenses, full notices and vulnerability review remain a packaging gate. tokenizers is Apache-2.0; no complete transitive supply-chain audit was executed in this lane.
