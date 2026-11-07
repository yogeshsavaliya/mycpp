/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e738: 6dbb33ed     	stp	d13, d12, [sp, #-0x50]!
  d6e73c: 6d012beb     	stp	d11, d10, [sp, #0x10]
  d6e740: 6d0223e9     	stp	d9, d8, [sp, #0x20]
  d6e744: a90357fe     	stp	x30, x21, [sp, #0x30]
  d6e748: a9044ff4     	stp	x20, x19, [sp, #0x40]
  d6e74c: 90008195     	adrp	x21, 0x1d9e000
  d6e750: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e754: 395392a8     	ldrb	w8, [x21, #0x4e4]
  d6e758: f9456e94     	ldr	x20, [x20, #0xad8]
  d6e75c: aa0003f3     	mov	x19, x0
  d6e760: 370000c8     	tbnz	w8, #0x0, 0xd6e778 <__start_il2cpp+0x8948>
  d6e764: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e768: f9456c00     	ldr	x0, [x0, #0xad8]
  d6e76c: 97fcdd61     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e770: 52800028     	mov	w8, #0x1                // =1
  d6e774: 391392a8     	strb	w8, [x21, #0x4e4]
  d6e778: f9400280     	ldr	x0, [x20]
  d6e77c: f9401274     	ldr	x20, [x19, #0x20]
  d6e780: b940e008     	ldr	w8, [x0, #0xe0]
  d6e784: 35000048     	cbnz	w8, 0xd6e78c <__start_il2cpp+0x895c>
  d6e788: 97fcdd94     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e78c: aa1403e0     	mov	x0, x20
  d6e790: aa1f03e1     	mov	x1, xzr
  d6e794: 94303bde     	bl	0x197d70c <__start_il2cpp+0xc178dc>
  d6e798: 36000580     	tbz	w0, #0x0, 0xd6e848 <__start_il2cpp+0x8a18>
  d6e79c: aa1303e0     	mov	x0, x19
  d6e7a0: aa1f03e1     	mov	x1, xzr
  d6e7a4: 9430256f     	bl	0x1977d60 <__start_il2cpp+0xc11f30>
  d6e7a8: b40005c0     	cbz	x0, 0xd6e860 <__start_il2cpp+0x8a30>
  d6e7ac: aa1f03e1     	mov	x1, xzr
  d6e7b0: 94304c9e     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e7b4: f9401260     	ldr	x0, [x19, #0x20]
  d6e7b8: b4000540     	cbz	x0, 0xd6e860 <__start_il2cpp+0x8a30>
  d6e7bc: aa1f03e1     	mov	x1, xzr
  d6e7c0: 4ea01c08     	mov	v8.16b, v0.16b
  d6e7c4: 4ea11c29     	mov	v9.16b, v1.16b
  d6e7c8: 4ea21c4a     	mov	v10.16b, v2.16b
  d6e7cc: 94302f36     	bl	0x197a4a4 <__start_il2cpp+0xc14674>
  d6e7d0: b4000480     	cbz	x0, 0xd6e860 <__start_il2cpp+0x8a30>
  d6e7d4: aa1f03e1     	mov	x1, xzr
  d6e7d8: 94304c94     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e7dc: 90008194     	adrp	x20, 0x1d9e000
  d6e7e0: 39555688     	ldrb	w8, [x20, #0x555]
  d6e7e4: 4ea01c0b     	mov	v11.16b, v0.16b
  d6e7e8: 4ea11c2c     	mov	v12.16b, v1.16b
  d6e7ec: 4ea21c4d     	mov	v13.16b, v2.16b
  d6e7f0: 350000c8     	cbnz	w8, 0xd6e808 <__start_il2cpp+0x89d8>
  d6e7f4: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e7f8: f946ec00     	ldr	x0, [x0, #0xdd8]
  d6e7fc: 97fcdd3d     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e800: 52800028     	mov	w8, #0x1                // =1
  d6e804: 39155688     	strb	w8, [x20, #0x555]
  d6e808: 90007868     	adrp	x8, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e80c: f946ed08     	ldr	x8, [x8, #0xdd8]
  d6e810: 1e2b390b     	fsub	s11, s8, s11
  d6e814: 1e2c3929     	fsub	s9, s9, s12
  d6e818: 1e2d3948     	fsub	s8, s10, s13
  d6e81c: f9400100     	ldr	x0, [x8]
  d6e820: b940e008     	ldr	w8, [x0, #0xe0]
  d6e824: 35000048     	cbnz	w8, 0xd6e82c <__start_il2cpp+0x89fc>
  d6e828: 97fcdd6c     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e82c: 1e2b0960     	fmul	s0, s11, s11
  d6e830: 1e290921     	fmul	s1, s9, s9
  d6e834: 1e212800     	fadd	s0, s0, s1
  d6e838: 1e280901     	fmul	s1, s8, s8
  d6e83c: 1e202820     	fadd	s0, s1, s0
  d6e840: 1e21c000     	fsqrt	s0, s0
  d6e844: bd002a60     	str	s0, [x19, #0x28]
  d6e848: a9444ff4     	ldp	x20, x19, [sp, #0x40]
  d6e84c: a94357fe     	ldp	x30, x21, [sp, #0x30]
  d6e850: 6d4223e9     	ldp	d9, d8, [sp, #0x20]
  d6e854: 6d412beb     	ldp	d11, d10, [sp, #0x10]
  d6e858: 6cc533ed     	ldp	d13, d12, [sp], #0x50
  d6e85c: d65f03c0     	ret
  d6e860: 97fcdda4     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
