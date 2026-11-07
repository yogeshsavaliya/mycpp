/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e6c8: a9be57fe     	stp	x30, x21, [sp, #-0x20]!
  d6e6cc: a9014ff4     	stp	x20, x19, [sp, #0x10]
  d6e6d0: 90008194     	adrp	x20, 0x1d9e000
  d6e6d4: 90007875     	adrp	x21, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e6d8: 39538e88     	ldrb	w8, [x20, #0x4e3]
  d6e6dc: f946eab5     	ldr	x21, [x21, #0xdd0]
  d6e6e0: aa0003f3     	mov	x19, x0
  d6e6e4: 370000c8     	tbnz	w8, #0x0, 0xd6e6fc <__start_il2cpp+0x88cc>
  d6e6e8: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e6ec: f946e800     	ldr	x0, [x0, #0xdd0]
  d6e6f0: 97fcdd80     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e6f4: 52800028     	mov	w8, #0x1                // =1
  d6e6f8: 39138e88     	strb	w8, [x20, #0x4e3]
  d6e6fc: f94002a0     	ldr	x0, [x21]
  d6e700: aa1f03e1     	mov	x1, xzr
  d6e704: 94302fc6     	bl	0x197a61c <__start_il2cpp+0xc147ec>
  d6e708: f9001260     	str	x0, [x19, #0x20]
  d6e70c: a9414ff4     	ldp	x20, x19, [sp, #0x10]
  d6e710: a8c257fe     	ldp	x30, x21, [sp], #0x20
  d6e714: d65f03c0     	ret
