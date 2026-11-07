/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e2a8: a9bd5ffe     	stp	x30, x23, [sp, #-0x30]!
  d6e2ac: a90157f6     	stp	x22, x21, [sp, #0x10]
  d6e2b0: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6e2b4: 90008196     	adrp	x22, 0x1d9e000
  d6e2b8: 90007877     	adrp	x23, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2bc: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2c0: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2c4: 395382c8     	ldrb	w8, [x22, #0x4e0]
  d6e2c8: f947aaf7     	ldr	x23, [x23, #0xf50]
  d6e2cc: f947ae94     	ldr	x20, [x20, #0xf58]
  d6e2d0: f94556b5     	ldr	x21, [x21, #0xaa8]
  d6e2d4: aa0003f3     	mov	x19, x0
  d6e2d8: 37000188     	tbnz	w8, #0x0, 0xd6e308 <__start_il2cpp+0x84d8>
  d6e2dc: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2e0: f947ac00     	ldr	x0, [x0, #0xf58]
  d6e2e4: 97fcde83     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e2e8: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2ec: f9455400     	ldr	x0, [x0, #0xaa8]
  d6e2f0: 97fcde80     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e2f4: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e2f8: f947a800     	ldr	x0, [x0, #0xf50]
  d6e2fc: 97fcde7d     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e300: 52800028     	mov	w8, #0x1                // =1
  d6e304: 391382c8     	strb	w8, [x22, #0x4e0]
  d6e308: f94002e0     	ldr	x0, [x23]
  d6e30c: 97fcdef8     	bl	0xca5eec <DllCanUnloadNow+0x1307c>
  d6e310: f9400282     	ldr	x2, [x20]
  d6e314: aa1303e1     	mov	x1, x19
  d6e318: aa0003f4     	mov	x20, x0
  d6e31c: 9400000a     	bl	0xd6e344 <__start_il2cpp+0x8514>
  d6e320: f94002a0     	ldr	x0, [x21]
  d6e324: b940e008     	ldr	w8, [x0, #0xe0]
  d6e328: 35000048     	cbnz	w8, 0xd6e330 <__start_il2cpp+0x8500>
  d6e32c: 97fcdeab     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e330: aa1403e0     	mov	x0, x20
  d6e334: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6e338: a94157f6     	ldp	x22, x21, [sp, #0x10]
  d6e33c: a8c35ffe     	ldp	x30, x23, [sp], #0x30
  d6e340: 14000023     	b	0xd6e3cc <__start_il2cpp+0x859c>
