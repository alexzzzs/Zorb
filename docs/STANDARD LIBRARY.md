# Zorb Standard Library Reference

This reference documents the standard library implemented in the repository's `std/` directory. It includes the public API, relevant internal definitions, platform behavior, and examples.

The standard library is ordinary Zorb source. Use it with imports such as:

```zorb
import "std/io.zorb"
import "std/os.zorb"
```

If a file imports another standard-library module internally, users normally do not need to import that dependency unless they call it directly.

## Error Codes: `std/errors.zorb`

### Internal Definitions

```zorb
export struct Error {}

export error OutOfMemory = 1
export error InvalidSize = 2
export error InvalidArgument = 3
export error BufferTooSmall = 4
export error OutOfBounds = 5
export error Overflow = 6
export error NullPointer = 7
export error AlreadyExists = 8
export error NotFound = 9
export error WouldBlock = 10
export error EndOfFile = 11
export error IOError = 12
export error UnsupportedPlatform = 13
export error NotImplemented = 14
```

### Usage

```zorb
import "std/errors.zorb"

fn validate(size: i64) !i64 {
    if size <= 0 {
        return error.InvalidSize
    }

    return size
}
```

Error codes are ordinary Zorb error declarations. They are used through `error.Name` and carry `i32` values in error unions.

## Operating System: `std/os.zorb`

`std/os.zorb` imports `errors.zorb`, declares Windows externs, and exports the `std.os` namespace marker struct.

### Internal Declarations

```zorb
import "errors.zorb"

export struct std.os {}

import c "windows.h"
extern fn ExitProcess(uExitCode: u32) -> void
extern fn VirtualAlloc(addr: *u8, size: i64, type: u32, protect: u32) -> *u8
```

### Platform Queries

```zorb
export fn std.os.is_linux() -> bool
export fn std.os.is_windows() -> bool
export fn std.os.is_bare_metal() -> bool
export fn std.os.is_x86_64() -> bool
export fn std.os.is_aarch64() -> bool
```

Each function returns the corresponding `Builtin.*` value.

Example:

```zorb
import "std/os.zorb"

fn main() -> i64 {
    if std.os.is_linux() {
        return 1
    }
    if std.os.is_windows() {
        return 2
    }
    return 0
}
```

### Names

```zorb
export fn std.os.platform_name() -> string
```

Returns:

```text
"bare-metal" when Builtin.IsBareMetal
"linux"      when Builtin.IsLinux
"windows"    when Builtin.IsWindows
"unknown"    otherwise
```

```zorb
export fn std.os.arch_name() -> string
```

Returns:

```text
"x86_64"  when Builtin.IsX86_64
"aarch64" when Builtin.IsAArch64
"unknown" otherwise
```

### Exit

```zorb
export fn std.os.exit(code: i32)
```

Behavior:

```text
bare-metal: infinite halt loop; on x86_64 emits cli/hlt
Linux x86_64: syscall(60, code)
Linux AArch64: syscall(93, code)
Windows: ExitProcess(cast(u32, code))
```

Example:

```zorb
import "std/os.zorb"

fn _start() {
    std.os.exit(0)
}
```

### Platform Type Code

```zorb
export fn std.os.get_type() -> i32
```

Returns:

```text
3 for bare metal
1 for Linux
2 for Windows
0 for unknown
```

### Page Allocation

```zorb
export fn std.os.get_pages(size: i64) !*u8
```

Behavior:

```text
size <= 0: error.InvalidSize
bare-metal: error.UnsupportedPlatform
Linux: mmap syscall
Windows: VirtualAlloc
unknown/unsupported: error.UnsupportedPlatform
```

Linux syscall numbers:

```text
x86_64: 9
AArch64: 222
```

Linux allocation uses:

```text
addr = 0
len = size
prot = 3      // READ | WRITE
flags = 34    // PRIVATE | ANON
fd = -1
offset = 0
```

Windows allocation uses:

```text
MEM_COMMIT | MEM_RESERVE = 0x3000
PAGE_READWRITE = 0x04
```

Example:

```zorb
import "std/os.zorb"

fn main() -> i64 {
    pages: *u8 = std.os.get_pages(4096) catch |err| {
        return err
    }

    return cast(i64, pages)
}
```

## I/O: `std/io.zorb`

`std/io.zorb` imports `str.zorb`, registers `windows.h`, declares Windows file-input/output externs, and exports the `std.io` namespace marker struct.

### Internal Declarations

```zorb
import "str.zorb"
export struct std.io {}

import c "windows.h"
extern fn GetStdHandle(nStdHandle: i32) -> i64
extern fn ReadFile(h: i64, buf: *i8, len: u32, read: *u32, overlapped: i64) -> i32
extern fn WriteFile(h: i64, buf: *i8, len: u32, written: *u32, overlapped: i64) -> i32
```

Internal file descriptor helpers:

```zorb
fn std.io.stdout_fd() -> i32 {
    return 1
}

fn std.io.stderr_fd() -> i32 {
    return 2
}
```

### Text Output

```zorb
export fn std.io.print(msg: string)
export fn std.io.println(msg: string)
export fn std.io.eprint(msg: string)
export fn std.io.eprintln(msg: string)
```

`print` and `eprint` convert the string to a `[]u8` by setting:

```zorb
msg_bytes.ptr = cast(*u8, msg)
msg_bytes.len = std.str.len(msg)
```

Then they call `std.io.write`.

`println` and `eprintln` call their non-newline forms and then write a one-byte newline slice.

Example:

```zorb
import "std/io.zorb"
import "std/os.zorb"

fn _start() {
    std.io.print("hello")
    std.io.println(" world")
    std.io.eprintln("diagnostic")
    std.os.exit(0)
}
```

### Integer Output

```zorb
export fn std.io.print_i64(val: i64)
export fn std.io.eprint_i64(val: i64)
export fn std.io.println_i64(val: i64)
export fn std.io.eprintln_i64(val: i64)
```

These allocate a local `[32]u8`, coerce it to `[]u8`, call `std.str.from_i64`, then write the produced prefix.

### Boolean Output

```zorb
export fn std.io.print_bool(val: bool)
```

Prints `true` or `false`.

Example:

```zorb
import "std/io.zorb"

fn main() {
    std.io.print_i64(-42)
    std.io.print("\n")
}
```

### Raw Write

```zorb
export fn std.io.write(fd: i32, buf: []u8)
```

If `buf.len < 0`, the function returns without writing.

Platform behavior:

```text
bare-metal x86_64: writes each byte to debug port 0xE9 with outb
bare-metal non-x86_64: Builtin.CompileError
Linux x86_64: syscall 1
Linux AArch64: syscall 64
Windows stdout: GetStdHandle(-11), WriteFile
Windows stderr: GetStdHandle(-12), WriteFile
```

On Windows, lengths greater than `4294967295` are clamped to that value before calling `WriteFile`.

### Raw Read

```zorb
export fn std.io.read(fd: i32, buf: []u8) -> i64
```

Behavior:

```text
buf.len < 0: return -1
bare-metal: return -1
Linux x86_64: syscall 0
Linux AArch64: syscall 63
Windows stdin: GetStdHandle(-10), ReadFile
Windows stdout/stderr: resolves those handles too, though stdin is the intended use
unknown/unsupported: return -1
```

On Windows, lengths greater than `4294967295` are clamped before calling `ReadFile`. Failed Windows reads return `-1`.

Example:

