#ifndef WINSIGHT_SEGMENTER_H
#define WINSIGHT_SEGMENTER_H

#include <stddef.h>
#include <stdint.h>



/* WinSight Chinese Segmenter ABI v1, cdecl.
 * All text and JSON buffers are UTF-8 encoded; lengths in bytes.
 * Return codes:
 *   0: Success
 *   1: Null pointer, invalid UTF-8, or input length >= 1 MiB
 *   2: Dictionary loading failed (invalid JSON or inaccessible base file)
 *   3: Internal error (caught panic)
 *
 * All handles and token buffers returned by native functions must be freed
 * by their matching free functions (`ws_segment_free`, `ws_segment_free_tokens`).
 * Never use C standard free().
 */

/**
 * Creates a new segmenter instance from a JSON configuration string.
 * Format: `{"base":null,"user":["词1","词2"]}` (null base uses built-in default dictionary).
 * Returns 0 on success and sets *handle_out. On failure, returns non-zero and sets *handle_out to NULL.
 */
int32_t ws_segment_new(const uint8_t *dict_json_utf8, size_t len, void **handle_out);

/**
 * Segments input text into newline-separated (`\n`), deduplicated, order-stable tokens.
 * Retains original case for ASCII and numbers.
 * Inputs >= 1 MiB (1048576 bytes) are rejected with return code 1.
 * Returns 0 on success and sets *out_tokens_utf8 and *out_len.
 */
int32_t ws_segment_text(const void *handle, const uint8_t *text_utf8, size_t len,
                        uint8_t **out_tokens_utf8, size_t *out_len);

/**
 * Frees a tokens buffer allocated by `ws_segment_text`.
 */
void ws_segment_free_tokens(uint8_t *ptr, size_t len);

/**
 * Frees a segmenter instance allocated by `ws_segment_new`.
 */
void ws_segment_free(void *handle);



#endif /* WINSIGHT_SEGMENTER_H */
