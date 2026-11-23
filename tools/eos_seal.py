"""Seal the EOS client config into app/online/eos.dat for shipped builds (Yunke 11-22: "用1+2").

Reads ~/.arkham/eos-config.local.json, writes the sealed copy that EosConfig.cs opens at run time. Prints no value
from the file: only whether it is complete, the sealed size and a round-trip check. Obfuscation only; the key's real
protection is the Client Policy on the Epic developer portal. Keep the layout in step with EosConfig.Unseal.
Run:  python -I tools/eos_seal.py <out file>
"""
import hashlib
import hmac
import json
import os
import secrets
import struct
import sys

A = bytes([0x9A, 0x55, 0x67, 0xC8, 0xF3, 0x06, 0x1E, 0x83, 0x87, 0x72, 0xFA, 0x26, 0x8A, 0x2D, 0x28, 0xB7])
B = bytes([0x09, 0x41, 0x16, 0xC9, 0x97, 0xA3, 0xF7, 0xD2, 0x08, 0x11, 0x79, 0x94, 0xE8, 0x1C, 0x5A, 0x95])
C = bytes([0xF1, 0xF0, 0x2E, 0xB5, 0xDB, 0x10, 0x53, 0x44, 0xEE, 0x4D, 0xB9, 0xB7, 0xF1, 0xBD, 0xBA, 0x3E])
KEYS = ("productId", "sandboxId", "deploymentId", "clientId", "clientSecret")


def stream(key, n):
    out, i = bytearray(), 0
    while len(out) < n:
        out += hashlib.sha256(key + struct.pack("<i", i)).digest()
        i += 1
    return bytes(out[:n])


def seal(plain):
    salt = secrets.token_bytes(16)
    key = hashlib.sha256(salt + A + B + C).digest()
    cipher = bytes(p ^ k for p, k in zip(plain, stream(key, len(plain))))
    tag = hmac.new(key, cipher, hashlib.sha256).digest()[:16]
    return b"AHE1" + salt + tag + cipher


def unseal(blob):
    salt, tag, cipher = blob[4:20], blob[20:36], blob[36:]
    key = hashlib.sha256(salt + A + B + C).digest()
    assert hmac.compare_digest(hmac.new(key, cipher, hashlib.sha256).digest()[:16], tag)
    return bytes(c ^ k for c, k in zip(cipher, stream(key, len(cipher))))


def main():
    out = sys.argv[1]
    src = os.path.join(os.path.expanduser("~"), ".arkham", "eos-config.local.json")
    with open(src, "rb") as f:
        plain = f.read()
    doc = json.loads(plain)
    missing = [k for k in KEYS if not isinstance(doc.get(k), str) or not doc.get(k)]
    if missing:
        print("incomplete config, missing fields:", ", ".join(missing))
        sys.exit(1)
    # only the five fields the game reads go into the package
    slim = json.dumps({k: doc[k] for k in KEYS}, separators=(",", ":")).encode("utf-8")
    blob = seal(slim)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "wb") as f:
        f.write(blob)
    ok = unseal(open(out, "rb").read()) == slim
    print(f"sealed {len(blob)} bytes -> {out}; round-trip {'ok' if ok else 'FAILED'}; plain text in file: {slim[:12] in blob}")


if __name__ == "__main__":
    main()
