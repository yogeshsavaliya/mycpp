/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6ebb0: fc1d0fe8     	str	d8, [sp, #-0x30]!
  d6ebb4: a90157fe     	stp	x30, x21, [sp, #0x10]
  d6ebb8: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6ebbc: 90008194     	adrp	x20, 0x1d9e000
  d6ebc0: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ebc4: 39539e88     	ldrb	w8, [x20, #0x4e7]
  d6ebc8: f947b6b5     	ldr	x21, [x21, #0xf68]
  d6ebcc: 4ea01c08     	mov	v8.16b, v0.16b
  d6ebd0: aa0003f3     	mov	x19, x0
  d6ebd4: 370000c8     	tbnz	w8, #0x0, 0xd6ebec <__start_il2cpp+0x8dbc>
  d6ebd8: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ebdc: f947b400     	ldr	x0, [x0, #0xf68]
  d6ebe0: 97fcdc44     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ebe4: 52800028     	mov	w8, #0x1                // =1
  d6ebe8: 39139e88     	strb	w8, [x20, #0x4e7]
  d6ebec: f94002a0     	ldr	x0, [x21]
  d6ebf0: 97fcdcbf     	bl	0xca5eec <DllCanUnloadNow+0x1307c>
  d6ebf4: aa1f03e1     	mov	x1, xzr
  d6ebf8: aa0003f4     	mov	x20, x0
  d6ebfc: 942aa23d     	bl	0x18174f0 <__start_il2cpp+0xab16c0>
  d6ec00: b900129f     	str	wzr, [x20, #0x10]
  d6ec04: f9001293     	str	x19, [x20, #0x20]
  d6ec08: bd002a88     	str	s8, [x20, #0x28]
  d6ec0c: aa1403e0     	mov	x0, x20
  d6ec10: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6ec14: a94157fe     	ldp	x30, x21, [sp, #0x10]
  d6ec18: fc4307e8     	ldr	d8, [sp], #0x30
  d6ec1c: d65f03c0     	ret
