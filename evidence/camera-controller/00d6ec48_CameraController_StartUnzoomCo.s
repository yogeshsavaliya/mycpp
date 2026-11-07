/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6ec48: 6dbd23e9     	stp	d9, d8, [sp, #-0x30]!
  d6ec4c: f9000bfe     	str	x30, [sp, #0x10]
  d6ec50: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6ec54: 90008193     	adrp	x19, 0x1d9e000
  d6ec58: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ec5c: 3953a268     	ldrb	w8, [x19, #0x4e8]
  d6ec60: f947ba94     	ldr	x20, [x20, #0xf70]
  d6ec64: 4ea11c28     	mov	v8.16b, v1.16b
  d6ec68: 4ea01c09     	mov	v9.16b, v0.16b
  d6ec6c: 370000c8     	tbnz	w8, #0x0, 0xd6ec84 <__start_il2cpp+0x8e54>
  d6ec70: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ec74: f947b800     	ldr	x0, [x0, #0xf70]
  d6ec78: 97fcdc1e     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ec7c: 52800028     	mov	w8, #0x1                // =1
  d6ec80: 3913a268     	strb	w8, [x19, #0x4e8]
  d6ec84: f9400280     	ldr	x0, [x20]
  d6ec88: 97fcdc99     	bl	0xca5eec <DllCanUnloadNow+0x1307c>
  d6ec8c: aa1f03e1     	mov	x1, xzr
  d6ec90: aa0003f3     	mov	x19, x0
  d6ec94: 942aa217     	bl	0x18174f0 <__start_il2cpp+0xab16c0>
  d6ec98: b900127f     	str	wzr, [x19, #0x10]
  d6ec9c: 2d042269     	stp	s9, s8, [x19, #0x20]
  d6eca0: aa1303e0     	mov	x0, x19
  d6eca4: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6eca8: f9400bfe     	ldr	x30, [sp, #0x10]
  d6ecac: 6cc323e9     	ldp	d9, d8, [sp], #0x30
  d6ecb0: d65f03c0     	ret
