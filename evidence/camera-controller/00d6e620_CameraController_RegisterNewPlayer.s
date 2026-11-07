/tmp/camera-evidence/libil2cpp.so:	file format elf64-littleaarch64
Disassembly of section il2cpp:
0000000000d65e30 <__start_il2cpp>:
  d6e620: a9be57fe     	stp	x30, x21, [sp, #-0x20]!
  d6e624: a9014ff4     	stp	x20, x19, [sp, #0x10]
  d6e628: 90008195     	adrp	x21, 0x1d9e000
  d6e62c: 90007874     	adrp	x20, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e630: 39538aa8     	ldrb	w8, [x21, #0x4e2]
  d6e634: f9455694     	ldr	x20, [x20, #0xaa8]
  d6e638: aa0003f3     	mov	x19, x0
  d6e63c: 370000c8     	tbnz	w8, #0x0, 0xd6e654 <__start_il2cpp+0x8824>
  d6e640: 90007860     	adrp	x0, 0x1c7a000 <_ZTVSt9exception+0x20>
  d6e644: f9455400     	ldr	x0, [x0, #0xaa8]
  d6e648: 97fcddaa     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e64c: 52800028     	mov	w8, #0x1                // =1
  d6e650: 39138aa8     	strb	w8, [x21, #0x4e2]
  d6e654: f9400280     	ldr	x0, [x20]
  d6e658: b940e008     	ldr	w8, [x0, #0xe0]
  d6e65c: 35000068     	cbnz	w8, 0xd6e668 <__start_il2cpp+0x8838>
  d6e660: 97fcddde     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e664: f9400280     	ldr	x0, [x20]
  d6e668: f9405c08     	ldr	x8, [x0, #0xb8]
  d6e66c: f9400108     	ldr	x8, [x8]
  d6e670: b4000268     	cbz	x8, 0xd6e6bc <__start_il2cpp+0x888c>
  d6e674: 90008195     	adrp	x21, 0x1d9e000
  d6e678: 395562a8     	ldrb	w8, [x21, #0x558]
  d6e67c: 350000c8     	cbnz	w8, 0xd6e694 <__start_il2cpp+0x8864>
  d6e680: aa1403e0     	mov	x0, x20
  d6e684: 97fcdd9b     	bl	0xca5cf0 <DllCanUnloadNow+0x12e80>
  d6e688: f9400280     	ldr	x0, [x20]
  d6e68c: 52800028     	mov	w8, #0x1                // =1
  d6e690: 391562a8     	strb	w8, [x21, #0x558]
  d6e694: b940e008     	ldr	w8, [x0, #0xe0]
  d6e698: 35000068     	cbnz	w8, 0xd6e6a4 <__start_il2cpp+0x8874>
  d6e69c: 97fcddcf     	bl	0xca5dd8 <DllCanUnloadNow+0x12f68>
  d6e6a0: f9400280     	ldr	x0, [x20]
  d6e6a4: f9405c08     	ldr	x8, [x0, #0xb8]
  d6e6a8: f9404508     	ldr	x8, [x8, #0x88]
  d6e6ac: f9001268     	str	x8, [x19, #0x20]
  d6e6b0: a9414ff4     	ldp	x20, x19, [sp, #0x10]
  d6e6b4: a8c257fe     	ldp	x30, x21, [sp], #0x20
  d6e6b8: d65f03c0     	ret
  d6e6bc: 97fcde0d     	bl	0xca5ef0 <DllCanUnloadNow+0x13080>
