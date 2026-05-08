using System.Runtime.InteropServices;
using KakaoDark.Native;

namespace KakaoDark.Core;

/// 시스템 전역 low-level mouse hook (WH_MOUSE_LL).
/// 마우스 down/up 이벤트가 OS 메시지 큐에 들어가는 순간 콜백 호출.
public sealed class MouseHook : IDisposable
{
    public delegate void MouseEventHandler(int screenX, int screenY);
    public event MouseEventHandler? LeftDown;
    public event MouseEventHandler? LeftUp;
    public event MouseEventHandler? Wheel;

    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_MOUSEWHEEL  = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint  mouseData;
        public uint  flags;
        public uint  time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelMouseProc? _proc;
    private IntPtr _hookHandle;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string? lpModuleName);

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _proc = HookProc;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandleA(null), 0);
    }

    public void Uninstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP
                || msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                try
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    switch (msg)
                    {
                        case WM_LBUTTONDOWN: LeftDown?.Invoke(data.pt.x, data.pt.y); break;
                        case WM_LBUTTONUP:   LeftUp?.Invoke(data.pt.x, data.pt.y); break;
                        case WM_MOUSEWHEEL:
                        case WM_MOUSEHWHEEL: Wheel?.Invoke(data.pt.x, data.pt.y); break;
                    }
                }
                catch { }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
