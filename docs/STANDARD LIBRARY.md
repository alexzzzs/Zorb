# Zorb Standard Library

The standard library is ordinary Zorb source in [`runtime/std/`](../runtime/std/).
Import the modules used by a program:

```zorb
import "std/io.zorb"
import "std/os.zorb"
```

This guide summarizes the public API. The source files are the definitive
reference for signatures and implementation details.

## Support

Support is module and target specific:

- `std.os`, `std.io`, `std.str`, and `std.mem` are the base library.
- `std.fs` is supported on hosted Linux and Windows.
- `std.net` provides low-level TCP sockets and readiness polling on hosted
  Linux and Windows.
- `std.process` supports hosted Linux and Windows, plus freestanding Linux
  targets that provide the documented syscall ABI.
- `std.task` is available where `std.task.is_supported()` returns `true`.
- `std.async` requires both `std.task` and `std.net`; check
  `std.async.is_supported()` before using it.

Unsupported operations return `error.UnsupportedPlatform` where their
signatures allow errors. Check the module's `is_supported()` function before
using target-specific facilities.

## Modules

### Errors — `std/errors.zorb`

The module exports a marker type and error codes for allocation, bounds,
I/O, and platform failures. Use the names as `error.Name` in error unions.

`OutOfMemory`, `InvalidSize`, `InvalidArgument`, `BufferTooSmall`,
`OutOfBounds`, `Overflow`, `NullPointer`, `AlreadyExists`, `NotFound`,
`WouldBlock`, `EndOfFile`, `IOError`, `UnsupportedPlatform`,
`NotImplemented`, and `TimedOut`.

### Operating system — `std/os.zorb`

- Platform and architecture queries: `is_linux`, `is_windows`,
  `is_bare_metal`, `is_x86_64`, `is_aarch64`, `platform_name`, `arch_name`.
- Process control: `exit`.
- Page allocation: `get_pages`, `free_pages`.
- Hosted monotonic clock: `monotonic_millis`.
- Legacy numeric platform code: `get_type`.

### Process and arguments — `std/process.zorb`

- Borrow native arguments with `from_native`, then inspect them with `len` and
  `at`.
- Check support with `is_supported`.
- Start a child with `spawn`; inspect and wait for it with `is_active` and
  `wait`.
- `run` starts a child and waits for it, inheriting standard input, output, and
  error.

### Input and output — `std/io.zorb`

- Text output: `print`, `eprint`, `println`, `eprintln`.
- Scalar output: `print_bool`, `print_i64`, `eprint_i64`, `println_i64`,
  `eprintln_i64`.
- File descriptor I/O: `write(fd, buf)` and `read(fd, buf)`.
- `set_stdout_fd` and `write_stdout` support low-level runtime setup.

### Strings — `std/str.zorb`

`reverse`, `eql`, `slice_eql`, `starts_with`, `ends_with`, `copy`,
`from_i64`, `from_u64`, and `find_byte` provide slice and string helpers.
`copy` and the integer formatters return the number of bytes written.

### Memory — `std/mem.zorb`

`zero` and `copy` operate on byte slices. `HeapAllocator.init`, `alloc`, and
`deinit` provide a simple heap allocator; `alloc` returns a pointer to the
requested byte range.

### Files — `std/fs.zorb`

`open_read`, `open_write`, `close`, `exists`, `size`, `read_all`, `write_all`,
`delete`, and `rename` provide hosted file operations. `read_all` takes a
`std.mem.HeapAllocator`; the other helpers use file paths and descriptors.

### Networking — `std/net.zorb`

- `is_supported` reports platform support.
- IPv4 helpers: `htons`, `htonl`, `ipv4`, `sockaddr_v4`,
  `sockaddr_v4_loopback`, and `sockaddr_v4_any`.
- Socket operations: `socket`, `socket_tcp_v4`, `bind_v4`, `listen`, `accept`,
  `connect_v4`, `send`, `recv`, and `close`.
- Readiness polling: `poll`, `poll_readable`, and `poll_writable`.

These are low-level APIs. Callers own socket descriptors and must handle
platform error results.

### Tasks — `std/task.zorb`

`Fiber` describes a task. `is_supported` reports availability; `spawn` starts a
fiber, and `yield` gives control back to the scheduler. `has_ready_fibers`,
`enqueue`, `dequeue`, and `dispose_dead_fiber` expose scheduler operations for
runtime code.

### Async I/O — `std/async.zorb`

`is_supported` reports availability and `init` initializes the event loop.
`wait_readable` and `wait_writable` wait for socket readiness; their timeout
variants accept milliseconds. `send_exact` and `recv_exact` transfer a full
buffer, with corresponding timeout variants. `loop` runs scheduled work.

### Collections — `std/collections.zorb`

- `ByteList`: `init`, `reserve`, `append_byte`, `append_slice`,
  `append_string`, and `slice`.
- `StringBuilder`: `init`, `append`, `append_slice`, `append_i64`, `slice`, and
  `c_string`.
- `StringInterner`: `init` and `intern`; `hash_bytes` supplies its byte hash.

Collection methods that allocate take a `std.mem.HeapAllocator` explicitly.

## Example

```zorb
import "std/io.zorb"
import "std/os.zorb"

fn _start() {
    std.io.println("hello from Zorb")
    std.os.exit(0)
}
```

More complete programs are in [`examples/`](../examples/). For language-level
error handling and imports, see the [semantics guide](SEMANTICS.md).