```zorb
import "std/io.zorb"

fn main() {
    bytes: [3]u8 = [3]u8{ 65, 66, 10 }
    view: []u8 = bytes
    std.io.write(1, view)
}
```

## Strings: `std/str.zorb`

`std/str.zorb` exports the `std.str` namespace marker struct.

### Internal Definition

```zorb
export struct std.str {}
```

### Length

```zorb
export [noinline, noclone]
fn std.str.len(s: string) -> i64
```

Computes the number of bytes before the first nul byte. Internally it casts the string to `*i8` and increments until `ptr[len] == 0`.

Example:

```zorb
import "std/str.zorb"

fn main() -> i64 {
    return std.str.len("abc")
}
```

### Reverse

```zorb
export fn std.str.reverse(buf: []u8)
```

Reverses the slice in place by swapping elements from the ends toward the middle.

Important behavior: it initializes `j` to `buf.len - 1`. Passing an empty slice would produce `j = -1`; the loop condition `i < j` prevents access, so the function returns without writing.

Example:

```zorb
import "std/str.zorb"

fn main() {
    data: [4]u8 = [4]u8{ 1, 2, 3, 4 }
    view: []u8 = data
    std.str.reverse(view)
    // data is now 4, 3, 2, 1
}
```

### Equality And Prefix/Suffix Checks

```zorb
export fn std.str.eql(a: string, b: string) -> bool
export fn std.str.slice_eql(a: []u8, b: []u8) -> bool
export fn std.str.starts_with(s: string, prefix: string) -> bool
export fn std.str.ends_with(s: string, suffix: string) -> bool
```

Current behavior:

```text
eql: compares string lengths, then byte-by-byte contents
slice_eql: compares slice lengths, then byte-by-byte contents
starts_with: false when prefix is longer than s
ends_with: false when suffix is longer than s
```

### String Copy

```zorb
export fn std.str.copy(dst: []u8, src: string) -> i64
```

Copies the bytes of `src` into `dst` without appending a terminator. Returns the copied byte count on success or `-1` when `dst.len < std.str.len(src)`.

### Integer Formatting

```zorb
export fn std.str.from_i64(value: i64, buf: []u8) -> i64
export fn std.str.from_u64(value: u64, buf: []u8) -> i64
```

Formats an integer into a caller-provided byte buffer as ASCII decimal, writes a nul terminator, and returns the number of digits written. It returns `-1` on insufficient capacity.

Current capacity rules:

```text
buf.len < 2: return -1
value == 0: writes "0\0", returns 1
otherwise: requires buf.len >= 21
```

`from_i64` writes digits in reverse, optionally appends `-`, reverses only the written prefix, then writes the terminator.
`from_u64` follows the same flow without signed handling.

Example:

```zorb
import "std/str.zorb"

fn main() -> i64 {
    buf: [32]u8
    digits: []u8 = buf
    len: i64 = std.str.from_i64(-42, digits)
    if len < 0 {
        return 1
    }
    digits.len = len
    return std.str.len(cast(string, digits.ptr))
}
```

## Memory: `std/mem.zorb`

`std/mem.zorb` imports `errors.zorb` and `os.zorb`, then exports `std.mem` and a bump allocator.

### Internal Definitions

```zorb
import "errors.zorb"
import "os.zorb"

export struct std.mem {}

export struct std.mem.HeapAllocator {
    buffer: *u8,
    len: i64,
    pos: i64
}
```

### Slice Helpers

```zorb
export fn std.mem.zero(buf: []u8)
export fn std.mem.copy(dst: []u8, src: []u8) -> i64
```

Behavior:

```text
zero: writes 0 to every byte in the slice
copy: returns -1 when dst.len < src.len, otherwise copies src.len bytes and returns src.len
```

`HeapAllocator` is a simple bump allocator over one contiguous region. It does not free memory.

### Initialization

```zorb
export fn std.mem.HeapAllocator.init(initial_size: i64) !std.mem.HeapAllocator
```

Behavior:

```text
initial_size <= 0: error.InvalidSize
std.os.get_pages(initial_size) error.InvalidSize: returns error.InvalidSize
std.os.get_pages(initial_size) error.UnsupportedPlatform: returns error.UnsupportedPlatform
other allocation failure: returns error.OutOfMemory
success: buffer = raw memory, len = initial_size, pos = 0
```

Example:

```zorb
import "std/mem.zorb"

fn main() -> i64 {
    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(4096) catch |err| {
        return err
    }

    return heap.len
}
```

### Allocation

```zorb
export fn std.mem.HeapAllocator.alloc(self: *std.mem.HeapAllocator, size: i64) !*u8
```

Behavior:

```text
size <= 0: error.InvalidSize
aligned_size = (size + 7) & -8
self.pos + aligned_size > self.len: error.OutOfMemory
success: returns &self.buffer[self.pos], then advances self.pos
```

Allocations are 8-byte aligned in size. The allocator assumes the backing buffer address returned by the OS is sufficiently aligned.

Example:

```zorb
import "std/mem.zorb"

fn main() -> i64 {
    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(4096) catch |err| {
        return err
    }

    ptr: *u8 = std.mem.HeapAllocator.alloc(&heap, 64) catch |err| {
        return err
    }

    return cast(i64, ptr)
}
```

## File System: `std/fs.zorb`

`std/fs.zorb` provides small cross-platform file helpers for the hosted
targets. It currently exposes open-for-read, open-for-write, close, existence
checks, simple metadata, rename/delete operations, and whole-file read/write
helpers.

### Open For Read

```zorb
export fn std.fs.open_read(path: string) !i32
```

Behavior:

```text
Linux x86_64/AArch64: uses openat with O_RDONLY
Windows x86_64/AArch64: uses the CRT _open(path, O_RDONLY | O_BINARY, 0)
missing file or open failure: error.NotFound
other targets: error.UnsupportedPlatform
```

### Open For Write

```zorb
export fn std.fs.open_write(path: string) !i32
```

Behavior:

```text
Linux x86_64/AArch64: uses openat with O_WRONLY | O_CREAT | O_TRUNC and mode 0644
Windows x86_64/AArch64: uses the CRT _open(path, O_WRONLY | O_CREAT | O_TRUNC | O_BINARY, owner read/write mode bits)
open failure: error.IOError
other targets: error.UnsupportedPlatform
```

### Close

```zorb
export fn std.fs.close(fd: i32)
```

Behavior:

```text
Linux x86_64/AArch64: closes the descriptor with close
Windows x86_64/AArch64: closes the descriptor with _close
other targets: no-op
```

### Exists

```zorb
export fn std.fs.exists(path: string) -> bool
```

Behavior:

```text
existing readable file: true
missing or unreadable file: false
unsupported targets: false
```

### Size

```zorb
export fn std.fs.size(path: string) !i64
```

Behavior:

```text
missing file: error.NotFound
Linux x86_64/AArch64: opens the file and uses lseek(fd, 0, SEEK_END)
Windows x86_64/AArch64: opens the file and uses _lseeki64(fd, 0, SEEK_END)
seek failure: error.IOError
unsupported targets: error.UnsupportedPlatform
```

### Read All

```zorb
export fn std.fs.read_all(path: string, allocator: *std.mem.HeapAllocator) ![]u8
```

Behavior:

```text
open failure: forwards error.NotFound or error.UnsupportedPlatform
read failure: error.IOError
allocation failure: error.InvalidSize or error.OutOfMemory
success: returns an owned []u8 containing the file contents
```

