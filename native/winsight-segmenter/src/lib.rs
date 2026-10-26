use jieba_rs::Jieba;
use serde::Deserialize;
use std::{
    ffi::c_void,
    panic::{catch_unwind, AssertUnwindSafe},
    ptr, slice, str,
};

pub const MAX_SEGMENT_INPUT_LEN: usize = 1024 * 1024; // 1 MiB limit

#[derive(Deserialize, Default)]
struct DictConfig {
    #[serde(default)]
    base: Option<String>,
    #[serde(default)]
    user: Option<Vec<String>>,
}

pub struct Segmenter {
    jieba: Jieba,
}

unsafe impl Send for Segmenter {}
unsafe impl Sync for Segmenter {}

impl Segmenter {
    pub fn new(dict_json: &str) -> Result<Self, i32> {
        let config: DictConfig = serde_json::from_str(dict_json).map_err(|_| 2)?;

        let mut jieba = match config.base {
            None => Jieba::new(),
            Some(base_path) => {
                let file = std::fs::File::open(&base_path).map_err(|_| 2)?;
                let mut reader = std::io::BufReader::new(file);
                Jieba::with_dict(&mut reader).map_err(|_| 2)?
            }
        };

        if let Some(user_words) = config.user {
            for word in user_words {
                jieba.add_word(&word, None, None);
            }
        }

        Ok(Self { jieba })
    }

    pub fn segment(&self, text: &str) -> String {
        if text.is_empty() {
            return String::new();
        }

        let raw_tokens = self.jieba.cut(text, false);
        let mut seen = std::collections::HashSet::new();
        let mut ordered_tokens = Vec::new();

        for token in raw_tokens {
            let word = token.word;
            // Keep tokens containing at least one alphanumeric or CJK ideographic character;
            // discard pure punctuation and whitespace.
            if !word.chars().any(|c| c.is_alphanumeric()) {
                continue;
            }
            if seen.insert(word) {
                ordered_tokens.push(word);
            }
        }

        ordered_tokens.join("\n")
    }
}

fn boundary<F, R>(action: F, fallback: R) -> R
where
    F: FnOnce() -> R + std::panic::UnwindSafe,
{
    match catch_unwind(action) {
        Ok(res) => res,
        Err(_) => fallback,
    }
}

/// Create a new segmenter instance from a JSON configuration string.
/// Format: `{"base":null,"user":["词1","词2"]}` (null base uses built-in default dictionary).
/// Return codes:
///   0: Success
///   1: Null pointer or invalid UTF-8
///   2: Dictionary loading failed (invalid JSON or inaccessible base file)
///   3: Internal error (caught panic)
#[no_mangle]
pub unsafe extern "C" fn ws_segment_new(
    dict_json_utf8: *const u8,
    len: usize,
    handle_out: *mut *mut c_void,
) -> i32 {
    if handle_out.is_null() {
        return 1;
    }
    *handle_out = ptr::null_mut();

    if dict_json_utf8.is_null() {
        return 1;
    }

    boundary(
        AssertUnwindSafe(|| {
            let slice = slice::from_raw_parts(dict_json_utf8, len);
            let json_str = match str::from_utf8(slice) {
                Ok(s) => s,
                Err(_) => return 1,
            };

            match Segmenter::new(json_str) {
                Ok(segmenter) => {
                    *handle_out = Box::into_raw(Box::new(segmenter)) as *mut c_void;
                    0
                }
                Err(code) => code,
            }
        }),
        3,
    )
}

/// Segment text into newline-separated, deduplicated, order-stable tokens.
/// Rejects inputs >= 1 MiB.
/// Return codes:
///   0: Success
///   1: Null pointer, invalid UTF-8, or length >= 1 MiB
///   2: Dictionary loading failed (not applicable here)
///   3: Internal error (caught panic)
#[no_mangle]
pub unsafe extern "C" fn ws_segment_text(
    handle: *const c_void,
    text_utf8: *const u8,
    len: usize,
    out_tokens_utf8: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    if !out_tokens_utf8.is_null() {
        *out_tokens_utf8 = ptr::null_mut();
    }
    if !out_len.is_null() {
        *out_len = 0;
    }

    if handle.is_null() || out_tokens_utf8.is_null() || out_len.is_null() || text_utf8.is_null() {
        return 1;
    }
    if len >= MAX_SEGMENT_INPUT_LEN {
        return 1;
    }

    boundary(
        AssertUnwindSafe(|| {
            let slice = if len == 0 {
                &[]
            } else {
                slice::from_raw_parts(text_utf8, len)
            };

            let text = match str::from_utf8(slice) {
                Ok(s) => s,
                Err(_) => return 1,
            };

            let segmenter = &*(handle as *const Segmenter);
            let joined = segmenter.segment(text);

            let bytes = joined.into_bytes().into_boxed_slice();
            *out_len = bytes.len();
            *out_tokens_utf8 = Box::into_raw(bytes) as *mut u8;
            0
        }),
        3,
    )
}

/// Free a tokens buffer returned by `ws_segment_text`.
#[no_mangle]
pub unsafe extern "C" fn ws_segment_free_tokens(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(Box::from_raw(ptr::slice_from_raw_parts_mut(ptr, len)));
    }
}

