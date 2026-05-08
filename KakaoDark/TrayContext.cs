using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KakaoDark.Core;
using KakaoDark.Native;

namespace KakaoDark;

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly KakaoWindowTracker _tracker;
    private readonly Dictionary<IntPtr, MagnifierHost> _hosts = new();
    private readonly Icon _trayIcon;
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "KakaoDark_diagnostic.log");
    private string _lastLoggedSnapshot = "";
    private readonly MouseHook _mouseHook = new();
    private const int HeaderHeightPx = 50; // 카카오톡 타이틀바 추정 높이
    private const int AdjacencyPx = 30;    // host 인접 판단 임계값
    private readonly HashSet<IntPtr> _dragGroup = new();

    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _presetSimple;
    private readonly ToolStripMenuItem _presetSmart;
    private readonly ToolStripMenuItem _presetSoft;

    private bool _enabled = true;
    private ColorPreset _preset = ColorPreset.SmartInvert;

    public TrayContext()
    {
        _trayIcon = BuildTrayIcon();

        _tracker = new KakaoWindowTracker { PauseWhenInactive = false };
        _tracker.KakaoWindowsChanged += OnKakaoWindowsChanged;
        _tracker.DragStateChanged += (hwnd, dragging) =>
        {
            if (dragging) BeginDragGroup(hwnd);
            else EndDragGroup();
        };

        // 헤더 영역 마우스 down → BeginDrag 그룹 트리거 (OS 이벤트 보강)
        _mouseHook.LeftDown += OnGlobalLeftDown;
        _mouseHook.LeftUp += OnGlobalLeftUp;
        // 마우스 휠 / 클릭 발생 시 카카오 영역 안이면 매그니파이어 짧게 invalidate burst (스크롤 갱신용)
        _mouseHook.Wheel += (sx, sy) => TriggerRefreshIfInside(sx, sy, 300);
        _mouseHook.Install();

        var menu = new ContextMenuStrip();

        _toggleItem = new ToolStripMenuItem("다크모드 ON") { Checked = true };
        _toggleItem.Click += (_, _) => SetEnabled(!_enabled);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());

        _presetSimple = MakePresetItem("단순 반전", ColorPreset.SimpleInvert);
        _presetSmart  = MakePresetItem("스마트 인버전", ColorPreset.SmartInvert);
        _presetSoft   = MakePresetItem("부드러운 다크", ColorPreset.SoftDark);
        _presetSmart.Checked = true;
        menu.Items.Add(_presetSimple);
        menu.Items.Add(_presetSmart);
        menu.Items.Add(_presetSoft);
        menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("종료");
        quit.Click += (_, _) => ExitThread();
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Text = "KakaoDark",
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        _tracker.Start();
    }

    private ToolStripMenuItem MakePresetItem(string label, ColorPreset preset)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => SetPreset(preset);
        return item;
    }

    private void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _toggleItem.Checked = enabled;
        _toggleItem.Text = enabled ? "다크모드 ON" : "다크모드 OFF";

        if (!enabled)
            foreach (var host in _hosts.Values) host.HideOverlay();
        else
            _tracker.ForceReevaluate();
    }

    private void SetPreset(ColorPreset preset)
    {
        _preset = preset;
        _presetSimple.Checked = preset == ColorPreset.SimpleInvert;
        _presetSmart.Checked  = preset == ColorPreset.SmartInvert;
        _presetSoft.Checked   = preset == ColorPreset.SoftDark;
        foreach (var host in _hosts.Values)
            host.ApplyPreset(preset);
    }

    private void OnGlobalLeftDown(int sx, int sy)
    {
        if (!_enabled) return;
        try
        {
            foreach (var kv in _hosts)
            {
                if (!User32.GetWindowRect(kv.Key, out RECT r)) continue;
                if (sx < r.Left || sx >= r.Right) continue;
                if (sy < r.Top  || sy >= r.Top + HeaderHeightPx) continue;
                BeginDragGroup(kv.Key);
                return;
            }
        }
        catch { }
    }

    private void OnGlobalLeftUp(int sx, int sy)
    {
        EndDragGroup();
        // 마우스 떼는 순간 클릭 결과로 화면이 갱신될 수 있어 burst
        TriggerRefreshIfInside(sx, sy, 200);
    }

    private void TriggerRefreshIfInside(int sx, int sy, int durationMs)
    {
        foreach (var kv in _hosts)
        {
            if (!User32.GetWindowRect(kv.Key, out RECT r)) continue;
            if (sx < r.Left || sx >= r.Right) continue;
            if (sy < r.Top  || sy >= r.Bottom) continue;
            kv.Value.TriggerRefresh(durationMs);
            return;
        }
    }

    // 메인 + 인접한 owned popup(예: 메인 하단 광고)을 한 그룹으로 묶어 BeginDrag.
    // 메인 헤더를 잡고 옮길 때 광고도 같이 따라가므로 drag 중 검정 처리도 함께 일관되게.
    private void BeginDragGroup(IntPtr seedHwnd)
    {
        if (!_hosts.ContainsKey(seedHwnd)) return;
        if (!User32.GetWindowRect(seedHwnd, out RECT seedRect)) return;

        // 이전 그룹 잔여 정리
        EndDragGroup();

        _dragGroup.Add(seedHwnd);
        _hosts[seedHwnd].BeginDrag();

        // 인접한 다른 host 찾기 (메인 RECT 가장자리 ±AdjacencyPx 안에 붙은 popup)
        foreach (var kv in _hosts)
        {
            if (kv.Key == seedHwnd) continue;
            if (!User32.GetWindowRect(kv.Key, out RECT other)) continue;
            if (RectsAdjacent(seedRect, other, AdjacencyPx))
            {
                _dragGroup.Add(kv.Key);
                kv.Value.BeginDrag();
            }
        }
    }

    private void EndDragGroup()
    {
        if (_dragGroup.Count == 0) return;
        foreach (var hwnd in _dragGroup)
        {
            if (_hosts.TryGetValue(hwnd, out var host))
                host.EndDrag();
        }
        _dragGroup.Clear();
    }

    // 두 RECT가 겹치거나(포함 포함) 가장자리에서 threshold 픽셀 이내로 닿아 있으면 그룹으로 판단.
    // 광고 popup이 메인 RECT 안에 포함된 경우도 잡기 위해 겹침 검사 추가.
    private static bool RectsAdjacent(RECT a, RECT b, int threshold)
    {
        // 직접 겹침 또는 포함
        bool xOverlap = a.Left < b.Right && a.Right > b.Left;
        bool yOverlap = a.Top < b.Bottom && a.Bottom > b.Top;
        if (xOverlap && yOverlap) return true;

        // 수직 인접 (a 위·아래에 b 붙음)
        if (xOverlap)
        {
            if (Math.Abs(a.Bottom - b.Top) <= threshold) return true;
            if (Math.Abs(b.Bottom - a.Top) <= threshold) return true;
        }
        // 수평 인접
        if (yOverlap)
        {
            if (Math.Abs(a.Right - b.Left) <= threshold) return true;
            if (Math.Abs(b.Right - a.Left) <= threshold) return true;
        }
        return false;
    }

    private void OnKakaoWindowsChanged(List<KakaoWindowInfo> windows)
    {
        LogSnapshot(windows);

        if (!_enabled)
        {
            foreach (var host in _hosts.Values) host.HideOverlay();
            return;
        }

        var seen = new HashSet<IntPtr>(windows.Count);
        foreach (var w in windows)
        {
            seen.Add(w.Hwnd);
            bool isNew = false;
            if (!_hosts.TryGetValue(w.Hwnd, out var host))
            {
                host = new MagnifierHost();
                _ = host.Handle;
                host.ApplyPreset(_preset);
                _hosts[w.Hwnd] = host;
                isNew = true;
            }
            host.SyncTo(w.Hwnd, w.Rect);
            // 새로 만든 host (예: 작업표시줄에서 카카오톡 처음 띄울 때 화면 밖→안으로 들어옴)
            // 의 첫 1초 burst refresh로 매그니파이어 캡처가 즉시 안정화되게.
            if (isNew) host.TriggerRefresh(1000);
        }

        if (_hosts.Count > seen.Count)
        {
            var stale = _hosts.Keys.Where(h => !seen.Contains(h)).ToList();
            foreach (var hwnd in stale)
            {
                if (_hosts.TryGetValue(hwnd, out var host))
                {
                    host.HideOverlay();
                    host.Dispose();
                    _hosts.Remove(hwnd);
                }
            }
        }
    }

    private void LogSnapshot(List<KakaoWindowInfo> windows)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] tracked=").Append(windows.Count);
            foreach (var w in windows)
            {
                User32.GetWindowRect(w.Hwnd, out RECT live);
                sb.Append(" | hwnd=0x").Append(w.Hwnd.ToInt64().ToString("X"))
                  .Append(" liveRect=(").Append(live.Left).Append(',').Append(live.Top).Append(")-(").Append(live.Right).Append(',').Append(live.Bottom).Append(')');
                if (_hosts.TryGetValue(w.Hwnd, out var host))
                {
                    var hr = host.LastRect;
                    sb.Append(" hostCache=(").Append(hr.Left).Append(',').Append(hr.Top).Append(")-(").Append(hr.Right).Append(',').Append(hr.Bottom).Append(')');
                    // 실제 OS의 호스트 위치 — 캐시와 다르면 SetWindowPos가 효과 없는 것
                    User32.GetWindowRect(host.Handle, out RECT actual);
                    sb.Append(" hostActual=(").Append(actual.Left).Append(',').Append(actual.Top).Append(")-(").Append(actual.Right).Append(',').Append(actual.Bottom).Append(')');
                    sb.Append(" magVis=").Append(host.MagControlVisible);
                    if (!string.IsNullOrEmpty(host.LastSyncDebug))
                        sb.Append(" DEBUG[").Append(host.LastSyncDebug).Append(']');
                }
            }
            string snap = sb.ToString();
            int after = snap.IndexOf("] ");
            string snapNoTime = after >= 0 ? snap.Substring(after + 2) : snap;
            if (snapNoTime == _lastLoggedSnapshot) return;
            _lastLoggedSnapshot = snapNoTime;
            File.AppendAllText(_logPath, snap + Environment.NewLine);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _mouseHook.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _tracker.Dispose();
            foreach (var host in _hosts.Values) host.Dispose();
            _hosts.Clear();
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
            using var pen = new Pen(Color.FromArgb(80, 80, 80));
            g.DrawRectangle(pen, 0, 0, 15, 15);
            using var f = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(245, 222, 64));
            g.DrawString("K", f, fg, new RectangleF(2, 2, 14, 14));
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
