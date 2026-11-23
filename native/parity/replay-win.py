"""Replay recorded core requests (one JSON per line) on the Windows arkham_core.dll; print one answer per line.
Same six exported functions as core-cli.c on Android, so both outputs can be compared line by line.
usage: python replay-win.py <arkham_core.dll> <requests.jsonl> > answers.jsonl"""
import ctypes, sys

dll, reqs = sys.argv[1], sys.argv[2]
lib = ctypes.CDLL(dll)
u64 = ctypes.c_uint64
lib.ah_core_call.argtypes = [ctypes.c_char_p, u64, ctypes.POINTER(u64)]
lib.ah_core_result_size.argtypes = [u64, ctypes.POINTER(u64)]
lib.ah_core_result_copy.argtypes = [u64, ctypes.c_char_p, u64]
lib.ah_core_result_release.argtypes = [u64]
assert lib.ah_core_init() == 0
out = sys.stdout.buffer
for line in open(reqs, "rb"):
    line = line.rstrip(b"\r\n")
    if not line:
        continue
    h = u64(0)
    rc = lib.ah_core_call(line, len(line), ctypes.byref(h))
    if rc:
        out.write(b'{"cliError":"call %d"}\n' % rc); continue
    n = u64(0)
    lib.ah_core_result_size(h, ctypes.byref(n))
    buf = ctypes.create_string_buffer(n.value)
    lib.ah_core_result_copy(h, buf, n.value)
    lib.ah_core_result_release(h)
    out.write(buf.raw[: n.value] + b"\n")
    out.flush()
