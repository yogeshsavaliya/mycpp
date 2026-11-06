#!/usr/bin/env python3
"""Execute fixtures/Il2CppStyleArm64/libfixture.so in Unicorn and record field/return vectors."""
import json, os, random, struct
from unicorn import Uc, UC_ARCH_ARM64, UC_MODE_ARM, UC_HOOK_INTR
from unicorn.arm64_const import *

HERE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures", "Il2CppStyleArm64")
so = open(os.path.join(HERE, "libfixture.so"), "rb").read()
syms = json.load(open(os.path.join(HERE, "symbols.json")))

def segments(data):
    e_phoff, = struct.unpack_from("<Q", data, 0x20)
    e_phentsize, e_phnum = struct.unpack_from("<HH", data, 0x36)
    for i in range(e_phnum):
        p_type, p_flags, p_offset, p_vaddr, _, p_filesz, p_memsz, _ = struct.unpack_from("<IIQQQQQQ", data, e_phoff + i * e_phentsize)
        if p_type == 1:
            yield p_offset, p_vaddr, p_filesz, p_memsz

OBJ_A, OBJ_B, STACK, SENTINEL = 0x100000, 0x200000, 0x800000, 0x900000
FIELDS = {"count": (0x18, "<i"), "speed": (0x1C, "<f"), "flag": (0x20, "<B"), "next": (0x28, "<Q"), "total": (0x30, "<i")}

def run(sym, this_state, next_state, int_args=(), float_args=()):
    mu = Uc(UC_ARCH_ARM64, UC_MODE_ARM)
    for off, va, filesz, memsz in segments(so):
        base = va & ~0xFFF
        size = ((va + memsz + 0xFFF) & ~0xFFF) - base
        mu.mem_map(base, max(size, 0x1000))
        mu.mem_write(va, so[off:off + filesz])
    for region in (OBJ_A, OBJ_B, STACK - 0x10000, SENTINEL):
        mu.mem_map(region, 0x10000)
    def write_obj(addr, state):
        for name, (off, fmt) in FIELDS.items():
            if name == "next":
                mu.mem_write(addr + off, struct.pack(fmt, OBJ_B if state.get("next") else 0))
            else:
                mu.mem_write(addr + off, struct.pack(fmt, state.get(name, 0)))
    write_obj(OBJ_A, this_state)
    if next_state is not None:
        write_obj(OBJ_B, next_state)
    mu.reg_write(UC_ARM64_REG_SP, STACK)
    mu.reg_write(UC_ARM64_REG_X0, OBJ_A)
    for i, v in enumerate(int_args):
        mu.reg_write(UC_ARM64_REG_X1 + i, v & 0xFFFFFFFFFFFFFFFF)
    mu.reg_write(UC_ARM64_REG_X1 + len(int_args), 0xDEAD)
    for i, v in enumerate(float_args):
        mu.reg_write(UC_ARM64_REG_S0 + i, struct.unpack("<I", struct.pack("<f", v))[0])
    mu.reg_write(UC_ARM64_REG_LR, SENTINEL)
    trapped = []
    mu.hook_add(UC_HOOK_INTR, lambda uc, intno, ud: (trapped.append(uc.reg_read(UC_ARM64_REG_PC)), uc.emu_stop()))
    mu.emu_start(syms[sym]["address"], SENTINEL, count=10000)
    def read_obj(addr):
        out = {}
        for name, (off, fmt) in FIELDS.items():
            raw = bytes(mu.mem_read(addr + off, struct.calcsize(fmt)))
            v, = struct.unpack(fmt, raw)
            out[name] = (v != 0) if name == "next" else v
        return out
    result = {"this": read_obj(OBJ_A)}
    if next_state is not None:
        result["next"] = read_obj(OBJ_B)
    nre = syms["il2cpp_codegen_raise_null_reference_exception"]["address"]
    if trapped:
        pc = trapped[0]
        result["exception"] = "NullReferenceException" if nre <= pc < nre + 8 else "trap@0x%X" % pc
    else:
        result["x0"] = mu.reg_read(UC_ARM64_REG_X0)
        result["s0"], = struct.unpack("<f", struct.pack("<I", mu.reg_read(UC_ARM64_REG_S0) & 0xFFFFFFFF))
    return result

random.seed(20261106)
EDGE = [0, 1, -1, 2, 9, 10, 11, -5, 0x7FFFFFFF, -0x80000000]
def rint():
    return random.choice(EDGE) if random.random() < 0.6 else random.randint(-2**31, 2**31 - 1)
def rfloat():
    return random.choice([0.0, 1.0, -1.0, 0.5, 5.0, -2.5]) if random.random() < 0.6 else struct.unpack("<f", struct.pack("<f", random.uniform(-1000, 1000)))[0]
def rstate(with_next):
    return {"count": rint(), "speed": rfloat(), "flag": random.choice([0, 1]), "total": rint(), "next": with_next}

cases = []
def add(method, sym, n, ints=0, floats=0, next_mode="random"):
    for _ in range(n):
        has_next = (random.random() < 0.7) if next_mode == "random" else next_mode
        this = rstate(has_next)
        if method == ".ctor":
            this = {"count": 0, "speed": 0.0, "flag": 0, "total": 0, "next": False}
            has_next = False
        nxt = rstate(False) if has_next else None
        ia = [rint() for _ in range(ints)]
        fa = [rfloat() for _ in range(floats)]
        cases.append({"method": method, "this": this, "next": nxt, "intArgs": ia, "floatArgs": fa, "native": run(sym, this, nxt, ia, fa)})

add("SmoothFollow", "Fixture_SmoothFollow_m", 8)
add("LateUpdate", "Fixture_LateUpdate_m", 6)
add("Tick", "Fixture_Tick_m", 6)
add(".ctor", "Fixture__ctor_m", 2)
add("SumNext", "Fixture_SumNext_m", 8)
add("SumNext", "Fixture_SumNext_m", 3, next_mode=False)
add("Clamp", "Fixture_Clamp_m", 10, ints=1)
add("Scaled", "Fixture_Scaled_m", 6, floats=1)
add("Toggle", "Fixture_Toggle_m", 8)
add("Pick", "Fixture_Pick_m", 8, ints=2)
add("NextTotal", "Fixture_NextTotal_m", 4)
add("NextTotal", "Fixture_NextTotal_m", 2, next_mode=False)
add("CopyToNext", "Fixture_CopyToNext_m", 4)
add("CopyToNext", "Fixture_CopyToNext_m", 2, next_mode=False)
json.dump({"generator": "tools/fixture_vectors.py", "unicorn": __import__("unicorn").__version__, "cases": cases}, open(os.path.join(HERE, "vectors.json"), "w"), indent=1)
print(len(cases), "vectors;", sum(1 for c in cases if "exception" in c["native"]), "null-reference")
