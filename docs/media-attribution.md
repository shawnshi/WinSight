# Experimental media preprocessing: provenance and release gate

This source-only integration has **not** demonstrated numerical equivalence or passed model/runtime acceptance. The subsequent source cutover makes Windows ML the default and stages it for packaging; neither change establishes runtime or redistribution acceptance. See windowsml-cutover.md.

## References consulted

- Transformers.js npm 4.3.1, `gemma4` audio/image and `embedding_gemma2` processing. Separate source snapshot commit: `511bb61a7c46b73e33f1af3fb308fbd1afe98c02`, https://github.com/huggingface/transformers.js/tree/511bb61a7c46b73e33f1af3fb308fbd1afe98c02 . The npm release and snapshot are distinct provenance inputs, not assumed byte-identical. Upstream code is Apache-2.0; retain applicable notices if redistributed/adapted.
- sharp 0.35.5 / libvips 8.18.7, bicubic affine resizing. libvips snapshot `24ad4d042940e6bf99a68871ba886ca8847c9c82`: https://github.com/libvips/libvips/tree/24ad4d042940e6bf99a68871ba886ca8847c9c82/libvips/resample . Its source is LGPL-2.1-or-later. The fixed 64-phase Q12 numerical rules were consulted for `MediaBicubic.cs`; this was not a clean-room exercise or legal clearance. Do not infer that the repository's MIT license resolves the consulted-source obligations.
- Windows SDK 10.0.26100 WIC COM ABI for decode/orientation. WIC decoding is separate from the managed resizing implementation and has not been shown identical to sharp decoding.
- Rust tokenizers 0.23.1, Apache-2.0, plus the exact transitive Cargo lock. Native buffers and opaque handles remain owned/freed by Rust. Transitive license/notice and VC runtime closure remain open.
- Microsoft.WindowsAppSDK.ML 2.1.94 and its locked dependencies have their own redistribution terms; the repository's license does not replace them.
- Model weights are not included in the new source integration or development build script. Their model-card/license terms require separate distribution review.

## Before any distribution

Review the actual code/source correspondence, preserve required copyright/license/NOTICE material, establish any LGPL source/relink obligations, and complete native/SDK/runtime/model dependency closure. No independent license review or release approval is claimed here. The development host builder is not an installer, package validation or permission to publish.
