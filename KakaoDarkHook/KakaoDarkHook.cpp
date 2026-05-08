// KakaoDarkHook.cpp — 카카오톡 PC 다크모드 후킹 DLL (inline 후킹 버전)
//
// 동작 원리:
//   1. C# 인젝터가 LoadLibraryW로 이 DLL을 카카오톡 프로세스에 주입한다.
//   2. DllMain → InitThread에서 gdi32.dll / user32.dll 함수의 첫 5바이트를
//      `JMP our_hook` 으로 직접 패치 (inline 후킹).
//   3. trampoline 페이지에 원본 5바이트 + `JMP target+5` 를 두어 hook 함수가
//      원본을 그대로 호출할 수 있게 한다.
//   4. 후킹된 함수들이 흰 무채색 → 다크, 검정 무채색 → 밝은 색으로 색만 치환.
//      이미지(BitBlt 계열)는 후킹하지 않으므로 사진/이모티콘 원본 보존.
//
// IAT 후킹과의 차이: IAT는 카카오톡이 import한 함수만 잡는데, 카카오톡은
// 핵심 그리기 함수를 IAT가 아닌 GetProcAddress / 자체 정적 라이브러리로 호출한다.
// inline 후킹은 함수 자체를 패치하므로 어떤 경로로 호출돼도 잡힌다.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdint.h>
#include <stdio.h>
#include <ctype.h>
#include <string.h>

#pragma comment(lib, "psapi.lib")

// ============================================================================
// 색 변환 정책
// ============================================================================

static inline int Luma(BYTE r, BYTE g, BYTE b) {
    return (r * 299 + g * 587 + b * 114) / 1000;
}

static inline int Sat(BYTE r, BYTE g, BYTE b) {
    BYTE mx = r; if (g > mx) mx = g; if (b > mx) mx = b;
    BYTE mn = r; if (g < mn) mn = g; if (b < mn) mn = b;
    return mx - mn;
}

// 배경(밝은 무채색)을 다크로. 채도 있는 색·이미 어두운 색은 그대로.
static COLORREF DarkifyBg(COLORREF c) {
    if (c == CLR_INVALID) return c;
    BYTE r = GetRValue(c), g = GetGValue(c), b = GetBValue(c);
    int sat = Sat(r, g, b);
    int luma = Luma(r, g, b);
    if (sat <= 25 && luma >= 200) return RGB(30, 30, 30);  // 흰색 → 진한 회색
    if (sat <= 25 && luma >= 150) return RGB(45, 45, 45);  // 밝은 회색 → 어두운 회색
    if (sat <= 25 && luma >= 110) return RGB(70, 70, 70);
    return c;
}

// 텍스트(어두운 무채색)을 밝게. 채도 있는 색·이미 밝은 색은 그대로.
static COLORREF DarkifyFg(COLORREF c) {
    if (c == CLR_INVALID) return c;
    BYTE r = GetRValue(c), g = GetGValue(c), b = GetBValue(c);
    int sat = Sat(r, g, b);
    int luma = Luma(r, g, b);
    if (sat <= 25 && luma <= 80)  return RGB(230, 230, 230);
    if (sat <= 25 && luma <= 130) return RGB(180, 180, 180);
    return c;
}

// ============================================================================
// 함수 시그니처 + trampoline
// ============================================================================

typedef COLORREF (WINAPI *SetTextColor_t)(HDC, COLORREF);
typedef COLORREF (WINAPI *SetBkColor_t)(HDC, COLORREF);
typedef int      (WINAPI *FillRect_t)(HDC, const RECT*, HBRUSH);
typedef BOOL     (WINAPI *ExtTextOutW_t)(HDC, int, int, UINT, const RECT*, LPCWSTR, UINT, const INT*);
typedef BOOL     (WINAPI *ExtTextOutA_t)(HDC, int, int, UINT, const RECT*, LPCSTR, UINT, const INT*);
typedef HBRUSH   (WINAPI *CreateSolidBrush_t)(COLORREF);

// "Real_*" = trampoline pointer. hook 안에서 원본을 호출할 때 사용.
static SetTextColor_t     Real_SetTextColor     = nullptr;
static SetBkColor_t       Real_SetBkColor       = nullptr;
static FillRect_t         Real_FillRect         = nullptr;
static ExtTextOutW_t      Real_ExtTextOutW      = nullptr;
static ExtTextOutA_t      Real_ExtTextOutA      = nullptr;
static CreateSolidBrush_t Real_CreateSolidBrush = nullptr;

