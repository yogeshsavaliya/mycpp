/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e864: 6dbc2beb     	stp	d11, d10, [sp, #-0x40]!
  d6e868: 6d0123e9     	stp	d9, d8, [sp, #0x10]
  d6e86c: a90257fe     	stp	x30, x21, [sp, #0x20]
  d6e870: a9034ff4     	stp	x20, x19, [sp, #0x30]
  d6e874: 90008195     	adrp	x21, 0x1d9e000
  d6e878: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e87c: 395396a8     	ldrb	w8, [x21, #0x4e5]
  d6e880: f9456e94     	ldr	x20, [x20, #0xad8]
  d6e884: aa0003f3     	mov	x19, x0
  d6e888: 370000c8     	tbnz	w8, #0x0, 0xd6e8a0 <__start_il2cpp+0x8a70>
  d6e88c: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e890: f9456c00     	ldr	x0, [x0, #0xad8]
  d6e894: 97fcdd17     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e898: 52800028     	mov	w8, #0x1                // =1
  d6e89c: 391396a8     	strb	w8, [x21, #0x4e5]
  d6e8a0: f9400280     	ldr	x0, [x20]
  d6e8a4: f9401274     	ldr	x20, [x19, #0x20]
  d6e8a8: b940e008     	ldr	w8, [x0, #0xe0]
  d6e8ac: 35000048     	cbnz	w8, 0xd6e8b4 <__start_il2cpp+0x8a84>
  d6e8b0: 97fcdd4a     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e8b4: aa1403e0     	mov	x0, x20
  d6e8b8: aa1f03e1     	mov	x1, xzr
  d6e8bc: 94303b94     	bl	0x197d70c <__start_il2cpp+0xc178dc>
  d6e8c0: 360009e0     	tbz	w0, #0x0, 0xd6e9fc <__start_il2cpp+0x8bcc>
  d6e8c4: aa1303e0     	mov	x0, x19
  d6e8c8: aa1f03e1     	mov	x1, xzr
  d6e8cc: 94302525     	bl	0x1977d60 <__start_il2cpp+0xc11f30>
  d6e8d0: aa0003f4     	mov	x20, x0
  d6e8d4: aa1303e0     	mov	x0, x19
  d6e8d8: aa1f03e1     	mov	x1, xzr
  d6e8dc: 94302521     	bl	0x1977d60 <__start_il2cpp+0xc11f30>
  d6e8e0: b4000980     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e8e4: aa1f03e1     	mov	x1, xzr
  d6e8e8: 94304c50     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e8ec: f9401260     	ldr	x0, [x19, #0x20]
  d6e8f0: b4000900     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e8f4: aa1f03e1     	mov	x1, xzr
  d6e8f8: 4ea01c08     	mov	v8.16b, v0.16b
  d6e8fc: 94302eea     	bl	0x197a4a4 <__start_il2cpp+0xc14674>
  d6e900: b4000880     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e904: aa1f03e1     	mov	x1, xzr
  d6e908: 94304c48     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e90c: bd404a6a     	ldr	s10, [x19, #0x48]
  d6e910: aa1f03e0     	mov	x0, xzr
  d6e914: 4ea01c09     	mov	v9.16b, v0.16b
  d6e918: 91010275     	add	x21, x19, #0x40
  d6e91c: 94300fb4     	bl	0x19727ec <__start_il2cpp+0xc0c9bc>
  d6e920: 52aff008     	mov	w8, #0x7f800000         // =2139095040
  d6e924: 4ea01c04     	mov	v4.16b, v0.16b
  d6e928: 1e270103     	fmov	s3, w8
  d6e92c: 4ea81d00     	mov	v0.16b, v8.16b
  d6e930: 4ea91d21     	mov	v1.16b, v9.16b
  d6e934: aa1503e0     	mov	x0, x21
  d6e938: 4eaa1d42     	mov	v2.16b, v10.16b
  d6e93c: aa1f03e1     	mov	x1, xzr
  d6e940: 94300fb5     	bl	0x1972814 <__start_il2cpp+0xc0c9e4>
  d6e944: aa1303e0     	mov	x0, x19
  d6e948: aa1f03e1     	mov	x1, xzr
  d6e94c: 4ea01c08     	mov	v8.16b, v0.16b
  d6e950: 94302504     	bl	0x1977d60 <__start_il2cpp+0xc11f30>
  d6e954: b40005e0     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e958: aa1f03e1     	mov	x1, xzr
  d6e95c: 94304c33     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e960: f9401260     	ldr	x0, [x19, #0x20]
  d6e964: b4000560     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e968: aa1f03e1     	mov	x1, xzr
  d6e96c: 4ea11c29     	mov	v9.16b, v1.16b
  d6e970: 94302ecd     	bl	0x197a4a4 <__start_il2cpp+0xc14674>
  d6e974: b40004e0     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e978: aa1f03e1     	mov	x1, xzr
  d6e97c: 94304c2b     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e980: bd404a6b     	ldr	s11, [x19, #0x48]
  d6e984: aa1f03e0     	mov	x0, xzr
  d6e988: 4ea11c2a     	mov	v10.16b, v1.16b
  d6e98c: 91011275     	add	x21, x19, #0x44
  d6e990: 94300f97     	bl	0x19727ec <__start_il2cpp+0xc0c9bc>
  d6e994: 52aff008     	mov	w8, #0x7f800000         // =2139095040
  d6e998: 4ea01c04     	mov	v4.16b, v0.16b
  d6e99c: 1e270103     	fmov	s3, w8
  d6e9a0: 4ea91d20     	mov	v0.16b, v9.16b
  d6e9a4: 4eaa1d41     	mov	v1.16b, v10.16b
  d6e9a8: aa1503e0     	mov	x0, x21
  d6e9ac: 4eab1d62     	mov	v2.16b, v11.16b
  d6e9b0: aa1f03e1     	mov	x1, xzr
  d6e9b4: 94300f98     	bl	0x1972814 <__start_il2cpp+0xc0c9e4>
  d6e9b8: aa1303e0     	mov	x0, x19
  d6e9bc: aa1f03e1     	mov	x1, xzr
  d6e9c0: 4ea01c09     	mov	v9.16b, v0.16b
  d6e9c4: 943024e7     	bl	0x1977d60 <__start_il2cpp+0xc11f30>
  d6e9c8: b4000240     	cbz	x0, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e9cc: aa1f03e1     	mov	x1, xzr
  d6e9d0: 94304c16     	bl	0x1981a28 <__start_il2cpp+0xc1bbf8>
  d6e9d4: b40001f4     	cbz	x20, 0xd6ea10 <__start_il2cpp+0x8be0>
  d6e9d8: aa1403e0     	mov	x0, x20
  d6e9dc: 4ea81d00     	mov	v0.16b, v8.16b
  d6e9e0: 4ea91d21     	mov	v1.16b, v9.16b
  d6e9e4: a9434ff4     	ldp	x20, x19, [sp, #0x30]
  d6e9e8: a94257fe     	ldp	x30, x21, [sp, #0x20]
  d6e9ec: 6d4123e9     	ldp	d9, d8, [sp, #0x10]
  d6e9f0: aa1f03e1     	mov	x1, xzr
  d6e9f4: 6cc42beb     	ldp	d11, d10, [sp], #0x40
  d6e9f8: 14304c34     	b	0x1981ac8 <__start_il2cpp+0xc1bc98>
  d6e9fc: a9434ff4     	ldp	x20, x19, [sp, #0x30]
  d6ea00: a94257fe     	ldp	x30, x21, [sp, #0x20]
  d6ea04: 6d4123e9     	ldp	d9, d8, [sp, #0x10]
  d6ea08: 6cc42beb     	ldp	d11, d10, [sp], #0x40
  d6ea0c: d65f03c0     	ret
  d6ea10: 97fcdd38     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
