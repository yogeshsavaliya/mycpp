/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e4a8: a9bd5ffe     	stp	x30, x23, [sp, #-0x30]!
  d6e4ac: a90157f6     	stp	x22, x21, [sp, #0x10]
  d6e4b0: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6e4b4: 90008196     	adrp	x22, 0x1d9e000
  d6e4b8: 90007877     	adrp	x23, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4bc: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4c0: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4c4: 395386c8     	ldrb	w8, [x22, #0x4e1]
  d6e4c8: f947aaf7     	ldr	x23, [x23, #0xf50]
  d6e4cc: f947ae94     	ldr	x20, [x20, #0xf58]
  d6e4d0: f94556b5     	ldr	x21, [x21, #0xaa8]
  d6e4d4: aa0003f3     	mov	x19, x0
  d6e4d8: 37000188     	tbnz	w8, #0x0, 0xd6e508 <__start_il2cpp+0x86d8>
  d6e4dc: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4e0: f947ac00     	ldr	x0, [x0, #0xf58]
  d6e4e4: 97fcde03     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e4e8: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4ec: f9455400     	ldr	x0, [x0, #0xaa8]
  d6e4f0: 97fcde00     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e4f4: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e4f8: f947a800     	ldr	x0, [x0, #0xf50]
  d6e4fc: 97fcddfd     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e500: 52800028     	mov	w8, #0x1                // =1
  d6e504: 391386c8     	strb	w8, [x22, #0x4e1]
  d6e508: f94002e0     	ldr	x0, [x23]
  d6e50c: 97fcde78     	bl	0xca5eec <DllCanUnloadNow+0x1307c>
  d6e510: f9400282     	ldr	x2, [x20]
  d6e514: aa1303e1     	mov	x1, x19
  d6e518: aa0003f4     	mov	x20, x0
  d6e51c: 97ffff8a     	bl	0xd6e344 <__start_il2cpp+0x8514>
  d6e520: f94002a0     	ldr	x0, [x21]
  d6e524: b940e008     	ldr	w8, [x0, #0xe0]
  d6e528: 35000048     	cbnz	w8, 0xd6e530 <__start_il2cpp+0x8700>
  d6e52c: 97fcde2b     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e530: aa1403e0     	mov	x0, x20
  d6e534: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6e538: a94157f6     	ldp	x22, x21, [sp, #0x10]
  d6e53c: a8c35ffe     	ldp	x30, x23, [sp], #0x30
  d6e540: 14000001     	b	0xd6e544 <__start_il2cpp+0x8714>
