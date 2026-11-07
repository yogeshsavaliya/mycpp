/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6ee68: fc1e0fe8     	str	d8, [sp, #-0x20]!
  d6ee6c: f90007fe     	str	x30, [sp, #0x8]
  d6ee70: a9014ff4     	stp	x20, x19, [sp, #0x10]
  d6ee74: 90008194     	adrp	x20, 0x1d9e000
  d6ee78: 3953aa88     	ldrb	w8, [x20, #0x4ea]
  d6ee7c: aa0003f3     	mov	x19, x0
  d6ee80: 370000c8     	tbnz	w8, #0x0, 0xd6ee98 <__start_il2cpp+0x9068>
  d6ee84: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6ee88: f945c000     	ldr	x0, [x0, #0xb80]
  d6ee8c: 97fcdb99     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6ee90: 52800028     	mov	w8, #0x1                // =1
  d6ee94: 3913aa88     	strb	w8, [x20, #0x4ea]
  d6ee98: b9401268     	ldr	w8, [x19, #0x10]
  d6ee9c: 7100051f     	cmp	w8, #0x1
  d6eea0: 540000e0     	b.eq	0xd6eebc <__start_il2cpp+0x908c>
  d6eea4: 35000588     	cbnz	w8, 0xd6ef54 <__start_il2cpp+0x9124>
  d6eea8: 12800008     	mov	w8, #-0x1               // =-1
  d6eeac: 1e2703e8     	fmov	s8, wzr
  d6eeb0: b9002a7f     	str	wzr, [x19, #0x28]
  d6eeb4: b9001268     	str	w8, [x19, #0x10]
  d6eeb8: 14000007     	b	0xd6eed4 <__start_il2cpp+0x90a4>
  d6eebc: bd402a68     	ldr	s8, [x19, #0x28]
  d6eec0: 1e2e1000     	fmov	s0, #1.00000000
  d6eec4: 12800008     	mov	w8, #-0x1               // =-1
  d6eec8: b9001268     	str	w8, [x19, #0x10]
  d6eecc: 1e202100     	fcmp	s8, s0
  d6eed0: 54000425     	b.pl	0xd6ef54 <__start_il2cpp+0x9124>
  d6eed4: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6eed8: f945c294     	ldr	x20, [x20, #0xb80]
  d6eedc: aa1f03e0     	mov	x0, xzr
  d6eee0: 94300e43     	bl	0x19727ec <__start_il2cpp+0xc0c9bc>
  d6eee4: 1e2c1001     	fmov	s1, #0.50000000
  d6eee8: 1e210800     	fmul	s0, s0, s1
  d6eeec: 1e2e1004     	fmov	s4, #1.00000000
  d6eef0: 1e202900     	fadd	s0, s8, s0
  d6eef4: 1e2703e1     	fmov	s1, wzr
  d6eef8: bd002a60     	str	s0, [x19, #0x28]
  d6eefc: 1e202008     	fcmp	s0, #0.0
  d6ef00: 1e245800     	fmin	s0, s0, s4
  d6ef04: 1e215c00     	fcsel	s0, s0, s1, pl
  d6ef08: f9400288     	ldr	x8, [x20]
  d6ef0c: 2d440e62     	ldp	s2, s3, [x19, #0x20]
  d6ef10: 1e211005     	fmov	s5, #3.00000000
  d6ef14: 1e202801     	fadd	s1, s0, s0
  d6ef18: 1e250805     	fmul	s5, s0, s5
  d6ef1c: 1e210801     	fmul	s1, s0, s1
  d6ef20: 1e250805     	fmul	s5, s0, s5
  d6ef24: 1e210800     	fmul	s0, s0, s1
  d6ef28: 1e2038a0     	fsub	s0, s5, s0
  d6ef2c: f9405d08     	ldr	x8, [x8, #0xb8]
  d6ef30: 1e200861     	fmul	s1, s3, s0
  d6ef34: 1e203880     	fsub	s0, s4, s0
  d6ef38: 1e200840     	fmul	s0, s2, s0
  d6ef3c: 1e202820     	fadd	s0, s1, s0
  d6ef40: 52800020     	mov	w0, #0x1                // =1
  d6ef44: bd000900     	str	s0, [x8, #0x8]
  d6ef48: f9000e7f     	str	xzr, [x19, #0x18]
  d6ef4c: b9001260     	str	w0, [x19, #0x10]
  d6ef50: 14000002     	b	0xd6ef58 <__start_il2cpp+0x9128>
  d6ef54: 2a1f03e0     	mov	w0, wzr
  d6ef58: a9414ff4     	ldp	x20, x19, [sp, #0x10]
  d6ef5c: f94007fe     	ldr	x30, [sp, #0x8]
  d6ef60: fc4207e8     	ldr	d8, [sp], #0x20
  d6ef64: d65f03c0     	ret
