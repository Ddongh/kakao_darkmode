using System.Windows.Forms;

namespace KakaoDarkInject;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new System.Threading.Mutex(initiallyOwned: true,
            "Local\\KakaoDarkInject.SingleInstance", out bool createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new TrayContext());
        }
        finally
        {
            GC.KeepAlive(mutex);
        }
    }
}
