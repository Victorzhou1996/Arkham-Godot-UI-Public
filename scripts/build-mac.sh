#!/usr/bin/env bash
# DRAFT - NOT VERIFIED ON macOS. Mirrors native/ffi build-dll.ps1 (Windows) and the Android build that works
# (G:\AHMobile-20261122 scripts/link-core.sh, 11-23): rules b25aded6, GIT_SHA1 fixed, -O0, POSIX lifecycle, ported host.
# Produces native/mac-build/libarkham_core.dylib and a NativeHost build.
# Expect to fix flags on first real run; read each step's output.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BACKEND="$ROOT/native/backend"
FFI="$ROOT/native/ffi"
HOST="$ROOT/native/host"
OUT="$ROOT/native/mac-build"

# Same packages as build-dll.ps1
PKGS=(-package base -package deepseq -package arkham-core -package aeson -package bytestring -package text \
      -package containers -package uuid -package mtl -package transformers)

command -v stack >/dev/null || { echo "Install stack first: brew install haskell-stack"; exit 1; }
command -v dotnet >/dev/null || { echo "Install .NET 8 SDK first"; exit 1; }

mkdir -p "$OUT"

echo "== 1. Haskell core library and card generator"
cd "$BACKEND"
# Arkham/Git.hs embeds this at compile time; the identity definition expects exactly this value
export GIT_SHA1=f6ed09bec346ebc505ade739d34a7aea3305fa5c
stack --no-terminal build arkham-core:lib cards-discover:exe:cards-discover

echo "== 2. FFI objects (ArkhamFFI.hs + lifecycle.c)"
stack --no-terminal exec -- ghc --make -no-link -O0 -XGHC2021 \
    -odir "$OUT" -hidir "$OUT" -stubdir "$OUT" "${PKGS[@]}" "$FFI/ArkhamFFI.hs"
# lifecycle.c is Windows-only (SRWLOCK); the POSIX version (pthread rwlock, RTS without signal handlers) built the Android core
cc -c "$ROOT/native/ffi-posix/lifecycle-posix.c" -I"$OUT" -I"$FFI" -fPIC -o "$OUT/lifecycle.o"

echo "== 3. Link libarkham_core.dylib"
# TODO(verify): on macOS GHC may need -dynamic instead of -static; check the link output.
OBJS=$(ls "$OUT"/*.o)
stack --no-terminal exec -- ghc -shared -dynamic -threaded -no-hs-main -O0 \
    "${PKGS[@]}" $OBJS -o "$OUT/libarkham_core.dylib"

echo "== 4. Export check (expect ah_core_init, ah_core_call, ah_core_result_*, ah_core_shutdown)"
nm -gU "$OUT/libarkham_core.dylib" | grep ah_core || { echo "Export check failed"; exit 1; }

echo "== 5. NativeHost (managed): native/host is the ported copy (NativeLibrary.Load / default SQLite VFS off Windows)"
cd "$HOST"
dotnet build Arkham.NativeHost.csproj -c Release -o "$OUT/host"

echo "Done (draft). Output: $OUT"