### Write All

```zorb
export fn std.fs.write_all(path: string, buf: []u8) !i32
```

Behavior:

```text
buf.len < 0: error.InvalidSize
open failure: forwards error.IOError or error.UnsupportedPlatform
write failure or partial-progress failure: error.IOError
success: truncates/creates the target file and returns 0 after writing the full buffer
```

### Delete

```zorb
export fn std.fs.delete(path: string) !i32
```

Behavior:

```text
Linux x86_64/AArch64: uses unlinkat(path, 0)
Windows x86_64/AArch64: uses the CRT _unlink(path)
missing path: error.NotFound
unsupported targets: error.UnsupportedPlatform
success: returns 0
```

### Rename

```zorb
export fn std.fs.rename(old_path: string, new_path: string) !i32
```

Behavior:

```text
Linux x86_64/AArch64: uses renameat(old_path, new_path)
Windows x86_64/AArch64: uses the CRT rename(old_path, new_path)
missing source path: error.NotFound
unsupported targets: error.UnsupportedPlatform
success: returns 0
```

Example:

```zorb
import "std/fs.zorb"
import "std/io.zorb"
import "std/mem.zorb"

fn main() -> i64 {
    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(8192) catch |err| {
        return err
    }

    message: []u8
    message.ptr = cast(*u8, "hello file\n")
    message.len = 11

    std.fs.write_all("note.txt", message) catch |err| {
        return err
    }

    size: i64 = std.fs.size("note.txt") catch |err| {
        return err
    }
    std.io.println_i64(size)

    std.fs.rename("note.txt", "note-old.txt") catch |err| {
        return err
    }

    data: []u8 = std.fs.read_all("note-old.txt", &heap) catch |err| {
        return err
    }
    std.io.write(1, data)

    std.fs.delete("note-old.txt") catch |err| {
        return err
    }

    return 0
}
```

## Networking: `std/net.zorb`

`std/net.zorb` is a low-level cross-platform networking layer for the current
hosted targets. It focuses on raw TCP socket setup and readiness polling rather
than higher-level protocols.

### Internal Definitions

```zorb
import "errors.zorb"

export struct std.net {}

export struct std.net.SockAddrV4 {
    family: u16,
    port: u16,
    addr: u32,
    zero: [8]u8
}
```

### Support Check

```zorb
export fn std.net.is_supported() -> bool
```

Current behavior:

```text
bare-metal: false
 Linux x86_64: true
 Linux AArch64: true
 Windows x86_64: true
 Windows AArch64: true
other targets: false
```

### Byte Order And IPv4 Helpers

```zorb
export fn std.net.errno(result: i64) -> i32
export fn std.net.htons(value: u16) -> u16
export fn std.net.htonl(value: u32) -> u32
export fn std.net.ipv4(a: u8, b: u8, c: u8, d: u8) -> u32
export fn std.net.sockaddr_v4(addr_be: u32, port: u16) -> std.net.SockAddrV4
export fn std.net.sockaddr_v4_loopback(port: u16) -> std.net.SockAddrV4
export fn std.net.sockaddr_v4_any(port: u16) -> std.net.SockAddrV4
```

Notes:

```text
errno(result): returns 0 on success; on Windows it prefers WSAGetLastError(), otherwise it converts the negative raw result
htons/htonl: current implementations assume the supported little-endian hosted targets in this repo
sockaddr_v4 helpers write family = AF_INET and store port/address in network byte order
```

### Raw Socket Operations

```zorb
export fn std.net.socket(domain: i32, kind: i32, protocol: i32) -> i64
export fn std.net.socket_tcp_v4() -> i64
export fn std.net.bind_v4(fd: i32, addr: *std.net.SockAddrV4) -> i64
export fn std.net.listen(fd: i32, backlog: i32) -> i64
export fn std.net.accept(fd: i32) -> i64
export fn std.net.connect_v4(fd: i32, addr: *std.net.SockAddrV4) -> i64
export fn std.net.send(fd: i32, buf: []u8) -> i64
export fn std.net.recv(fd: i32, buf: []u8) -> i64
export fn std.net.close(fd: i32) -> i64
export fn std.net.poll(fds: *std.net.PollFd, count: i64, timeout_ms: i32) -> i64
export fn std.net.poll_readable(fd: i32, timeout_ms: i32) -> i64
export fn std.net.poll_writable(fd: i32, timeout_ms: i32) -> i64
```

Behavior:

```text
on Linux targets: socket operations lower through raw syscalls
on Windows targets: socket operations lower through Winsock and WSAStartup is initialized lazily
on unsupported targets: these return negative sentinel values derived from std.errors
socket_tcp_v4(): shorthand for socket(AF_INET, SOCK_STREAM, 0)
bind_v4/connect_v4(): pass a 16-byte IPv4 sockaddr
send/recv(): lower through sendto/recvfrom on Linux and send/recv on Windows
accept(): currently accepts without returning peer address metadata
poll(): lowers through poll on Linux and WSAPoll on Windows
poll_readable/poll_writable(): convenience wrappers for one descriptor
```

Example:

```zorb
import "std/io.zorb"
import "std/os.zorb"
import "std/net.zorb"

fn _start() {
    if !std.net.is_supported() {
        std.io.println("net unsupported")
        std.os.exit(0)
        return
    }

    fd_raw: i64 = std.net.socket_tcp_v4()
    if fd_raw < 0 {
        std.os.exit(std.net.errno(fd_raw))
        return
    }

    fd: i32 = cast(i32, fd_raw)
    addr: std.net.SockAddrV4 = std.net.sockaddr_v4_any(0)

    if std.net.bind_v4(fd, &addr) < 0 {
        std.net.close(fd)
        std.os.exit(1)
        return
    }

    if std.net.listen(fd, 8) < 0 {
        std.net.close(fd)
        std.os.exit(2)
        return
    }

    std.net.close(fd)
    std.os.exit(0)
}
```

## Tasks: `std/task.zorb`

`std/task.zorb` implements cooperative fibers. It imports `mem.zorb` and `errors.zorb`.

This module is low-level and target-sensitive. Check `std.task.is_supported()` before using task APIs in portable programs.

### Internal Types And Globals

```zorb
import "mem.zorb"
import "errors.zorb"

export struct Fiber {
    id: i64,
    stack_ptr: *u8,
    state: i32, // 0: Ready, 1: Waiting, 2: Dead
    func: fn(*void) -> void,
    arg: *void,
    next: *Fiber
    wait_fd: i32,
    wait_events: i16,
    wait_revents: i16,
    waiting_next: *Fiber
}

export current_fiber: *Fiber = cast(*Fiber, 0)
export ready_queue_head: *Fiber = cast(*Fiber, 0)
export ready_queue_tail: *Fiber = cast(*Fiber, 0)
export scheduler_sp: *u8 = cast(*u8, 0)
export active_fibers: i64 = 0
```

The queue is a singly-linked list using `Fiber.next`. `active_fibers` tracks spawned fibers that have not finished.

### Support Check

```zorb
export fn std.task.is_supported() -> bool
```

Current behavior:

```text
bare-metal: false
x86_64 Linux/Windows: true
AArch64 Linux/Windows: true
otherwise: false
```

Example:

```zorb
import "std/task.zorb"

fn main() -> i64 {
    if std.task.is_supported() {
        return 0
    }
    return 1
}
```

### Context Switching

Internal barrier:

```zorb
fn std.task.context_switch_barrier()
```

