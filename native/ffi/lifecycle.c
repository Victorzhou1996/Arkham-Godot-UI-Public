#include <windows.h>
#include <Rts.h>
#include "arkham_core.h"
#include "ArkhamFFI_stub.h"

static SRWLOCK lifecycle_lock = SRWLOCK_INIT;
static int lifecycle_state = 0; /* uninitialized / ready / permanently closed */
static char program_name[] = "arkham-native";
static char *program_argv[] = { program_name, NULL };

int ah_core_init(void) {
    int result = AH_OK;
    AcquireSRWLockExclusive(&lifecycle_lock);
    if (lifecycle_state == 2) result = AH_CLOSED;
    else if (lifecycle_state == 0) {
        int argc = 1;
        char **args = program_argv;
        /* Test a modest nursery for the many short-lived UI projection objects.
           Rule objects, capabilities, limits and Save encoding stay unchanged. */
        RtsConfig config = defaultRtsConfig;
        config.rts_opts = "-A16m";
        hs_init_ghc(&argc, &args, config);
        lifecycle_state = 1;
    }
    ReleaseSRWLockExclusive(&lifecycle_lock);
    return result;
}

int ah_core_call(const uint8_t *input, uint64_t length, uint64_t *result) {
    int status;
    if (!result) return AH_ARGUMENT;
    *result = 0;
    if ((!input && length) || length > 64ULL*1024*1024) return AH_ARGUMENT;
    AcquireSRWLockShared(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_call((HsPtr)input, length, (HsPtr)result) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    ReleaseSRWLockShared(&lifecycle_lock);
    return status;
}

int ah_core_result_size(uint64_t result, uint64_t *length) {
    int status;
    if (!length) return AH_ARGUMENT;
    *length = 0;
    AcquireSRWLockShared(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_size(result,(HsPtr)length) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    ReleaseSRWLockShared(&lifecycle_lock);
    return status;
}

int ah_core_result_copy(uint64_t result, uint8_t *destination, uint64_t capacity) {
    int status;
    if (!destination) return AH_ARGUMENT;
    AcquireSRWLockShared(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_copy(result,(HsPtr)destination,capacity) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    ReleaseSRWLockShared(&lifecycle_lock);
    return status;
}

int ah_core_result_release(uint64_t result) {
    int status;
    AcquireSRWLockShared(&lifecycle_lock);
    status = lifecycle_state == 1 ? ah_hs_result_release(result) : (lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY);
    ReleaseSRWLockShared(&lifecycle_lock);
    return status;
}

int ah_core_shutdown(void) {
    int status;
    uint64_t live = 0;
    AcquireSRWLockExclusive(&lifecycle_lock);
    if (lifecycle_state != 1) status = lifecycle_state == 2 ? AH_CLOSED : AH_NOT_READY;
    else {
        status = ah_hs_live_count((HsPtr)&live);
        if (status == AH_OK && live != 0) status = AH_BUSY;
        else if (status == AH_OK) { hs_exit(); lifecycle_state = 2; }
    }
    ReleaseSRWLockExclusive(&lifecycle_lock);
    return status;
}
