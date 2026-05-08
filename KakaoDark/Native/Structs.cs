using System.Runtime.InteropServices;

namespace KakaoDark.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public override string ToString() => $"RECT({Left},{Top},{Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MAGCOLOREFFECT
{
    public fixed float transform[25];
}

public delegate void WinEventDelegate(
    IntPtr hWinEventHook,
    uint eventType,
    IntPtr hwnd,
    int idObject,
    int idChild,
    uint dwEventThread,
    uint dwmsEventTime);
