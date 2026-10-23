#ifndef WINSIGHT_TOKENIZER_H
#define WINSIGHT_TOKENIZER_H
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ABI v1, cdecl. Buffers are UTF8, lengths in bytes; special is 0 or 1.
 * Return: 0 success, 1 input/tokenizer error, 2 caught Rust panic.
 * Output slots must be valid and non-overlapping. Failed calls zero their outputs.
 * Error strings, ID slices and tokenizer handles are Rust-owned until matching free.
 * Each result is freed exactly once, never using C free(). No parallel encode/free.
 */
int32_t ws_tokenizer_new(const uint8_t *json, size_t len, void **handle, char **error);
int32_t ws_tokenizer_encode(const void *handle, const uint8_t *text, size_t len,
    uint8_t special, uint32_t **ids, size_t *count, char **error);
void ws_tokenizer_free(void *handle);
void ws_tokenizer_ids_free(uint32_t *ids, size_t count);
void ws_tokenizer_error_free(char *error);
#ifdef __cplusplus
}
#endif
#endif