Emits an empty asm block with clobbers for caller-saved registers and memory. The x86_64 and AArch64 clobber sets differ.

Public context-switch helpers:

```zorb
export [noinline]
fn std.task.yield_to_scheduler()

export [noinline]
fn std.task.resume(f: *Fiber)

export [noinline, noclone]
fn std.task.swap_context(old_sp_ptr: **u8, new_sp: *u8)
```

`yield_to_scheduler` saves the current fiber stack pointer through `&(current_fiber.stack_ptr)` and switches to `scheduler_sp`.

`resume` sets `current_fiber`, saves `scheduler_sp`, and switches to the fiber stack pointer.

`swap_context` is architecture-specific inline assembly on non-Windows targets:

```text
x86_64 Linux/ELF: saves/restores rbp, rbx, rdi, rsi, r12-r15 and switches rsp
x86_64 Windows: uses native Windows fibers via ConvertThreadToFiber/CreateFiber/SwitchToFiber
AArch64: saves/restores x19-x30 and switches sp
```

### Spawn

```zorb
export fn std.task.spawn(gpa: *std.mem.HeapAllocator, taskFunc: fn(*void) -> void, arg: *void) !i32
```

Behavior:

```text
gpa == null: error.InvalidArgument
!std.task.is_supported(): error.UnsupportedPlatform
x86_64 Linux/ELF: allocate stack and Fiber, initialize x86_64 stack frame, enqueue, return 0
x86_64 Windows: allocate Fiber metadata, create a native Windows fiber, enqueue, return 0
AArch64: allocate stack and Fiber, initialize AArch64 context frame, enqueue, return 0
allocation failure: error.OutOfMemory
```

Linux x86_64 and AArch64 allocate a 65536-byte stack plus a `Fiber` object from the provided `HeapAllocator`.

Windows x86_64 allocates the `Fiber` metadata from the provided `HeapAllocator` and uses the operating system fiber scheduler for the execution stack.

The private `fiber_entry_point` calls `f.func(f.arg)`, marks the fiber dead with `state = 2`, decrements `active_fibers`, and yields to the scheduler.

Example:

```zorb
import "std/io.zorb"
import "std/os.zorb"
import "std/mem.zorb"
import "std/task.zorb"

fn job(arg: *void) {
    std.io.print("job\n")
}

fn _start() {
    if !std.task.is_supported() {
        std.os.exit(1)
        return
    }

    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(131072) catch |err| {
        std.os.exit(err)
        return
    }

    result: i32 = std.task.spawn(&heap, job, cast(*void, 0)) catch |err| {
        std.os.exit(err)
        return
    }

    std.os.exit(result)
}
```

### Yield

```zorb
export fn std.task.yield()
```

If tasks are unsupported, it returns immediately. Otherwise it enqueues `current_fiber` and yields to the scheduler.
If `current_fiber == null`, it also returns immediately.

### Ready Queue Check

```zorb
export fn std.task.has_ready_fibers() -> bool
```

Returns whether the ready queue is non-empty.

### Queue Operations

```zorb
fn std.task.enqueue(f: *Fiber)
export fn std.task.dequeue() -> *Fiber
```

`enqueue` appends a fiber to the ready queue and clears `f.next`.

`dequeue` returns the head fiber or null when the queue is empty. When the last fiber is removed, it clears `ready_queue_tail`.

## Async: `std/async.zorb`

`std/async.zorb` imports `errors.zorb`, `net.zorb`, and `task.zorb` and
implements a cooperative socket-wait scheduler layer on top of fibers.

### Internal Definitions

```zorb
import "errors.zorb"
import "net.zorb"
import "task.zorb"

export struct std.async {}
waiting_head: *Fiber = cast(*Fiber, 0)
```

These globals are module-private because they are not exported.

### Support Check

```zorb
export fn std.async.is_supported() -> bool
```

Returns `true` only when:

```text
not bare-metal
std.task.is_supported()
```

### Init

```zorb
export fn std.async.init()
```

Resets the waiting queue head to null. There is no separate kernel poller
handle; async waiting builds on `std.net.poll(...)`.

### Wait Primitives

```zorb
export fn std.async.wait_readable(fd: i32) !i32
export fn std.async.wait_writable(fd: i32) !i32
```

Behavior:

```text
fd < 0: error.InvalidArgument
!std.async.is_supported(): error.UnsupportedPlatform
outside a fiber: blocks in std.net.poll_* with timeout -1
inside a fiber: registers the current fiber as waiting, yields, and resumes when the fd becomes ready
poll failure: error.IOError
```

### Event Loop

```zorb
export fn std.async.loop()
```

If async is unsupported, it returns immediately.

Otherwise:

1. Runs while `active_fibers > 0`.
2. Dequeues and resumes ready fibers.
3. Skips fibers whose `state == 2`.
4. If no fibers are waiting on I/O, returns immediately.
5. Otherwise calls `std.async.poll_events()`.

Internal polling:

```zorb
fn std.async.poll_events()
```

Returns immediately when no fibers are waiting. Otherwise it batches waiting
fibers into a local 64-element poll array, uses timeout `0` when the ready
queue is non-empty and `-1` when it is empty, calls `std.net.poll(...)`, and
wakes any fibers whose descriptors became ready. The implementation rotates its
batch cursor across polling passes so waiters beyond the first 64 still make
progress. Each individual poll pass still only examines up to 64 waiters.

Example:

```zorb
import "std/io.zorb"
import "std/os.zorb"
import "std/mem.zorb"
import "std/task.zorb"
import "std/async.zorb"

fn job(arg: *void) {
    std.io.print("ok\n")
}

fn _start() {
    if !std.async.is_supported() {
        std.os.exit(1)
        return
    }

    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(131072) catch |err| {
        std.os.exit(err)
        return
    }

    std.async.init()

    spawn_result: i32 = std.task.spawn(&heap, job, cast(*void, 0)) catch |err| {
        std.os.exit(err)
        return
    }

    std.async.loop()
    std.os.exit(spawn_result)
}
```

## Cross-Module Example

```zorb
import "std/io.zorb"
import "std/os.zorb"
import "std/mem.zorb"

fn _start() {
    std.io.print("running on ")
    std.io.print(std.os.platform_name())
    std.io.print(" ")
    std.io.println(std.os.arch_name())

    heap: std.mem.HeapAllocator = std.mem.HeapAllocator.init(4096) catch |err| {
        std.io.eprint("heap init failed: ")
        std.io.eprint_i64(err)
        std.io.eprint("\n")
        std.os.exit(err)
        return
    }

    ptr: *u8 = std.mem.HeapAllocator.alloc(&heap, 64) catch |err| {
        std.io.eprint("alloc failed\n")
        std.os.exit(err)
        return
    }

    if cast(i64, ptr) != 0 {
        std.io.println("allocated")
        std.os.exit(0)
        return
    }

    std.os.exit(1)
}
```

## Current Source Listings

This section records the current standard-library source definitions for reference. The prose above explains behavior and examples; these listings show the implementation shape directly.

### `std/errors.zorb`

```zorb
export struct Error {}

export error OutOfMemory = 1
export error InvalidSize = 2
export error InvalidArgument = 3
export error BufferTooSmall = 4
export error OutOfBounds = 5
export error Overflow = 6
export error NullPointer = 7
export error AlreadyExists = 8
export error NotFound = 9
export error WouldBlock = 10
export error EndOfFile = 11
export error IOError = 12
export error UnsupportedPlatform = 13
export error NotImplemented = 14
```

