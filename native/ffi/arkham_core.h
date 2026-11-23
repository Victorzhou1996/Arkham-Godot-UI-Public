#ifndef ARKHAM_CORE_H
#define ARKHAM_CORE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
enum { AH_OK=0, AH_ARGUMENT=1, AH_NOT_READY=2, AH_NOT_FOUND=3, AH_INTERNAL=4, AH_LIMIT=5, AH_BUSY=6, AH_CLOSED=7 };
int ah_core_init(void);
int ah_core_call(const uint8_t *utf8, uint64_t length, uint64_t *result);
int ah_core_result_size(uint64_t result, uint64_t *length);
int ah_core_result_copy(uint64_t result, uint8_t *destination, uint64_t capacity);
int ah_core_result_release(uint64_t result);
int ah_core_shutdown(void);
/* Handles never expose StablePtr/native object addresses. Results are copied
   into caller-owned memory, then explicitly released with the originating DLL.
   NULL/bounds are checked; arbitrary invalid native addresses are not a sandbox.
   Keep DLL loaded for the process lifetime. No hs_exit -> hs_init restart. */
#ifdef __cplusplus
}
#endif
#endif
