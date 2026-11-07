/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e20c: f81d0ffe     	str	x30, [sp, #-0x30]!
  d6e210: a90157f6     	stp	x22, x21, [sp, #0x10]
  d6e214: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6e218: 90008196     	adrp	x22, 0x1d9e000
  d6e21c: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e220: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e224: 39537ec8     	ldrb	w8, [x22, #0x4df]
  d6e228: f945c294     	ldr	x20, [x20, #0xb80]
  d6e22c: f947a6b5     	ldr	x21, [x21, #0xf48]
  d6e230: aa0003f3     	mov	x19, x0
  d6e234: 37000128     	tbnz	w8, #0x0, 0xd6e258 <__start_il2cpp+0x8428>
  d6e238: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e23c: f945c000     	ldr	x0, [x0, #0xb80]
  d6e240: 97fcdeac     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e244: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e248: f947a400     	ldr	x0, [x0, #0xf48]
  d6e24c: 97fcdea9     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e250: 52800028     	mov	w8, #0x1                // =1
  d6e254: 39137ec8     	strb	w8, [x22, #0x4df]
  d6e258: f9400288     	ldr	x8, [x20]
  d6e25c: aa1303e0     	mov	x0, x19
  d6e260: f9405d08     	ldr	x8, [x8, #0xb8]
  d6e264: f9000113     	str	x19, [x8]
  d6e268: f94002a1     	ldr	x1, [x21]
  d6e26c: 94053c77     	bl	0xebd448 <__start_il2cpp+0x157618>
  d6e270: f9001a60     	str	x0, [x19, #0x30]
  d6e274: b4000180     	cbz	x0, 0xd6e2a4 <__start_il2cpp+0x8474>
  d6e278: aa1f03e1     	mov	x1, xzr
  d6e27c: 942fae63     	bl	0x1959c08 <__start_il2cpp+0xbf3dd8>
  d6e280: bd003a60     	str	s0, [x19, #0x38]
  d6e284: b9003e7f     	str	wzr, [x19, #0x3c]
  d6e288: f9400288     	ldr	x8, [x20]
  d6e28c: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6e290: a94157f6     	ldp	x22, x21, [sp, #0x10]
  d6e294: f9405d08     	ldr	x8, [x8, #0xb8]
  d6e298: b900091f     	str	wzr, [x8, #0x8]
  d6e29c: f84307fe     	ldr	x30, [sp], #0x30
  d6e2a0: d65f03c0     	ret
  d6e2a4: 97fcdf13     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
