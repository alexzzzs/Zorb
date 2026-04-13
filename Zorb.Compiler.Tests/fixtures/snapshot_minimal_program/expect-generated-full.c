#include <stdint.h>
#ifdef _WIN32
    #include <windows.h>
#endif

#if defined(__linux__)
    #define __zorb_builtin_is_linux 1
#else
    #define __zorb_builtin_is_linux 0
#endif
#if defined(_WIN32)
    #define __zorb_builtin_is_windows 1
#else
    #define __zorb_builtin_is_windows 0
#endif
#if defined(__x86_64__) || defined(_M_X64)
    #define __zorb_builtin_is_x86_64 1
#else
    #define __zorb_builtin_is_x86_64 0
#endif
#if defined(__aarch64__) || defined(_M_ARM64)
    #define __zorb_builtin_is_aarch64 1
#else
    #define __zorb_builtin_is_aarch64 0
#endif

#if defined(__linux__) || defined(__unix__) || defined(__APPLE__)

#if defined(__x86_64__)
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    int64_t ret;
    register int64_t r10 __asm__("r10") = a4;
    register int64_t r8  __asm__("r8")  = a5;
    register int64_t r9  __asm__("r9")  = a6;
    __asm__ volatile (
        "syscall"
        : "=a"(ret)
        : "a"(n), "D"(a1), "S"(a2), "d"(a3), "r"(r10), "r"(r8), "r"(r9)
        : "rcx", "r11", "memory"
    );
    return ret;
}
#elif defined(__aarch64__)
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    register int64_t x8 __asm__("x8") = n;
    register int64_t x0 __asm__("x0") = a1;
    register int64_t x1 __asm__("x1") = a2;
    register int64_t x2 __asm__("x2") = a3;
    register int64_t x3 __asm__("x3") = a4;
    register int64_t x4 __asm__("x4") = a5;
    register int64_t x5 __asm__("x5") = a6;
    __asm__ volatile (
        "svc #0"
        : "+r"(x0)
        : "r"(x8), "r"(x1), "r"(x2), "r"(x3), "r"(x4), "r"(x5)
        : "memory"
    );
    return x0;
}
#else
static int64_t __zorb_syscall(int64_t n, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6) {
    (void)n; (void)a1; (void)a2; (void)a3; (void)a4; (void)a5; (void)a6;
    return -38;
}
#endif

#define SYSCALL_GET_8TH(_1,_2,_3,_4,_5,_6,_7,NAME,...) NAME
#define syscall(...) SYSCALL_GET_8TH(__VA_ARGS__, __syscall7, __syscall6, __syscall5, __syscall4, __syscall3, __syscall2, __syscall1)(__VA_ARGS__)

#define __syscall1(n) __zorb_syscall((int64_t)n, 0, 0, 0, 0, 0, 0)
#define __syscall2(n, a) __zorb_syscall((int64_t)n, (int64_t)a, 0, 0, 0, 0, 0)
#define __syscall3(n, a, b) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, 0, 0, 0, 0)
#define __syscall4(n, a, b, c) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, 0, 0, 0)
#define __syscall5(n, a, b, c, d) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, 0, 0)
#define __syscall6(n, a, b, c, d, e) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, (int64_t)e, 0)
#define __syscall7(n, a, b, c, d, e, f) __zorb_syscall((int64_t)n, (int64_t)a, (int64_t)b, (int64_t)c, (int64_t)d, (int64_t)e, (int64_t)f)


#endif
int64_t add(int64_t a, int64_t b);
int64_t main();

#ifdef _WIN32
extern int64_t GetStdHandle(int32_t nStdHandle);
extern int32_t WriteFile(int64_t h, int8_t* buf, uint32_t len, uint32_t* written, int64_t overlapped);
extern int32_t ExitProcess(uint32_t uExitCode);
extern uint8_t* VirtualAlloc(uint8_t* addr, int64_t size, uint32_t type, uint32_t protect);
#endif

int64_t add(int64_t a, int64_t b) {
    return (a + b);
}

int64_t main() {
    return add(1, 2);
}

