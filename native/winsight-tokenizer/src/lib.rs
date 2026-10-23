use std::{ffi::{c_char, CString}, panic::{catch_unwind, AssertUnwindSafe}, ptr, slice, str};
use tokenizers::Tokenizer;

fn error_string(message: &str) -> *mut c_char {
    CString::new(message.replace('\0', " ")).unwrap().into_raw()
}

fn boundary(error: *mut *mut c_char, action: impl FnOnce() -> Result<(), String>) -> i32 {
    if error.is_null() { return 1; }
    unsafe { *error = ptr::null_mut(); }
    match catch_unwind(AssertUnwindSafe(action)) {
        Ok(Ok(())) => 0,
        Ok(Err(message)) => { unsafe { *error = error_string(&message); } 1 }
        Err(_) => { unsafe { *error = error_string("Rust tokenizer panic"); } 2 }
    }
}

// All pointers are caller-owned valid memory for the declared lengths; output slots must not alias.
// No network feature is compiled in. Tokenizer JSON and input text are strictly UTF-8.
#[no_mangle]
pub unsafe extern "C" fn ws_tokenizer_new(json: *const u8, len: usize,
    handle: *mut *mut Tokenizer, error: *mut *mut c_char) -> i32 {
    if !handle.is_null() { *handle = ptr::null_mut(); }
    boundary(error, || {
        if json.is_null() || handle.is_null() || len == 0 || len > 64 * 1024 * 1024 {
            return Err("Invalid tokenizer JSON buffer".into());
        }
        let bytes = slice::from_raw_parts(json, len);
        str::from_utf8(bytes).map_err(|e| e.to_string())?;
        let tokenizer = Tokenizer::from_bytes(bytes).map_err(|e| e.to_string())?;
        *handle = Box::into_raw(Box::new(tokenizer));
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn ws_tokenizer_encode(handle: *const Tokenizer,
    text: *const u8, len: usize, special: u8, ids: *mut *mut u32,
    count: *mut usize, error: *mut *mut c_char) -> i32 {
    if !ids.is_null() { *ids = ptr::null_mut(); }
    if !count.is_null() { *count = 0; }
    boundary(error, || {
        if handle.is_null() || text.is_null() || ids.is_null() || count.is_null() || len > 1024 * 1024 || special > 1 {
            return Err("Invalid tokenizer encode arguments".into());
        }
        let text = str::from_utf8(slice::from_raw_parts(text, len)).map_err(|e| e.to_string())?;
        let encoding = (*handle).encode(text, special == 1).map_err(|e| e.to_string())?;
        let tokens = encoding.get_ids().to_vec().into_boxed_slice();
        *count = tokens.len();
        *ids = Box::into_raw(tokens) as *mut u32;
        Ok(())
    })
}

// Each returned buffer/handle is released exactly once by its matching function; never by C free().
#[no_mangle]
pub unsafe extern "C" fn ws_tokenizer_free(handle: *mut Tokenizer) {
    if !handle.is_null() { drop(Box::from_raw(handle)); }
}
#[no_mangle]
pub unsafe extern "C" fn ws_tokenizer_ids_free(ids: *mut u32, count: usize) {
    if !ids.is_null() { drop(Box::from_raw(ptr::slice_from_raw_parts_mut(ids, count))); }
}
#[no_mangle]
pub unsafe extern "C" fn ws_tokenizer_error_free(error: *mut c_char) {
    if !error.is_null() { drop(CString::from_raw(error)); }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn null_and_invalid_json_are_errors() {
        unsafe {
            let mut handle = ptr::null_mut(); let mut error = ptr::null_mut();
            assert_eq!(ws_tokenizer_new(ptr::null(), 0, &mut handle, &mut error), 1);
            assert!(handle.is_null()); assert!(!error.is_null()); ws_tokenizer_error_free(error);
            assert_eq!(ws_tokenizer_new(b"{}".as_ptr(), 2, &mut handle, &mut error), 1);
            assert!(handle.is_null()); ws_tokenizer_error_free(error);
        }
    }
    #[test]
    fn native_utf8_and_special_token_contract() {
        let path = std::env::var("WINSIGHT_TEST_TOKENIZER").expect("Set pinned tokenizer path");
        let json = std::fs::read(path).unwrap();
        unsafe {
            let mut handle = ptr::null_mut(); let mut error = ptr::null_mut();
            assert_eq!(ws_tokenizer_new(json.as_ptr(), json.len(), &mut handle, &mut error), 0);
            let mut ids = ptr::null_mut(); let mut count = 0;
            assert_eq!(ws_tokenizer_encode(handle, [0xff].as_ptr(), 1, 1, &mut ids, &mut count, &mut error), 1);
            assert!(ids.is_null()); assert_eq!(count, 0); ws_tokenizer_error_free(error);
            assert_eq!(ws_tokenizer_encode(handle, b"".as_ptr(), 0, 1, &mut ids, &mut count, &mut error), 0);
            assert_eq!(slice::from_raw_parts(ids, count), [2, 1]); ws_tokenizer_ids_free(ids, count);
            let text = "<|image>\u{4e2d}\u{6587}<|image|><image|>";
            assert_eq!(ws_tokenizer_encode(handle, text.as_ptr(), text.len(), 1, &mut ids, &mut count, &mut error), 0);
            let tokens = slice::from_raw_parts(ids, count);
            assert_eq!(tokens[0], 2); assert_eq!(tokens[tokens.len()-1], 1);
            assert!(tokens.contains(&255999)); assert!(tokens.contains(&258880)); assert!(tokens.contains(&258882));
            ws_tokenizer_ids_free(ids, count); ws_tokenizer_free(handle);
        }
    }
    #[test]
    fn panic_becomes_error() {
        let mut error = ptr::null_mut();
        assert_eq!(boundary(&mut error, || panic!("injected")), 2);
        unsafe { ws_tokenizer_error_free(error); }
    }
}