### `std/os.zorb`

```zorb
import "errors.zorb"

export struct std.os {}

import c "windows.h"
extern fn ExitProcess(uExitCode: u32) -> void
extern fn VirtualAlloc(addr: *u8, size: i64, type: u32, protect: u32) -> *u8

export fn std.os.is_linux() -> bool {
    if Builtin.IsLinux {
        return true
    }

    return false
}

export fn std.os.is_windows() -> bool {
    if Builtin.IsWindows {
        return true
    }

    return false
}

export fn std.os.is_bare_metal() -> bool {
    if Builtin.IsBareMetal {
        return true
    }

    return false
}

export fn std.os.is_x86_64() -> bool {
    if Builtin.IsX86_64 {
        return true
    }

    return false
}

export fn std.os.is_aarch64() -> bool {
    if Builtin.IsAArch64 {
        return true
    }

    return false
}

export fn std.os.platform_name() -> string {
    if Builtin.IsBareMetal {
        return "bare-metal"
    }

    if Builtin.IsLinux {
        return "linux"
    }

    if Builtin.IsWindows {
        return "windows"
    }

    return "unknown"
}

export fn std.os.arch_name() -> string {
    if Builtin.IsX86_64 {
        return "x86_64"
    }

    if Builtin.IsAArch64 {
        return "aarch64"
    }

    return "unknown"
}

export fn std.os.exit(code: i32) {
    if Builtin.IsBareMetal {
        while true {
            if Builtin.IsX86_64 {
                asm {
                    "cli"
                    "hlt"
                    : : : "memory"
                }
            }
        }
    }

    if Builtin.IsLinux {
        if Builtin.IsX86_64 {
            syscall(60, code)
        }

        if Builtin.IsAArch64 {
            syscall(93, code)
        }
    }
    
    if Builtin.IsWindows {
        ExitProcess(cast(u32, code))
    }
}

export fn std.os.get_type() -> i32 {
    if std.os.is_bare_metal() {
        return 3
    }
    if std.os.is_linux() {
        return 1
    }
    if std.os.is_windows() {
        return 2
    }
    return 0
}

export fn std.os.get_pages(size: i64) !*u8 {
    if size <= 0 {
        return error.InvalidSize
    }

    if Builtin.IsBareMetal {
        return error.UnsupportedPlatform
    }

    if Builtin.IsLinux {
        mmap_nr: i64 = 0
        if Builtin.IsX86_64 {
            mmap_nr = 9
        }
        if Builtin.IsAArch64 {
            mmap_nr = 222
        }
        if mmap_nr == 0 {
            return error.UnsupportedPlatform
        }

        addr: i64 = syscall(mmap_nr, 0, size, 3, 34, -1, 0)
        if addr < 0 {
            return error.OutOfMemory
        }
        return cast(*u8, addr)
    }
    
    if Builtin.IsWindows {
        pages: *u8 = VirtualAlloc(cast(*u8, 0), size, 12288, 4)
        if cast(i64, pages) == 0 {
            return error.OutOfMemory
        }
        return pages
    }

    return error.UnsupportedPlatform
}
```

### `std/io.zorb`

```zorb
import "str.zorb"
export struct std.io {}

import c "windows.h"
extern fn GetStdHandle(nStdHandle: i32) -> i64
extern fn ReadFile(h: i64, buf: *i8, len: u32, read: *u32, overlapped: i64) -> i32
extern fn WriteFile(h: i64, buf: *i8, len: u32, written: *u32, overlapped: i64) -> i32

fn std.io.stdout_fd() -> i32 {
    return 1
}

fn std.io.stderr_fd() -> i32 {
    return 2
}

export fn std.io.print(msg: string) {
    msg_bytes: []u8
    msg_bytes.ptr = cast(*u8, msg)
    msg_bytes.len = std.str.len(msg)
    std.io.write(std.io.stdout_fd(), msg_bytes)
}

export fn std.io.eprint(msg: string) {
    msg_bytes: []u8
    msg_bytes.ptr = cast(*u8, msg)
    msg_bytes.len = std.str.len(msg)
    std.io.write(std.io.stderr_fd(), msg_bytes)
}

export fn std.io.println(msg: string) {
    std.io.print(msg)
    newline: []u8
    newline.ptr = cast(*u8, "\n")
    newline.len = 1
    std.io.write(std.io.stdout_fd(), newline)
}

export fn std.io.eprintln(msg: string) {
    std.io.eprint(msg)
    newline: []u8
    newline.ptr = cast(*u8, "\n")
    newline.len = 1
    std.io.write(std.io.stderr_fd(), newline)
}

export fn std.io.print_bool(val: bool) {
    if val {
        std.io.print("true")
        return
    }

    std.io.print("false")
}

export fn std.io.print_i64(val: i64) {
    buf: [32]u8
    digits: []u8 = buf
    digits.len = std.str.from_i64(val, digits)
    std.io.write(std.io.stdout_fd(), digits)
}

export fn std.io.eprint_i64(val: i64) {
    buf: [32]u8
    digits: []u8 = buf
    digits.len = std.str.from_i64(val, digits)
    std.io.write(std.io.stderr_fd(), digits)
}

export fn std.io.println_i64(val: i64) {
    std.io.print_i64(val)
    std.io.print("\n")
}

export fn std.io.eprintln_i64(val: i64) {
    std.io.eprint_i64(val)
    std.io.eprint("\n")
}

export fn std.io.write(fd: i32, buf: []u8) {
    validated_len: i64 = buf.len
    if validated_len < 0 {
        return
    }

    if Builtin.IsBareMetal {
        if Builtin.IsX86_64 {
            index: i64 = 0
            while index < validated_len {
                ch: u8 = buf[index]
                asm {
                    "outb %b0, $0xE9"
                    : : "a"(ch) : "memory"
                }
                index = index + 1
            }
        }
        if !Builtin.IsX86_64 {
            Builtin.CompileError("Bare-metal output is only supported on x86_64. Non-x86_64 bare-metal targets require platform-specific UART/MMIO wiring.")
        }
        return
    }

    if Builtin.IsLinux {
        write_nr: i64 = 0
        if Builtin.IsX86_64 {
            write_nr = 1
        }
        if Builtin.IsAArch64 {
            write_nr = 64
        }
        if write_nr != 0 {
            syscall(write_nr, cast(i64, fd), cast(i64, buf.ptr), validated_len)
        }
    }

    if Builtin.IsWindows {
        windows_len: u32 = cast(u32, validated_len)
        if validated_len > 4294967295 {
            windows_len = cast(u32, 4294967295)
        }

        handle: i64 = 0
        if fd == std.io.stdout_fd() {
            handle = GetStdHandle(-11)
        }
        if fd == std.io.stderr_fd() {
            handle = GetStdHandle(-12)
        }
        if handle != 0 {
            written: u32 = 0
            WriteFile(handle, cast(*i8, buf.ptr), windows_len, &written, 0)
        }
    }
}

export fn std.io.read(fd: i32, buf: []u8) -> i64 {
    if buf.len < 0 {
        return -1
    }

    if Builtin.IsBareMetal {
        return -1
    }

    if Builtin.IsLinux {
        read_nr: i64 = 0
        if Builtin.IsX86_64 {
            read_nr = 0
        }
        if Builtin.IsAArch64 {
            read_nr = 63
        }
        if read_nr == 0 && !Builtin.IsX86_64 {
            return -1
        }

        return syscall(read_nr, cast(i64, fd), cast(i64, buf.ptr), buf.len)
    }

    if Builtin.IsWindows {
        windows_len: u32 = cast(u32, buf.len)
        if buf.len > 4294967295 {
            windows_len = cast(u32, 4294967295)
        }

        handle: i64 = 0
        if fd == std.io.stdout_fd() {
            handle = GetStdHandle(-11)
        }
        if fd == std.io.stderr_fd() {
            handle = GetStdHandle(-12)
        }
        if fd == 0 {
            handle = GetStdHandle(-10)
        }
        if handle == 0 {
            return -1
        }

        bytes_read: u32 = 0
        result: i32 = ReadFile(handle, cast(*i8, buf.ptr), windows_len, &bytes_read, 0)
        if result == 0 {
            return -1
        }

        return cast(i64, bytes_read)
    }

    return -1
}
```

