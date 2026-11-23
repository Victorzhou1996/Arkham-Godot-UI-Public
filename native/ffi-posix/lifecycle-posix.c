/* Android/POSIX copy of engine/ffi/lifecycle.c (classic-fast 1.0.5). Same states, checks and RTS options; only the
   Windows SRWLOCK becomes a pthread rwlock. The FFI layer is not part of the rule sourceId.
   RTS signal handlers are not installed: the host process is a Mono/Godot app that owns its signals. */
#include <pthread.h>
#include <Rts.h>
#include "arkham_core.h"
#include "ArkhamFFI_stub.h"

static pthread_rwlock_t lifecycle_lock = PTHREAD_RWLOCK_INITIALIZER;
static int lifecycle_state = 0; /* uninitialized / ready / permanently closed */
static char program_name[] = "arkham-native";
static char *program_argv[] = { program_name, NULL };

__attribute__((visibility("default"))) int ah_core_init(void) {
    int result = AH_OK;
    pthread_rwlock_wrlock(&lifecycle_lock);
    if (lifecycle_state == 2) result = AH_CLOSED;
    else if (lifecycle_state == 0) {
        int argc = 1;
        char **args = program_argv;
        RtsConfig config = defaultRtsConfig;
        config.rts_opts = "-A16m --install-signal-handlers=no";
        hs_init_ghc(&argc, &args, config);
        lifecycle_state = 1;
    }
    pthread_rwlock_unlock(&lifecycle_lock);
    return result;
}

__attribute__((visibility("default"))) int ah_core_call(const uint8_t *input, uint64_t length, uint64_t *result) {
    int status;
    if (!result) return AH_ARGUMENT;
    *result = 0;
    if ((!input && length) || length > 64ULL*1024*1024) return AH_ARGUMENT;
    pthread_rwlock_rdlock(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_call((HsPtr)input, length, (HsPtr)result) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    pthread_rwlock_unlock(&lifecycle_lock);
    return status;
}

__attribute__((visibility("default"))) int ah_core_result_size(uint64_t result, uint64_t *length) {
    int status;
    if (!length) return AH_ARGUMENT;
    *length = 0;
    pthread_rwlock_rdlock(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_size(result,(HsPtr)length) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    pthread_rwlock_unlock(&lifecycle_lock);
    return status;
}

__attribute__((visibility("default"))) int ah_core_result_copy(uint64_t result, uint8_t *destination, uint64_t capacity) {
    int status;
    if (!destination) return AH_ARGUMENT;
    pthread_rwlock_rdlock(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_copy(result,(HsPtr)destination,capacity) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    pthread_rwlock_unlock(&lifecycle_lock);
    return status;
}

__attribute__((visibility("default"))) int ah_core_result_release(uint64_t result) {
    int status;
    pthread_rwlock_rdlock(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_release(result) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    pthread_rwlock_unlock(&lifecycle_lock);
    return status;
}

__attribute__((visibility("default"))) int ah_core_shutdown(void) {
    int status;
    uint64_t live = 0;
    pthread_rwlock_wrlock(&lifecycle_lock);
    if (lifecycle_state != 1) status = lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY;
    else {
        status = ah_hs_live_count((HsPtr)&live);
        if (status == AH_OK && live != 0) status = AH_BUSY;
        else if (status == AH_OK) { hs_exit(); lifecycle_state = 2; }
    }
    pthread_rwlock_unlock(&lifecycle_lock);
    return status;
}
