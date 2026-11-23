"""Load a built arkham_core library and print its `info` answer.

Usage:  python3 tools/check-core-info.py /path/to/libarkham_core.dylib
Exit 0 only when coreSourceId == the b25aded6 target and snapshotVersion == 2.
Uses only the six exported C functions declared in engine/ffi/arkham_core.h.
"""
import ctypes, json, sys

TARGET = "arkham-core-sha256:b25aded6595c947497890ea4d3d63ea46792a465022a0e189cda5e0a40ea5cb2"

def main(path):
    lib = ctypes.CDLL(path)
    u64 = ctypes.c_uint64
    lib.ah_core_init.restype = ctypes.c_int
    lib.ah_core_call.argtypes = [ctypes.c_char_p, u64, ctypes.POINTER(u64)]
    lib.ah_core_result_size.argtypes = [u64, ctypes.POINTER(u64)]
    lib.ah_core_result_copy.argtypes = [u64, ctypes.c_char_p, u64]
    lib.ah_core_result_release.argtypes = [u64]
    rc = lib.ah_core_init()
    if rc != 0: raise SystemExit(f"ah_core_init -> {rc}")
    req = json.dumps({"apiVersion": 1, "method": "info"}).encode()
    handle = u64(0)
    rc = lib.ah_core_call(req, len(req), ctypes.byref(handle))
    if rc != 0: raise SystemExit(f"ah_core_call -> {rc}")
    size = u64(0)
    rc = lib.ah_core_result_size(handle, ctypes.byref(size))
    if rc != 0: raise SystemExit(f"ah_core_result_size -> {rc}")
    buf = ctypes.create_string_buffer(size.value)
    rc = lib.ah_core_result_copy(handle, buf, size.value)
    if rc != 0: raise SystemExit(f"ah_core_result_copy -> {rc}")
    lib.ah_core_result_release(handle)
    answer = json.loads(buf.raw[: size.value].decode("utf-8"))
    print(json.dumps(answer, ensure_ascii=False, indent=1))
    text = json.dumps(answer)
    ok = TARGET in text and '"snapshotVersion": 2' in text
    print("PASS" if ok else "FAIL: sourceId/snapshotVersion mismatch")
    # Do not call ah_core_shutdown/unload: the RTS cannot be restarted in-process.
    return 0 if ok else 1

if __name__ == "__main__":
    if len(sys.argv) != 2: raise SystemExit(__doc__)
    sys.exit(main(sys.argv[1]))
