using System.Windows.Forms;
using KakaoDark.Native;

namespace KakaoDark;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 단일 인스턴스 보장
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, "Local\\KakaoDark.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        if (!Magnification.MagInitialize())
        {
            MessageBox.Show(
                "Magnification API 초기화에 실패했습니다.\nWindows 환경(원격 데스크톱·일부 가상화 환경에서는 동작하지 않을 수 있음)을 확인하세요.",
                "KakaoDark",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        try
        {
            Application.Run(new TrayContext());
        }
        finally
        {
            Magnification.MagUninitialize();
            GC.KeepAlive(mutex);
        }
    }
}