/// Free a segmenter instance returned by `ws_segment_new`.
#[no_mangle]
pub unsafe extern "C" fn ws_segment_free(handle: *mut c_void) {
    if !handle.is_null() {
        drop(Box::from_raw(handle as *mut Segmenter));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_ffi_null_and_invalid_utf8_checks() {
        unsafe {
            let mut handle: *mut c_void = ptr::null_mut();

            // Null handle_out
            assert_eq!(ws_segment_new(b"{}".as_ptr(), 2, ptr::null_mut()), 1);

            // Null json ptr
            assert_eq!(ws_segment_new(ptr::null(), 0, &mut handle), 1);
            assert!(handle.is_null());

            // Invalid UTF-8 json
            let invalid_utf8 = [0xff, 0xfe];
            assert_eq!(
                ws_segment_new(invalid_utf8.as_ptr(), invalid_utf8.len(), &mut handle),
                1
            );
            assert!(handle.is_null());

            // Malformed JSON (code 2)
            let malformed = b"not a json";
            assert_eq!(
                ws_segment_new(malformed.as_ptr(), malformed.len(), &mut handle),
                2
            );
            assert!(handle.is_null());
        }
    }

    #[test]
    fn test_ffi_oversize_input_rejected() {
        unsafe {
            let dict_json = b"{\"base\":null,\"user\":[]}";
            let mut handle: *mut c_void = ptr::null_mut();
            assert_eq!(
                ws_segment_new(dict_json.as_ptr(), dict_json.len(), &mut handle),
                0
            );
            assert!(!handle.is_null());

            let mut out_tokens: *mut u8 = ptr::null_mut();
            let mut out_len: usize = 0;

            // Null text ptr -> 1
            assert_eq!(
                ws_segment_text(handle, ptr::null(), 10, &mut out_tokens, &mut out_len),
                1
            );

            // Exactly 1 MiB -> rejected (1)
            let oversize = vec![b'a'; MAX_SEGMENT_INPUT_LEN];
            assert_eq!(
                ws_segment_text(
                    handle,
                    oversize.as_ptr(),
                    oversize.len(),
                    &mut out_tokens,
                    &mut out_len
                ),
                1
            );
            assert!(out_tokens.is_null());
            assert_eq!(out_len, 0);

            // Over 1 MiB -> rejected (1)
            let way_oversize = vec![b'a'; MAX_SEGMENT_INPUT_LEN + 1];
            assert_eq!(
                ws_segment_text(
                    handle,
                    way_oversize.as_ptr(),
                    way_oversize.len(),
                    &mut out_tokens,
                    &mut out_len
                ),
                1
            );

            ws_segment_free(handle);
        }
    }

    #[test]
    fn test_ffi_segmentation_cases() {
        unsafe {
            let dict_json = r#"{"base":null,"user":["电子病历五级"]}"#;
            let mut handle: *mut c_void = ptr::null_mut();
            assert_eq!(
                ws_segment_new(dict_json.as_ptr(), dict_json.len(), &mut handle),
                0
            );
            assert!(!handle.is_null());

            let run_segment = |text: &str| -> String {
                let mut out_tokens: *mut u8 = ptr::null_mut();
                let mut out_len: usize = 0;
                let code = ws_segment_text(
                    handle,
                    text.as_ptr(),
                    text.len(),
                    &mut out_tokens,
                    &mut out_len,
                );
                assert_eq!(code, 0);
                let result = if out_len > 0 && !out_tokens.is_null() {
                    let s = slice::from_raw_parts(out_tokens, out_len);
                    str::from_utf8(s).unwrap().to_string()
                } else {
                    String::new()
                };
                ws_segment_free_tokens(out_tokens, out_len);
                result
            };

            // 1. "医保/合同/报销"
            let res1 = run_segment("医保/合同/报销");
            assert_eq!(res1, "医保\n合同\n报销");

            // 2. Product term with user dict: "电子病历五级"
            let res2 = run_segment("电子病历五级");
            assert_eq!(res2, "电子病历五级");

            // 3. Mixed Chinese/English: "DRG/DIP 结算"
            let res3 = run_segment("DRG/DIP 结算");
            assert_eq!(res3, "DRG\nDIP\n结算");

            // 4. Error code: "E1001"
            let res4 = run_segment("E1001");
            assert_eq!(res4, "E1001");

            // 5. Quotes and punctuation: "“测试”：‘你好’，世界！"
            let res5 = run_segment("“测试”：‘你好’，世界！");
            assert_eq!(res5, "测试\n你好\n世界");

            // 6. Empty string
            let res6 = run_segment("");
            assert_eq!(res6, "");

            // 7. Deduplication & order stability
            let res7 = run_segment("医保/结算/医保/结算/合同");
            assert_eq!(res7, "医保\n结算\n合同");

            // 8. Twice invocation consistency
            let pass1 = run_segment("DRG/DIP 结算 与 电子病历五级 医保 报销");
            let pass2 = run_segment("DRG/DIP 结算 与 电子病历五级 医保 报销");
            assert_eq!(pass1, pass2);

            // 9. Invalid UTF-8 in text -> 1
            let mut out_tokens: *mut u8 = ptr::null_mut();
            let mut out_len: usize = 0;
            let invalid_text = [0xff, 0xfe];
            assert_eq!(
                ws_segment_text(
                    handle,
                    invalid_text.as_ptr(),
                    invalid_text.len(),
                    &mut out_tokens,
                    &mut out_len
                ),
                1
            );
            assert!(out_tokens.is_null());
            assert_eq!(out_len, 0);

            ws_segment_free(handle);
        }
    }

    #[test]
    fn test_nonexistent_base_dict_fails_with_code_2() {
        unsafe {
            let mut handle: *mut c_void = ptr::null_mut();
            let dict_json = br#"{"base":"nonexistent_dict_path.txt","user":[]}"#;
            assert_eq!(
                ws_segment_new(dict_json.as_ptr(), dict_json.len(), &mut handle),
                2
            );
            assert!(handle.is_null());
        }
    }

    #[test]
    fn test_boundary_catches_panic_and_returns_3() {
        let code = boundary(|| panic!("simulated panic"), 3);
        assert_eq!(code, 3);
    }
}