static volatile LONG g_hooked = 0;

// ============================================================================
// 후킹 함수들
// ============================================================================

static COLORREF WINAPI Hook_SetTextColor(HDC hdc, COLORREF c) {
    return Real_SetTextColor(hdc, DarkifyFg(c));
}

static COLORREF WINAPI Hook_SetBkColor(HDC hdc, COLORREF c) {
    return Real_SetBkColor(hdc, DarkifyBg(c));
}

static int WINAPI Hook_FillRect(HDC hdc, const RECT* lprc, HBRUSH hbr) {
    if (hbr) {
        LOGBRUSH lb;
        ZeroMemory(&lb, sizeof(lb));
        if (GetObject(hbr, sizeof(lb), &lb) && lb.lbStyle == BS_SOLID) {
            COLORREF newC = DarkifyBg(lb.lbColor);
            if (newC != lb.lbColor) {
                HBRUSH dark = CreateSolidBrush(newC);
                if (dark) {
                    int rv = Real_FillRect(hdc, lprc, dark);
                    DeleteObject(dark);
                    return rv;
                }
            }
        }
    }
    return Real_FillRect(hdc, lprc, hbr);
}

// ExtTextOutW — 텍스트 그리기 진입점. SetTextColor가 이미 후킹되어 있으니
// 색 자체는 거기서 다크화되지만, 일부 카카오톡 코드는 SetTextColor 안 거치고
// 매번 임시 DC에 색만 박고 호출할 수 있어 한 번 더 강제.
static BOOL WINAPI Hook_ExtTextOutW(HDC hdc, int x, int y, UINT options, const RECT* lprect,
                                    LPCWSTR str, UINT cnt, const INT* dx)
{
    COLORREF orig = GetTextColor(hdc);
    COLORREF newCol = DarkifyFg(orig);
    if (newCol != orig) Real_SetTextColor(hdc, newCol);
    BOOL r = Real_ExtTextOutW(hdc, x, y, options, lprect, str, cnt, dx);
    if (newCol != orig) Real_SetTextColor(hdc, orig);
    return r;
}

static BOOL WINAPI Hook_ExtTextOutA(HDC hdc, int x, int y, UINT options, const RECT* lprect,
                                    LPCSTR str, UINT cnt, const INT* dx)
{
    COLORREF orig = GetTextColor(hdc);
    COLORREF newCol = DarkifyFg(orig);
    if (newCol != orig) Real_SetTextColor(hdc, newCol);
    BOOL r = Real_ExtTextOutA(hdc, x, y, options, lprect, str, cnt, dx);
    if (newCol != orig) Real_SetTextColor(hdc, orig);
    return r;
}

// ============================================================================
// Inline 후킹 — x86 5-byte JMP patch + trampoline
// ============================================================================

// trampoline 페이지: 원본 함수의 prologue 5바이트 + JMP back to target+5.
// hook 함수가 trampoline을 호출하면 원본 prologue → target+5로 점프 → 원본 함수 정상 실행.
struct Trampoline {
    BYTE  prologue[5];
    BYTE  jmpOpcode;       // 0xE9
    int32_t jmpRel;        // (target+5) - (trampoline+10)
};
#pragma pack(push, 1)
struct PatchJmp {
    BYTE  opcode;          // 0xE9
    int32_t rel;           // hookFn - (target+5)
};
#pragma pack(pop)

static void* AllocTrampolinePage()
{
    // 실행 가능한 메모리 페이지 1개. 여러 trampoline을 한 페이지에 모아둔다.
    static BYTE* pageBase = nullptr;
    static SIZE_T offset  = 0;
    static const SIZE_T pageSize = 4096;

    if (!pageBase || offset + sizeof(Trampoline) > pageSize) {
        pageBase = (BYTE*)VirtualAlloc(NULL, pageSize, MEM_COMMIT | MEM_RESERVE,
                                       PAGE_EXECUTE_READWRITE);
        offset = 0;
        if (!pageBase) return nullptr;
    }
    void* slot = pageBase + offset;
    offset += sizeof(Trampoline);
    return slot;
}

