using System.Runtime.InteropServices;

namespace KakaoDark.Native;

internal static class Magnification
{
    public const string WC_MAGNIFIER = "Magnifier";

    public const uint MS_SHOWMAGNIFIEDCURSOR = 0x0001;
    public const uint MS_CLIPAROUNDCURSOR    = 0x0002;
    public const uint MS_INVERTCOLORS        = 0x0004;

    public const int MW_FILTERMODE_EXCLUDE = 0;
    public const int MW_FILTERMODE_INCLUDE = 1;

    [DllImport("Magnification.dll")]
    public static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);

    [DllImport("Magnification.dll")]
    public static extern bool MagGetWindowSource(IntPtr hwnd, out RECT pRect);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, [In] IntPtr[] pHWND);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetColorEffect(IntPtr hwnd, ref MAGCOLOREFFECT pEffect);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM pTransform);

    [DllImport("Magnification.dll")]
    public static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MAGTRANSFORM
{
    public fixed float v[9];
}
