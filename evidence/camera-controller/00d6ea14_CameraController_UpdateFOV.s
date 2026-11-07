/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6ea14: a9be57fe     	stp	x30, x21, [sp, #-0x20]!
  d6ea18: a9014ff4     	stp	x20, x19, [sp, #0x10]
  d6ea1c: 90008194     	adrp	x20, 0x1d9e000
  d6ea20: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ea24: 39539a88     	ldrb	w8, [x20, #0x4e6]
  d6ea28: f94556b5     	ldr	x21, [x21, #0xaa8]
  d6ea2c: aa0003f3     	mov	x19, x0
  d6ea30: 370001e8     	tbnz	w8, #0x0, 0xd6ea6c <__start_il2cpp+0x8c3c>
  d6ea34: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ea38: f945c000     	ldr	x0, [x0, #0xb80]
  d6ea3c: 97fcdcad     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ea40: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ea44: f9455400     	ldr	x0, [x0, #0xaa8]
  d6ea48: 97fcdcaa     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ea4c: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ea50: f947b000     	ldr	x0, [x0, #0xf60]
  d6ea54: 97fcdca7     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ea58: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ea5c: f9456c00     	ldr	x0, [x0, #0xad8]
  d6ea60: 97fcdca4     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ea64: 52800028     	mov	w8, #0x1                // =1
  d6ea68: 39139a88     	strb	w8, [x20, #0x4e6]
  d6ea6c: f94002a0     	ldr	x0, [x21]
  d6ea70: b940e008     	ldr	w8, [x0, #0xe0]
  d6ea74: 35000068     	cbnz	w8, 0xd6ea80 <__start_il2cpp+0x8c50>
  d6ea78: 97fcdcd8     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6ea7c: f94002a0     	ldr	x0, [x21]
  d6ea80: f9405c08     	ldr	x8, [x0, #0xb8]
  d6ea84: 39404909     	ldrb	w9, [x8, #0x12]
  d6ea88: 34000089     	cbz	w9, 0xd6ea98 <__start_il2cpp+0x8c68>
  d6ea8c: a9414ff4     	ldp	x20, x19, [sp, #0x10]
  d6ea90: a8c257fe     	ldp	x30, x21, [sp], #0x20
  d6ea94: d65f03c0     	ret
  d6ea98: b940e009     	ldr	w9, [x0, #0xe0]
  d6ea9c: 35000089     	cbnz	w9, 0xd6eaac <__start_il2cpp+0x8c7c>
  d6eaa0: 97fcdcce     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6eaa4: f94002a8     	ldr	x8, [x21]
  d6eaa8: f9405d08     	ldr	x8, [x8, #0xb8]
  d6eaac: 39404508     	ldrb	w8, [x8, #0x11]
  d6eab0: 34fffee8     	cbz	w8, 0xd6ea8c <__start_il2cpp+0x8c5c>
  d6eab4: 90007868     	adrp	x8, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6eab8: f9456d08     	ldr	x8, [x8, #0xad8]
  d6eabc: f9401274     	ldr	x20, [x19, #0x20]
  d6eac0: f9400100     	ldr	x0, [x8]
  d6eac4: b940e008     	ldr	w8, [x0, #0xe0]
  d6eac8: 35000048     	cbnz	w8, 0xd6ead0 <__start_il2cpp+0x8ca0>
  d6eacc: 97fcdcc3     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6ead0: aa1403e0     	mov	x0, x20
  d6ead4: aa1f03e1     	mov	x1, xzr
  d6ead8: 94303b0d     	bl	0x197d70c <__start_il2cpp+0xc178dc>
  d6eadc: 3607fd80     	tbz	w0, #0x0, 0xd6ea8c <__start_il2cpp+0x8c5c>
  d6eae0: f9401260     	ldr	x0, [x19, #0x20]
  d6eae4: b4000540     	cbz	x0, 0xd6eb8c <__start_il2cpp+0x8d5c>
  d6eae8: 90007868     	adrp	x8, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6eaec: f947b108     	ldr	x8, [x8, #0xf60]
  d6eaf0: f9400101     	ldr	x1, [x8]
  d6eaf4: 9405aa23     	bl	0xed9380 <__start_il2cpp+0x173550>
  d6eaf8: b40004a0     	cbz	x0, 0xd6eb8c <__start_il2cpp+0x8d5c>
  d6eafc: aa1f03e1     	mov	x1, xzr
  d6eb00: 94003ba4     	bl	0xd7d990 <__start_il2cpp+0x17b60>
  d6eb04: f94002a8     	ldr	x8, [x21]
  d6eb08: 2a0003f4     	mov	w20, w0
  d6eb0c: b940e109     	ldr	w9, [x8, #0xe0]
  d6eb10: 35000089     	cbnz	w9, 0xd6eb20 <__start_il2cpp+0x8cf0>
  d6eb14: aa0803e0     	mov	x0, x8
  d6eb18: 97fcdcb0     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6eb1c: f94002a8     	ldr	x8, [x21]
  d6eb20: f9405d08     	ldr	x8, [x8, #0xb8]
  d6eb24: 52a852c9     	mov	w9, #0x42960000         // =1117126656
  d6eb28: f9401a60     	ldr	x0, [x19, #0x30]
  d6eb2c: 1e270121     	fmov	s1, w9
  d6eb30: b9404908     	ldr	w8, [x8, #0x48]
  d6eb34: 1e249000     	fmov	s0, #10.00000000
  d6eb38: 4b080288     	sub	w8, w20, w8
  d6eb3c: 1e220102     	scvtf	s2, w8
  d6eb40: 1e211841     	fdiv	s1, s2, s1
  d6eb44: 1e205820     	fmin	s0, s1, s0
  d6eb48: 1e202028     	fcmp	s1, #0.0
  d6eb4c: 1e2703e1     	fmov	s1, wzr
  d6eb50: 1e215c00     	fcsel	s0, s0, s1, pl
  d6eb54: bd003e60     	str	s0, [x19, #0x3c]
  d6eb58: b40001a0     	cbz	x0, 0xd6eb8c <__start_il2cpp+0x8d5c>
  d6eb5c: 90007868     	adrp	x8, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6eb60: f945c108     	ldr	x8, [x8, #0xb80]
  d6eb64: bd403a61     	ldr	s1, [x19, #0x38]
  d6eb68: a9414ff4     	ldp	x20, x19, [sp, #0x10]
  d6eb6c: aa1f03e1     	mov	x1, xzr
  d6eb70: f9400108     	ldr	x8, [x8]
  d6eb74: 1e212800     	fadd	s0, s0, s1
  d6eb78: f9405d08     	ldr	x8, [x8, #0xb8]
  d6eb7c: bd400902     	ldr	s2, [x8, #0x8]
  d6eb80: 1e202840     	fadd	s0, s2, s0
  d6eb84: a8c257fe     	ldp	x30, x21, [sp], #0x20
  d6eb88: 142fac2f     	b	0x1959c44 <__start_il2cpp+0xbf3e14>
  d6eb8c: 97fcdcd9     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