### `std/str.zorb`

```zorb
export struct std.str {}

export [noinline, noclone]
fn std.str.len(s: string) -> i64 {
    len: i64 = 0
    ptr: *i8 = cast(*i8, s)
    
    while cast(i8, ptr[len]) != 0 {
        len = len + 1
    }
    return len
}

export fn std.str.reverse(buf: []u8) {
    i: i64 = 0
    j: i64 = buf.len - 1
    while i < j {
        temp: u8 = buf[i]
        buf[i] = buf[j]
        buf[j] = temp
        i = i + 1
        j = j - 1
    }
}

export fn std.str.eql(a: string, b: string) -> bool {
    a_len: i64 = std.str.len(a)
    b_len: i64 = std.str.len(b)
    if a_len != b_len {
        return false
    }

    a_ptr: *u8 = cast(*u8, a)
    b_ptr: *u8 = cast(*u8, b)
    i: i64 = 0
    while i < a_len {
        if a_ptr[i] != b_ptr[i] {
            return false
        }
        i = i + 1
    }

    return true
}

export fn std.str.slice_eql(a: []u8, b: []u8) -> bool {
    if a.len != b.len {
        return false
    }

    i: i64 = 0
    while i < a.len {
        if a[i] != b[i] {
            return false
        }
        i = i + 1
    }

    return true
}

export fn std.str.starts_with(s: string, prefix: string) -> bool {
    s_len: i64 = std.str.len(s)
    prefix_len: i64 = std.str.len(prefix)
    if prefix_len > s_len {
        return false
    }

    s_ptr: *u8 = cast(*u8, s)
    prefix_ptr: *u8 = cast(*u8, prefix)
    i: i64 = 0
    while i < prefix_len {
        if s_ptr[i] != prefix_ptr[i] {
            return false
        }
        i = i + 1
    }

    return true
}

export fn std.str.ends_with(s: string, suffix: string) -> bool {
    s_len: i64 = std.str.len(s)
    suffix_len: i64 = std.str.len(suffix)
    if suffix_len > s_len {
        return false
    }

    s_ptr: *u8 = cast(*u8, s)
    suffix_ptr: *u8 = cast(*u8, suffix)
    start: i64 = s_len - suffix_len
    i: i64 = 0
    while i < suffix_len {
        if s_ptr[start + i] != suffix_ptr[i] {
            return false
        }
        i = i + 1
    }

    return true
}

export fn std.str.copy(dst: []u8, src: string) -> i64 {
    src_len: i64 = std.str.len(src)
    if dst.len < src_len {
        return -1
    }

    src_ptr: *u8 = cast(*u8, src)
    i: i64 = 0
    while i < src_len {
        dst[i] = src_ptr[i]
        i = i + 1
    }

    return src_len
}

export fn std.str.from_i64(value: i64, buf: []u8) -> i64 {
    if buf.len < 2 {
        return -1
    }

    if value == 0 {
        buf[0] = cast(u8, 48)
        buf[1] = cast(u8, 0) 
        return 1
    }

    required_capacity: i64 = 21
    if buf.len < required_capacity {
        return -1
    }

    i: i64 = 0
    v: i64 = value
    is_neg: i32 = 0
    limit: i64 = buf.len - 1
    
    if v < 0 {
        is_neg = 1
        v = -v
    }

    while v > 0 && i < limit {
        rem: i64 = v % 10
        buf[i] = cast(u8, rem + 48)
        v = v / 10
        i = i + 1
    }

    if v > 0 {
        return -1
    }

    if is_neg == 1 {
        if i >= limit {
            return -1
        }
        buf[i] = cast(u8, 45)
        i = i + 1
    }

    digits: []u8 = buf
    digits.len = i
    std.str.reverse(digits)

    if i >= buf.len {
        return -1
    }
    buf[i] = cast(u8, 0)
    return i
}

export fn std.str.from_u64(value: u64, buf: []u8) -> i64 {
    if buf.len < 2 {
        return -1
    }

    if value == 0 {
        buf[0] = cast(u8, 48)
        buf[1] = cast(u8, 0)
        return 1
    }

    required_capacity: i64 = 21
    if buf.len < required_capacity {
        return -1
    }

    i: i64 = 0
    v: u64 = value
    limit: i64 = buf.len - 1

    while v > 0 && i < limit {
        rem: u64 = v % cast(u64, 10)
        buf[i] = cast(u8, rem + cast(u64, 48))
        v = v / cast(u64, 10)
        i = i + 1
    }

    if v > 0 {
        return -1
    }

    digits: []u8 = buf
    digits.len = i
    std.str.reverse(digits)

    if i >= buf.len {
        return -1
    }

    buf[i] = cast(u8, 0)
    return i
}
```

### `std/mem.zorb`

```zorb
import "errors.zorb"
import "os.zorb"

export struct std.mem {}

export struct std.mem.HeapAllocator {
    buffer: *u8,
    len: i64,
    pos: i64
}

export fn std.mem.zero(buf: []u8) {
    i: i64 = 0
    while i < buf.len {
        buf[i] = cast(u8, 0)
        i = i + 1
    }
}

export fn std.mem.copy(dst: []u8, src: []u8) -> i64 {
    if dst.len < src.len {
        return -1
    }

    i: i64 = 0
    while i < src.len {
        dst[i] = src[i]
        i = i + 1
    }

    return src.len
}

export fn std.mem.HeapAllocator.init(initial_size: i64) !std.mem.HeapAllocator {
    alloc: std.mem.HeapAllocator

    if initial_size <= 0 {
        return error.InvalidSize
    }

    raw_mem: *u8 = std.os.get_pages(initial_size) catch |err| {
        if err == error.InvalidSize {
            return error.InvalidSize
        }
        if err == error.UnsupportedPlatform {
            return error.UnsupportedPlatform
        }
        return error.OutOfMemory
    }

    alloc.buffer = raw_mem
    alloc.len = initial_size
    alloc.pos = 0
    return alloc
}

export fn std.mem.HeapAllocator.alloc(self: *std.mem.HeapAllocator, size: i64) !*u8 {
    if size <= 0 {
        return error.InvalidSize
    }

    aligned_size: i64 = (size + 7) & -8
    
    if self.pos + aligned_size > self.len {
        return error.OutOfMemory
    }
    
    ptr: *u8 = &self.buffer[self.pos]
    self.pos = self.pos + aligned_size
    return ptr
}
```

### `std/net.zorb`

