

# Compiling Zorb Programs

Zorb targets a "Bare Metal" execution environment. When Zorb code is translated to C, it does not use the standard C library (`libc`) and manages its own entry point (`_start`) and thread stacks. 

To ensure the resulting binary runs correctly on Linux x86_64, you **must** use specific compiler and linker flags.

## The Recommended Command

```bash
gcc -O2 -nostdlib -fno-pie -no-pie -z execstack -fno-builtin out.c -o out
```

---

## Flag Breakdown

### 1. Environment & Entry Point
* **`-nostdlib`**: Prevents the compiler from looking for `main()` or linking standard libraries. This allows Zorb's `_start` function to act as the true entry point.

### 2. Memory Addressing (Critical)
* **`-fno-pie` & **`-no-pie`**: Disables "Position Independent Executable" generation.
    * **Why?** Without a dynamic loader (like `ld-linux.so`), the program cannot resolve relative addresses for global variables at runtime. These flags ensure that pointers to global strings and arrays use fixed, absolute memory addresses.

### 3. Stack Permissions
* **`-z execstack`**: Marks the program's data segments as executable.
    * **Why?** Modern Linux security (NX-bit) prevents code execution on the stack by default. This flag allows the CPU to execute the `call` and `ret` instructions required for thread functions to run.

### 4. Code Generation
* **`-O2`**: Highly recommended for programs using `asm` blocks. It ensures the compiler efficiently maps Zorb variables to the specific CPU registers required by the Linux Syscall ABI.

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
| :--- | :--- | :--- |
| **Segfault (SIGSEGV)** | Missing `-z execstack` or `-no-pie`. | Ensure all flags are present. |
| **Garbage Output** | Missing `-fno-pie`. | The syscall is pointing to the wrong memory address. |
| **"Multiple definition of _start"** | Forgot `-nostdlib`. | GCC is trying to link the standard C startup files. |


