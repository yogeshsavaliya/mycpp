/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6ecf4: d100c3ff     	sub	sp, sp, #0x30
  d6ecf8: fd000be8     	str	d8, [sp, #0x10]
  d6ecfc: f9000ffe     	str	x30, [sp, #0x18]
  d6ed00: a9024ff4     	stp	x20, x19, [sp, #0x20]
  d6ed04: 90008194     	adrp	x20, 0x1d9e000
  d6ed08: 3953a688     	ldrb	w8, [x20, #0x4e9]
  d6ed0c: aa0003f3     	mov	x19, x0
  d6ed10: 370000c8     	tbnz	w8, #0x0, 0xd6ed28 <__start_il2cpp+0x8ef8>
  d6ed14: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ed18: f9457000     	ldr	x0, [x0, #0xae0]
  d6ed1c: 97fcdbf5     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ed20: 52800028     	mov	w8, #0x1                // =1
  d6ed24: 3913a688     	strb	w8, [x20, #0x4e9]
  d6ed28: b9401268     	ldr	w8, [x19, #0x10]
  d6ed2c: f9401274     	ldr	x20, [x19, #0x20]
  d6ed30: 7100051f     	cmp	w8, #0x1
  d6ed34: 540001e0     	b.eq	0xd6ed70 <__start_il2cpp+0x8f40>
  d6ed38: 35000648     	cbnz	w8, 0xd6ee00 <__start_il2cpp+0x8fd0>
  d6ed3c: 12800008     	mov	w8, #-0x1               // =-1
  d6ed40: b9001268     	str	w8, [x19, #0x10]
  d6ed44: b40006b4     	cbz	x20, 0xd6ee18 <__start_il2cpp+0x8fe8>
  d6ed48: f9401a80     	ldr	x0, [x20, #0x30]
  d6ed4c: b4000660     	cbz	x0, 0xd6ee18 <__start_il2cpp+0x8fe8>
  d6ed50: aa1f03e1     	mov	x1, xzr
  d6ed54: 942fabad     	bl	0x1959c08 <__start_il2cpp+0xbf3dd8>
  d6ed58: 1e219001     	fmov	s1, #3.50000000
  d6ed5c: 1e212801     	fadd	s1, s0, s1
  d6ed60: 1e2703e8     	fmov	s8, wzr
  d6ed64: b900367f     	str	wzr, [x19, #0x34]
  d6ed68: 2d058660     	stp	s0, s1, [x19, #0x2c]
  d6ed6c: 14000007     	b	0xd6ed88 <__start_il2cpp+0x8f58>
  d6ed70: bd403668     	ldr	s8, [x19, #0x34]
  d6ed74: 1e2e1000     	fmov	s0, #1.00000000
  d6ed78: 12800008     	mov	w8, #-0x1               // =-1
  d6ed7c: b9001268     	str	w8, [x19, #0x10]
  d6ed80: 1e202100     	fcmp	s8, s0
  d6ed84: 540003e5     	b.pl	0xd6ee00 <__start_il2cpp+0x8fd0>
  d6ed88: aa1f03e0     	mov	x0, xzr
  d6ed8c: 94300e98     	bl	0x19727ec <__start_il2cpp+0xc0c9bc>
  d6ed90: bd402a61     	ldr	s1, [x19, #0x28]
  d6ed94: 1e211800     	fdiv	s0, s0, s1
  d6ed98: 1e202900     	fadd	s0, s8, s0
  d6ed9c: bd003660     	str	s0, [x19, #0x34]
  d6eda0: b40003d4     	cbz	x20, 0xd6ee18 <__start_il2cpp+0x8fe8>
  d6eda4: f9401a80     	ldr	x0, [x20, #0x30]
  d6eda8: b4000380     	cbz	x0, 0xd6ee18 <__start_il2cpp+0x8fe8>
  d6edac: 2d458a61     	ldp	s1, s2, [x19, #0x2c]
  d6edb0: 1e2e1003     	fmov	s3, #1.00000000
  d6edb4: 1e202008     	fcmp	s0, #0.0
  d6edb8: 1e2703e4     	fmov	s4, wzr
  d6edbc: 1e235800     	fmin	s0, s0, s3
  d6edc0: 1e213842     	fsub	s2, s2, s1
  d6edc4: 1e245c00     	fcsel	s0, s0, s4, pl
  d6edc8: 1e220800     	fmul	s0, s0, s2
  d6edcc: 1e202820     	fadd	s0, s1, s0
  d6edd0: aa1f03e1     	mov	x1, xzr
  d6edd4: 942fab9c     	bl	0x1959c44 <__start_il2cpp+0xbf3e14>
  d6edd8: 90007868     	adrp	x8, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6eddc: f9457108     	ldr	x8, [x8, #0xae0]
  d6ede0: 910033e1     	add	x1, sp, #0xc
  d6ede4: b9000fff     	str	wzr, [sp, #0xc]
  d6ede8: f9400100     	ldr	x0, [x8]
  d6edec: 97fcdbfd     	bl	0xca5de0 <DllCanUnloadNow+0x12f70>
  d6edf0: f9000e60     	str	x0, [x19, #0x18]
  d6edf4: 52800020     	mov	w0, #0x1                // =1
  d6edf8: b9001260     	str	w0, [x19, #0x10]
  d6edfc: 14000002     	b	0xd6ee04 <__start_il2cpp+0x8fd4>
  d6ee00: 2a1f03e0     	mov	w0, wzr
  d6ee04: a9424ff4     	ldp	x20, x19, [sp, #0x20]
  d6ee08: f9400ffe     	ldr	x30, [sp, #0x18]
  d6ee0c: fd400be8     	ldr	d8, [sp, #0x10]
  d6ee10: 9100c3ff     	add	sp, sp, #0x30
  d6ee14: d65f03c0     	ret
  d6ee18: 97fcdc36     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