```zorb
import "errors.zorb"

export struct std.net {}

export struct std.net.SockAddrV4 {
    family: u16,
    port: u16,
    addr: u32,
    zero: [8]u8
}

export fn std.net.is_supported() -> bool {
    if Builtin.IsBareMetal {
        return false
    }

    if !Builtin.IsLinux {
        return false
    }

    if Builtin.IsX86_64 {
        return true
    }

    if Builtin.IsAArch64 {
        return true
    }

    return false
}

export fn std.net.errno(result: i64) -> i32 {
    if result >= 0 {
        return 0
    }

    return cast(i32, -result)
}

export fn std.net.htons(value: u16) -> u16 {
    widened: u32 = cast(u32, value)
    upper: u32 = (widened & cast(u32, 255)) << 8
    lower: u32 = (widened >> 8) & cast(u32, 255)
    return cast(u16, upper | lower)
}

export fn std.net.htonl(value: u32) -> u32 {
    b0: u32 = (value & cast(u32, 255)) << 24
    b1: u32 = ((value >> 8) & cast(u32, 255)) << 16
    b2: u32 = ((value >> 16) & cast(u32, 255)) << 8
    b3: u32 = (value >> 24) & cast(u32, 255)
    return b0 | b1 | b2 | b3
}

export fn std.net.ipv4(a: u8, b: u8, c: u8, d: u8) -> u32 {
    host_order: u32 =
        (cast(u32, a) << 24) |
        (cast(u32, b) << 16) |
        (cast(u32, c) << 8) |
        cast(u32, d)
    return std.net.htonl(host_order)
}

export fn std.net.sockaddr_v4(addr_be: u32, port: u16) -> std.net.SockAddrV4 {
    addr: std.net.SockAddrV4
    addr.family = cast(u16, 2)
    addr.port = std.net.htons(port)
    addr.addr = addr_be

    i: i64 = 0
    while i < 8 {
        addr.zero[i] = cast(u8, 0)
        i = i + 1
    }

    return addr
}

export fn std.net.sockaddr_v4_loopback(port: u16) -> std.net.SockAddrV4 {
    return std.net.sockaddr_v4(std.net.ipv4(cast(u8, 127), cast(u8, 0), cast(u8, 0), cast(u8, 1)), port)
}

export fn std.net.sockaddr_v4_any(port: u16) -> std.net.SockAddrV4 {
    return std.net.sockaddr_v4(cast(u32, 0), port)
}

fn std.net.socket_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 41
    }

    if Builtin.IsAArch64 {
        return 198
    }

    return 0
}

fn std.net.connect_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 42
    }

    if Builtin.IsAArch64 {
        return 203
    }

    return 0
}

fn std.net.accept_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 43
    }

    if Builtin.IsAArch64 {
        return 202
    }

    return 0
}

fn std.net.sendto_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 44
    }

    if Builtin.IsAArch64 {
        return 206
    }

    return 0
}

fn std.net.recvfrom_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 45
    }

    if Builtin.IsAArch64 {
        return 207
    }

    return 0
}

fn std.net.close_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 3
    }

    if Builtin.IsAArch64 {
        return 57
    }

    return 0
}

fn std.net.bind_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 49
    }

    if Builtin.IsAArch64 {
        return 200
    }

    return 0
}

fn std.net.listen_nr() -> i64 {
    if Builtin.IsX86_64 {
        return 50
    }

    if Builtin.IsAArch64 {
        return 201
    }

    return 0
}

export fn std.net.socket(domain: i32, kind: i32, protocol: i32) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.socket_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, domain, kind, protocol)
}

export fn std.net.socket_tcp_v4() -> i64 {
    return std.net.socket(2, 1, 0)
}

export fn std.net.bind_v4(fd: i32, addr: *std.net.SockAddrV4) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.bind_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, cast(i64, addr), 16)
}

export fn std.net.listen(fd: i32, backlog: i32) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.listen_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, backlog)
}

export fn std.net.accept(fd: i32) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.accept_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, 0, 0)
}

export fn std.net.connect_v4(fd: i32, addr: *std.net.SockAddrV4) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.connect_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, cast(i64, addr), 16)
}

export fn std.net.send(fd: i32, buf: []u8) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.sendto_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, cast(i64, buf.ptr), buf.len, 0, 0, 0)
}

export fn std.net.recv(fd: i32, buf: []u8) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.recvfrom_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd, cast(i64, buf.ptr), buf.len, 0, 0, 0)
}

export fn std.net.close(fd: i32) -> i64 {
    if !std.net.is_supported() {
        return -cast(i64, error.UnsupportedPlatform)
    }

    nr: i64 = std.net.close_nr()
    if nr == 0 {
        return -cast(i64, error.UnsupportedPlatform)
    }

    return syscall(nr, fd)
}
```

### `std/task.zorb`

