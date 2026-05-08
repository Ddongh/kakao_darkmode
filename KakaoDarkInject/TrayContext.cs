using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace KakaoDarkInject;

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Icon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _watchTimer;
    private readonly HashSet<int> _injectedPids = new();
    private readonly string _dllPath;

    public TrayContext()
    {
        _trayIcon = BuildTrayIcon();
        _dllPath = ResolveDllPath();

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("상태: 카카오톡 감시 중") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var injectNow = new ToolStripMenuItem("지금 주입");
        injectNow.Click += (_, _) => InjectAllNow(showSuccessToast: true);
        menu.Items.Add(injectNow);

        var openFolder = new ToolStripMenuItem("설치 폴더 열기");
        openFolder.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(_dllPath);
            if (dir != null && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
        };
        menu.Items.Add(openFolder);

        menu.Items.Add(new ToolStripSeparator());

        var info = new ToolStripMenuItem("도움말 / 알려진 한계");
        info.Click += (_, _) => MessageBox.Show(
            "KakaoDarkInject\n\n" +
            "동작: 카카오톡 프로세스에 KakaoDarkHook.dll을 주입해 GDI 그리기 함수를 가로채고\n" +
            "      흰 배경 → 다크, 검정 텍스트 → 밝은 색으로 치환합니다.\n\n" +
            "한계:\n" +
            " • 카카오톡을 종료하고 다시 켜면 다시 주입해야 합니다.\n" +
            "   (이 프로그램이 실행 중이면 자동 감시·재주입합니다.)\n" +
            " • 카카오 약관상 클라이언트 변조에 해당할 수 있습니다 (사용 책임은 본인).\n" +
            " • Windows Defender/백신이 DLL 주입을 오탐할 수 있습니다.\n" +
            " • 카카오톡 업데이트로 그리기 패턴이 바뀌면 후킹이 일부만 동작할 수 있습니다.\n\n" +
            $"DLL 경로: {_dllPath}",
            "KakaoDarkInject", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(info);

        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("종료");
        quit.Click += (_, _) => ExitThread();
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Text = "KakaoDarkInject",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) InjectAllNow(true); };

        // 5초마다 새 카카오톡 프로세스 감시 → 자동 주입
        _watchTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _watchTimer.Tick += (_, _) => WatchTick();
        _watchTimer.Start();

        // 시작 시 한 번 주입 시도
        InjectAllNow(showSuccessToast: false);
    }

    private void InjectAllNow(bool showSuccessToast)
    {
        if (!File.Exists(_dllPath))
        {
            _icon.ShowBalloonTip(5000, "KakaoDarkInject",
                $"DLL을 찾을 수 없습니다:\n{_dllPath}\n\nKakaoDarkHook.dll을 같은 폴더에 두세요.",
                ToolTipIcon.Error);
            UpdateStatus("DLL 없음");
            return;
        }

        Process[] procs;
        try { procs = Process.GetProcessesByName("KakaoTalk"); }
        catch { procs = Array.Empty<Process>(); }

        if (procs.Length == 0)
        {
            UpdateStatus("카카오톡이 실행 중이지 않음");
            return;
        }

        int newInjections = 0;
        var errors = new List<string>();
        foreach (var p in procs)
        {
            try
            {
                if (_injectedPids.Contains(p.Id)) continue;
                var r = Injector.Inject(p.Id, _dllPath);
                if (r.Success)
                {
                    _injectedPids.Add(p.Id);
                    newInjections++;
                }
                else
                {
                    errors.Add($"PID {p.Id}: {r.Error}");
                }
            }
            catch (Exception e) { errors.Add($"PID {p.Id}: {e.Message}"); }
            finally { p.Dispose(); }
        }

        if (newInjections > 0)
        {
            UpdateStatus($"주입됨 ({_injectedPids.Count}개 프로세스)");
            if (showSuccessToast)
                _icon.ShowBalloonTip(2500, "KakaoDarkInject",
                    $"{newInjections}개 카카오톡 프로세스에 다크모드 적용", ToolTipIcon.Info);
        }
        else if (_injectedPids.Count > 0)
        {
            UpdateStatus($"주입됨 ({_injectedPids.Count}개 프로세스)");
        }

        if (errors.Count > 0 && showSuccessToast)
        {
            _icon.ShowBalloonTip(5000, "KakaoDarkInject - 일부 실패",
                string.Join("\n", errors), ToolTipIcon.Warning);
        }
    }

    private void WatchTick()
    {
        // 죽은 PID 정리 + 새 PID 발견 시 주입
        var alivePids = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName("KakaoTalk"))
            {
                alivePids.Add(p.Id);
                p.Dispose();
            }
        }
        catch { return; }

        // 종료된 PID 제거
        _injectedPids.RemoveWhere(pid => !alivePids.Contains(pid));

        // 새 PID 있으면 주입
        if (alivePids.Any(pid => !_injectedPids.Contains(pid)))
            InjectAllNow(showSuccessToast: false);
        else if (alivePids.Count == 0)
            UpdateStatus("카카오톡이 실행 중이지 않음");
    }

    private void UpdateStatus(string text)
    {
        _statusItem.Text = "상태: " + text;
    }

    private static string ResolveDllPath()
    {
        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "KakaoDarkHook.dll"),
            Path.Combine(exeDir, "..", "..", "..", "..", "KakaoDarkHook", "KakaoDarkHook.dll"),
            Path.Combine(exeDir, "..", "KakaoDarkHook", "KakaoDarkHook.dll"),
        };
        foreach (var c in candidates)
        {
            try { var full = Path.GetFullPath(c); if (File.Exists(full)) return full; }
            catch { }
        }
        return Path.Combine(exeDir, "KakaoDarkHook.dll");
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _watchTimer.Stop();
            _watchTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _trayIcon.Dispose();
        }
        catch { }
        base.ExitThreadCore();
    }

    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(28, 28, 28));
            g.FillRectangle(bg, 0, 0, 16, 16);
            using var pen = new Pen(Color.FromArgb(100, 100, 100));
            g.DrawRectangle(pen, 0, 0, 15, 15);
            using var f = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(245, 222, 64));
            g.DrawString("Kh", f, fg, new RectangleF(0, 2, 16, 14));
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