// 32-bit inline hook 설치. target 첫 5바이트가 atomic instruction boundary임을 가정한다.
// (대부분의 Windows API는 hot-patch prologue `mov edi,edi; push ebp; mov ebp,esp` 5바이트.)
static bool InstallInlineHook(void* target, void* hookFn, void** outTrampoline)
{
    if (!target || !hookFn) return false;

    Trampoline* tr = (Trampoline*)AllocTrampolinePage();
    if (!tr) return false;

    // 1) 원본 prologue 5바이트 → trampoline.prologue
    memcpy(tr->prologue, target, 5);
    // 2) trampoline 끝에 JMP target+5
    tr->jmpOpcode = 0xE9;
    tr->jmpRel = (int32_t)((BYTE*)target + 5 - (BYTE*)&tr->jmpRel - 4);

    // 3) target 첫 5바이트를 JMP hookFn 으로 패치
    PatchJmp patch;
    patch.opcode = 0xE9;
    patch.rel = (int32_t)((BYTE*)hookFn - (BYTE*)target - 5);

    DWORD oldProtect = 0;
    if (!VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &oldProtect)) return false;
    memcpy(target, &patch, 5);
    DWORD tmp;
    VirtualProtect(target, 5, oldProtect, &tmp);
    FlushInstructionCache(GetCurrentProcess(), target, 5);

    *outTrampoline = tr;
    return true;
}

struct InlineHookSpec {
    const char* dll;
    const char* func;
    void*       hook;
    void**      tramp;     // 결과 trampoline 저장 위치 (Real_*)
};

static InlineHookSpec g_inlineHooks[] = {
    { "gdi32.dll",  "SetTextColor", (void*)Hook_SetTextColor, (void**)&Real_SetTextColor },
    { "gdi32.dll",  "SetBkColor",   (void*)Hook_SetBkColor,   (void**)&Real_SetBkColor   },
    { "gdi32.dll",  "ExtTextOutW",  (void*)Hook_ExtTextOutW,  (void**)&Real_ExtTextOutW  },
    { "gdi32.dll",  "ExtTextOutA",  (void*)Hook_ExtTextOutA,  (void**)&Real_ExtTextOutA  },
    { "user32.dll", "FillRect",     (void*)Hook_FillRect,     (void**)&Real_FillRect     },
};

static int InstallAllInlineHooks()
{
    int installed = 0;
    HMODULE hGdi  = GetModuleHandleA("gdi32.dll");
    HMODULE hUser = GetModuleHandleA("user32.dll");

    for (auto& h : g_inlineHooks) {
        HMODULE mod = nullptr;
        if (_stricmp(h.dll, "gdi32.dll") == 0) mod = hGdi;
        else if (_stricmp(h.dll, "user32.dll") == 0) mod = hUser;
        if (!mod) continue;

        void* target = (void*)GetProcAddress(mod, h.func);
        if (!target) continue;

        void* tramp = nullptr;
        if (InstallInlineHook(target, h.hook, &tramp)) {
            *h.tramp = tramp;
            installed++;
        }
    }
    return installed;
}

// ============================================================================
// 화면 강제 갱신 — 후킹 직후 카카오톡 모든 창에 redraw 보내 다크로 다시 그리게 한다.
// ============================================================================

static BOOL CALLBACK RedrawProc(HWND hwnd, LPARAM /*lparam*/)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == GetCurrentProcessId()) {
        RedrawWindow(hwnd, NULL, NULL,
                     RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }
    return TRUE;
}

static DWORD WINAPI InitThread(LPVOID /*param*/)
{
    if (InterlockedExchange(&g_hooked, 1) != 0) return 0;

    // 카카오톡이 안정 상태 들어가도록 약간 대기
    Sleep(50);

    int installed = 0;
    __try {
        installed = InstallAllInlineHooks();
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // 후킹 실패해도 카카오톡은 정상 동작
    }

    if (installed > 0) {
        // 후킹 즉시 모든 카카오톡 창 강제 redraw → 다크 적용
        EnumWindows(RedrawProc, 0);
    }
    return 0;
}

// ============================================================================
// DllMain
// ============================================================================

extern "C" __declspec(dllexport) void __stdcall InstallHooks()
{
    InitThread(nullptr);
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hInst);
        // DllMain에서 직접 작업하면 로더 락 위험. 별도 스레드로 분리.
        HANDLE h = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
    return TRUE;
}
