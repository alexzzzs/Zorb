
# Compiling Freestanding Linux Zorb Programs

This document is about Zorb's `freestanding-linux` target, not true bare-metal kernels.

`freestanding-linux` means:

- `_start` is preserved instead of lowered to `main`
- the generated program talks directly to the Linux syscall ABI
- the usual C runtime startup objects are not linked

That is still a Linux userspace binary. It is not the same thing as `bare-metal-x86_64`, which links a kernel ELF through either Zorb's bundled linker script or a user-supplied linker script.

## Recommended Command

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

## Flag Breakdown

### Environment and entry point

- `-nostdlib`: do not link the standard C runtime startup files. This lets Zorb's `_start` remain the entry point.

### Addressing model

- `-fno-pie` and `-no-pie`: disable position-independent executable generation. The current freestanding Linux runtime model expects fixed addresses for globals and string literals.

### Stack permissions

- `-z execstack`: mark the stack executable. The current task runtime swaps to manually managed stacks and expects normal call/return instructions to work there.

### Code generation

- `-O2`: strongly recommended for syscall-heavy and inline-assembly-heavy output.
- `-fno-builtin`: prevents the C compiler from rewriting operations into hosted libc assumptions.

## Troubleshooting

| Symptom | Likely Cause | Fix |
| :--- | :--- | :--- |
| Segfault | Missing `-z execstack` or `-no-pie`. | Use the full freestanding Linux flag set. |
| Garbage output | Missing `-fno-pie`. | Rebuild with `-fno-pie -no-pie`. |
| `multiple definition of _start` | Forgot `-nostdlib`. | Remove the hosted startup objects by using `-nostdlib`. |