```zorb
import "mem.zorb"
import "errors.zorb"

export struct Fiber {
    id: i64,
    stack_ptr: *u8,
    state: i32,
    func: fn(*void) -> void,
    arg: *void,
    next: *Fiber,
    wait_fd: i32,
    wait_events: i16,
    wait_revents: i16,
    waiting_next: *Fiber
}

export current_fiber: *Fiber = cast(*Fiber, 0)
export ready_queue_head: *Fiber = cast(*Fiber, 0)
export ready_queue_tail: *Fiber = cast(*Fiber, 0)
export scheduler_sp: *u8 = cast(*u8, 0)
export active_fibers: i64 = 0

export fn std.task.is_supported() -> bool {
    if Builtin.IsBareMetal {
        return false
    }

    if !Builtin.IsLinux && !Builtin.IsWindows {
        return false
    }

    if Builtin.IsX86_64 {
        return true
    }

    if Builtin.IsAArch64 {
        return true
    }

    return false
}

fn std.task.context_switch_barrier() {
    if Builtin.IsX86_64 {
        asm {
            ""
            : : : "rax", "rcx", "rdx", "rsi", "rdi", "r8", "r9", "r10", "r11", "memory"
        }
    }

    if Builtin.IsAArch64 {
        asm {
            ""
            : : : "x0", "x1", "x2", "x3", "x4", "x5", "x6", "x7", "x8", "x9", "x10", "x11", "x12", "x13", "x14", "x15", "x16", "x17", "x18", "memory"
        }
    }
}

fn fiber_entry_point() {
    f: *Fiber = current_fiber
    f.func(f.arg)
    f.state = 2 
    active_fibers = active_fibers - 1
    std.task.yield_to_scheduler()
}

export [noinline]
fn std.task.yield_to_scheduler() {
    std.task.context_switch_barrier()
    std.task.swap_context(&(current_fiber.stack_ptr), scheduler_sp)
}

export [noinline]
fn std.task.resume(f: *Fiber) {
    current_fiber = f
    std.task.context_switch_barrier()
    std.task.swap_context(&scheduler_sp, f.stack_ptr)
}

export [noinline, noclone]
fn std.task.swap_context(old_sp_ptr: **u8, new_sp: *u8) {
    if Builtin.IsX86_64 {
        asm {
            "pushq %%rbp; pushq %%rbx; pushq %%rdi; pushq %%rsi; pushq %%r12; pushq %%r13; pushq %%r14; pushq %%r15"
            "movq %%rsp, (%0)"
            "movq %1, %%rsp"
            "popq %%r15; popq %%r14; popq %%r13; popq %%r12; popq %%rsi; popq %%rdi; popq %%rbx; popq %%rbp"
            : : "r"(old_sp_ptr), "r"(new_sp) : "memory"
        }
        return
    }

    if Builtin.IsAArch64 {
        asm {
            "stp x19, x20, [sp, #-16]!"
            "stp x21, x22, [sp, #-16]!"
            "stp x23, x24, [sp, #-16]!"
            "stp x25, x26, [sp, #-16]!"
            "stp x27, x28, [sp, #-16]!"
            "stp x29, x30, [sp, #-16]!"
            "mov x2, sp"
            "str x2, [%0]"
            "mov sp, %1"
            "ldp x29, x30, [sp], #16"
            "ldp x27, x28, [sp], #16"
            "ldp x25, x26, [sp], #16"
            "ldp x23, x24, [sp], #16"
            "ldp x21, x22, [sp], #16"
            "ldp x19, x20, [sp], #16"
            : : "r"(old_sp_ptr), "r"(new_sp) : "x2", "memory"
        }
    }
}

export fn std.task.spawn(gpa: *std.mem.HeapAllocator, taskFunc: fn(*void) -> void, arg: *void) !i32 {
    if cast(i64, gpa) == 0 {
        return error.InvalidArgument
    }

    if !std.task.is_supported() {
        return error.UnsupportedPlatform
    }

    if Builtin.IsX86_64 {
        stack_size: i64 = 65536
        raw_stack: *u8 = (std.mem.HeapAllocator.alloc(gpa, stack_size)) catch |stack_err| {
            return error.OutOfMemory
        }
        fiber_mem: *u8 = (std.mem.HeapAllocator.alloc(gpa, Builtin.sizeof(Fiber))) catch |fiber_err| {
            return error.OutOfMemory
        }
        f: *Fiber = cast(*Fiber, fiber_mem)
        
        f.func = taskFunc
        f.arg = arg
        f.state = 0
        f.next = cast(*Fiber, 0)
        f.wait_fd = -1
        f.wait_events = cast(i16, 0)
        f.wait_revents = cast(i16, 0)
        f.waiting_next = cast(*Fiber, 0)
        active_fibers = active_fibers + 1
        
        fiber_handle: *void = CreateFiber(stack_size, fiber_entry_point_windows, cast(*void, f))
        if cast(i64, fiber_handle) == 0 {
            return error.OutOfMemory
        }
        f.stack_ptr = cast(*u8, fiber_handle)
        
        std.task.enqueue(f)
        return 0
    } else if Builtin.IsAArch64 {
        stack_size: i64 = 65536
        raw_stack: *u8 = (std.mem.HeapAllocator.alloc(gpa, stack_size)) catch |stack_err| {
            return error.OutOfMemory
        }
        fiber_mem: *u8 = (std.mem.HeapAllocator.alloc(gpa, Builtin.sizeof(Fiber))) catch |fiber_err| {
            return error.OutOfMemory
        }
        f: *Fiber = cast(*Fiber, fiber_mem)

        f.func = taskFunc
        f.arg = arg
        f.state = 0
        f.next = cast(*Fiber, 0)
        f.wait_fd = -1
        f.wait_events = cast(i16, 0)
        f.wait_revents = cast(i16, 0)
        f.waiting_next = cast(*Fiber, 0)
        active_fibers = active_fibers + 1

        top: i64 = (cast(i64, raw_stack) + stack_size) & -16
        ctx: **u8 = cast(**u8, top - 96)
        ctx[0] = cast(*u8, 0)
        ctx[1] = cast(*u8, cast(i64, fiber_entry_point))
        ctx[2] = cast(*u8, 0)
        ctx[3] = cast(*u8, 0)
        ctx[4] = cast(*u8, 0)
        ctx[5] = cast(*u8, 0)
        ctx[6] = cast(*u8, 0)
        ctx[7] = cast(*u8, 0)
        ctx[8] = cast(*u8, 0)
        ctx[9] = cast(*u8, 0)
        ctx[10] = cast(*u8, 0)
        ctx[11] = cast(*u8, 0)
        f.stack_ptr = cast(*u8, ctx)

        std.task.enqueue(f)
        return 0
    } else {
        return error.UnsupportedPlatform
    }
}

export fn std.task.yield() {
    if !std.task.is_supported() {
        return
    }

    if cast(i64, current_fiber) == 0 {
        return
    }

    std.task.enqueue(current_fiber)
    std.task.yield_to_scheduler()
}

export fn std.task.has_ready_fibers() -> bool {
    if cast(i64, ready_queue_head) == 0 {
        return false
    }

    return true
}

export fn std.task.enqueue(f: *Fiber) {
    f.next = cast(*Fiber, 0)
    if cast(i64, ready_queue_tail) == 0 {
        ready_queue_head = f
        ready_queue_tail = f
    } else {
        ready_queue_tail.next = f
        ready_queue_tail = f
    }
}

export fn std.task.dequeue() -> *Fiber {
    if cast(i64, ready_queue_head) == 0 { return cast(*Fiber, 0) }
    f: *Fiber = ready_queue_head
    ready_queue_head = f.next
    if cast(i64, ready_queue_head) == 0 { ready_queue_tail = cast(*Fiber, 0) }
    return f
}
```

### `std/async.zorb`

```zorb
import "errors.zorb"
import "net.zorb"
import "task.zorb"

export struct std.async {}
waiting_head: *Fiber = cast(*Fiber, 0)

export fn std.async.is_supported() -> bool {
    if Builtin.IsBareMetal {
        return false
    }

    if !std.task.is_supported() {
        return false
    }

    return true
}

export fn std.async.init() {
    waiting_head = cast(*Fiber, 0)
}

export fn std.async.wait_readable(fd: i32) !i32 {
    result: i32 = std.async.wait_for_fd(fd, std.async.read_event()) catch |err| {
        return err
    }
    return result
}

export fn std.async.wait_writable(fd: i32) !i32 {
    result: i32 = std.async.wait_for_fd(fd, std.async.write_event()) catch |err| {
        return err
    }
    return result
}

export fn std.async.loop() {
    if !std.async.is_supported() {
        return
    }

    while active_fibers > 0 {
        while std.task.has_ready_fibers() {
            f: *Fiber = std.task.dequeue()
            if f.state == 2 { continue }
            std.task.resume(f)
        }

        if active_fibers <= 0 {
            return
        }

        if cast(i64, waiting_head) == 0 {
            return
        }

        std.async.poll_events()
    }
}

fn std.async.poll_events() {
    if cast(i64, waiting_head) == 0 {
        return
    }

    timeout_ms: i32 = 0
    if cast(i64, ready_queue_head) == 0 {
        timeout_ms = -1
    }

    poll_storage: [64]std.net.PollFd
    waiting_fibers: [64]i64
    count: i64 = 0
    cursor: *Fiber = waiting_head

    while cast(i64, cursor) != 0 && count < 64 {
        poll_storage[count].fd = cursor.wait_fd
        poll_storage[count].events = cursor.wait_events
        poll_storage[count].revents = cast(i16, 0)
        waiting_fibers[count] = cast(i64, cursor)
        cursor = cursor.waiting_next
        count = count + 1
    }

    ready: i64 = std.net.poll(poll_storage, count, timeout_ms)
    if ready <= 0 {
        return
    }

    i: i64 = 0
    while i < count {
        if poll_storage[i].revents != cast(i16, 0) {
            std.async.wake_waiting_fiber(cast(*Fiber, waiting_fibers[i]), poll_storage[i].revents)
        }
        i = i + 1
    }
}
```
