using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KakaoDarkInject;

/// <summary>
/// 카카오톡 프로세스에 KakaoDarkHook.dll을 주입한다.
/// 표준 LoadLibraryW + CreateRemoteThread 방식.
/// </summary>
internal static class Injector
{
    private const uint PROCESS_CREATE_THREAD     = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION      = 0x0008;
    private const uint PROCESS_VM_WRITE          = 0x0020;
    private const uint PROCESS_VM_READ           = 0x0010;
    private const uint PROCESS_ACCESS = PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION
                                      | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ;

    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, uint size, uint type, uint protect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, uint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, uint size, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, uint stack, IntPtr start,
                                                    IntPtr param, uint flags, IntPtr tid);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

    /// <summary>
    /// 주어진 PID에 DLL을 주입한다. 이미 주입돼 있어도 큰 문제 없이 LoadLibrary가 ref count만 올림.
    /// </summary>
    public static InjectResult Inject(int pid, string dllPath)
    {
        if (!System.IO.File.Exists(dllPath))
            return InjectResult.Fail($"DLL 파일을 찾을 수 없음: {dllPath}");

        IntPtr proc = OpenProcess(PROCESS_ACCESS, false, pid);
        if (proc == IntPtr.Zero)
            return InjectResult.Fail($"OpenProcess 실패 (PID {pid}). 관리자 권한이 필요할 수 있음. error={Marshal.GetLastWin32Error()}");

        try
        {
            // 인젝터와 대상 프로세스 비트수 일치 여부 검사 (디버그 정보 제공용)
            if (IsWow64Process(proc, out bool targetWow64))
            {
                bool selfWow64;
                IsWow64Process(System.Diagnostics.Process.GetCurrentProcess().Handle, out selfWow64);
                bool selfIs32 = IntPtr.Size == 4 || selfWow64;
                bool targetIs32 = targetWow64 || (IntPtr.Size == 8 && !targetWow64 && Environment.Is64BitOperatingSystem == false);
                // 더 정확한 판정: 대상이 32bit 프로세스면 IsWow64Process가 true (64bit OS에서)
                bool mismatch = (selfIs32 != targetWow64) && Environment.Is64BitOperatingSystem;
                if (mismatch)
                {
                    return InjectResult.Fail("인젝터와 카카오톡의 비트수가 다릅니다. KakaoDarkInject는 x86으로 빌드되어야 합니다.");
                }
            }

            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
            IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (uint)pathBytes.Length,
                                           MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                return InjectResult.Fail($"VirtualAllocEx 실패. error={Marshal.GetLastWin32Error()}");

            try
            {
                if (!WriteProcessMemory(proc, remote, pathBytes, (uint)pathBytes.Length, out _))
                    return InjectResult.Fail($"WriteProcessMemory 실패. error={Marshal.GetLastWin32Error()}");

                IntPtr kernel32 = GetModuleHandleA("kernel32.dll");
                IntPtr loadLibW = GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibW == IntPtr.Zero)
                    return InjectResult.Fail("LoadLibraryW 주소 획득 실패");

                IntPtr thread = CreateRemoteThread(proc, IntPtr.Zero, 0, loadLibW, remote, 0, IntPtr.Zero);
                if (thread == IntPtr.Zero)
                    return InjectResult.Fail($"CreateRemoteThread 실패. error={Marshal.GetLastWin32Error()}");

                try
                {
                    WaitForSingleObject(thread, 5000);
                }
                finally
                {
                    CloseHandle(thread);
                }

                return InjectResult.Ok();
            }
            finally
            {
                VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(proc);
        }
    }

    /// <summary>모든 KakaoTalk 프로세스에 주입.</summary>
    public static InjectResult InjectAll(string dllPath)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("KakaoTalk"); }
        catch (Exception e) { return InjectResult.Fail($"카카오톡 프로세스 조회 실패: {e.Message}"); }

        if (procs.Length == 0)
            return InjectResult.Fail("실행 중인 카카오톡 프로세스가 없습니다. 카카오톡을 먼저 실행하세요.");

        var failures = new List<string>();
        int success = 0;
        foreach (var p in procs)
        {
            try
            {
                var r = Inject(p.Id, dllPath);
                if (r.Success) success++;
                else failures.Add($"PID {p.Id}: {r.Error}");
            }
            catch (Exception e)
            {
                failures.Add($"PID {p.Id}: {e.Message}");
            }
            finally { p.Dispose(); }
        }

        if (success == 0)
            return InjectResult.Fail("모든 카카오톡 프로세스 주입 실패:\n" + string.Join("\n", failures));

        if (failures.Count > 0)
            return InjectResult.Ok($"{success}개 주입 성공. 일부 실패:\n" + string.Join("\n", failures));

        return InjectResult.Ok($"{success}개 카카오톡 프로세스에 주입 완료");
    }
}

internal readonly struct InjectResult
{
    public bool Success { get; }
    public string? Error { get; }
    public string? Message { get; }

    private InjectResult(bool ok, string? err, string? msg)
    {
        Success = ok; Error = err; Message = msg;
    }

    public static InjectResult Ok(string? msg = null) => new(true, null, msg);
    public static InjectResult Fail(string err) => new(false, err, null);
}
