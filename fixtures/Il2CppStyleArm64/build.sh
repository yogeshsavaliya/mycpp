#!/usr/bin/env bash
# Rebuilds libfixture.so and symbols.json with the local clang/lld.
set -euo pipefail
cd "$(dirname "$0")"
FLAGS="--target=aarch64-linux-gnu -O2 -fPIC -fvisibility=hidden -fno-exceptions -fno-rtti -ffreestanding -nostdlib"
clang++ $FLAGS -c fixture.cpp -o fixture.o
clang++ $FLAGS -c runtime_stubs.cpp -o runtime_stubs.o
ld.lld -shared --hash-style=gnu -o libfixture.so fixture.o runtime_stubs.o
NM=llvm-nm
if ! command -v llvm-nm >/dev/null 2>&1; then
  NM=/usr/lib/llvm-18/bin/llvm-nm
fi
python3 - <<PY
import json, subprocess
out = subprocess.check_output(["$NM", "--defined-only", "-S", "libfixture.so"], text=True)
syms = {}
for line in out.splitlines():
    p = line.split()
    if len(p) == 4 and p[2] in ("T", "t"):
        syms[p[3]] = {"address": int(p[0], 16), "size": int(p[1], 16)}
json.dump(dict(sorted(syms.items(), key=lambda kv: kv[1]["address"])), open("symbols.json", "w"), indent=2)
print(json.dumps(syms, indent=2))
PY
clang++ --version | head -1 > toolchain.txt
sha256sum libfixture.so >> toolchain.txt
echo "target=aarch64-linux-gnu" >> toolchain.txt
