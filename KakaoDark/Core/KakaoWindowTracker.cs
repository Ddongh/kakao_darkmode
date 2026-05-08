using System.Diagnostics;
using System.Text;
using KakaoDark.Native;

namespace KakaoDark.Core;

/// 카카오톡 프로세스의 모든 가시 top-level EVA_* 창을 추적한다.
/// 위치/포커스/종료 이벤트가 발생하면 현재 창 목록을 다시 enumerate해서
/// `KakaoWindowsChanged` 이벤트로 전달한다. UI 스레드로 마샬링됨.
public sealed class KakaoWindowTracker : IDisposable
{
    public event Action<List<KakaoWindowInfo>>? KakaoWindowsChanged;
    /// 드래그·리사이즈 시작·종료 — hwnd, isDragging
    public event Action<IntPtr, bool>? DragStateChanged;

    private IntPtr _hookLocation;
    private IntPtr _hookForeground;
    private IntPtr _hookDestroy;
    private IntPtr _hookMinimize;
    private IntPtr _hookShow;
    private IntPtr _hookReorder;
    private IntPtr _hookMoveSize;
    private WinEventDelegate? _winEventDelegate;
    private readonly System.Windows.Forms.Timer _findTimer;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly SynchronizationContext _syncCtx;
    private bool _enabled;
    private bool _pauseWhenInactive;
    private HashSet<uint> _kakaoPidsCache = new();
    private DateTime _pidCacheStamp = DateTime.MinValue;
    private bool _lastReportedAny;
    private HashSet<IntPtr> _trackedHwnds = new(); // 마지막으로 보고한 카카오 top-level 창 hwnd 집합

    public KakaoWindowTracker()
    {
        _syncCtx = SynchronizationContext.Current
                   ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
        // 16ms(60Hz) 폴링 — WinEventHook의 OUTOFCONTEXT 메시지 lag을 우회.
        // SyncTo는 변경분만 처리하므로 위치 변경 없으면 SetWindowPos도 호출되지 않음.
        // 부담은 EnumWindows 호출 정도(~0.5ms × 60Hz ≒ 3% CPU 미만).
        _findTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _findTimer.Tick += (_, _) => ForceReevaluate();
        // 16ms 디바운스: 짧은 시간에 몰린 이벤트는 한 번만 Reevaluate.
        _debounceTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            Reevaluate();
        };
    }

    private void ScheduleReevaluate()
    {
        if (!_enabled) return;
        if (!_debounceTimer.Enabled) _debounceTimer.Start();
    }

    public bool PauseWhenInactive
    {
        get => _pauseWhenInactive;
        set
        {
            if (_pauseWhenInactive == value) return;
            _pauseWhenInactive = value;
            ForceReevaluate();
        }
    }

    public void Start()
    {
        if (_enabled) return;
        _enabled = true;

        _winEventDelegate = OnWinEvent;

        _hookLocation = User32.SetWinEventHook(
            User32.EVENT_OBJECT_LOCATIONCHANGE, User32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookForeground = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookDestroy = User32.SetWinEventHook(
            User32.EVENT_OBJECT_DESTROY, User32.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookMinimize = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookShow = User32.SetWinEventHook(
            User32.EVENT_OBJECT_SHOW, User32.EVENT_OBJECT_HIDE,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookReorder = User32.SetWinEventHook(
            User32.EVENT_OBJECT_REORDER, User32.EVENT_OBJECT_REORDER,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _hookMoveSize = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MOVESIZESTART, User32.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _winEventDelegate, 0, 0,
            User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);

        _findTimer.Start();
        ForceReevaluate();
    }

    public void ForceReevaluate()
    {
        if (!_enabled) return;
        Reevaluate();
    }

    public void Stop()
    {
        if (!_enabled) return;
        _enabled = false;
        _findTimer.Stop();
        _debounceTimer.Stop();
        UnhookAll();
        if (_lastReportedAny)
        {
            _lastReportedAny = false;
            KakaoWindowsChanged?.Invoke(new List<KakaoWindowInfo>());
        }
    }

    private void UnhookAll()
    {
        if (_hookLocation != IntPtr.Zero) { User32.UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }
        if (_hookForeground != IntPtr.Zero) { User32.UnhookWinEvent(_hookForeground); _hookForeground = IntPtr.Zero; }
        if (_hookDestroy != IntPtr.Zero) { User32.UnhookWinEvent(_hookDestroy); _hookDestroy = IntPtr.Zero; }
        if (_hookMinimize != IntPtr.Zero) { User32.UnhookWinEvent(_hookMinimize); _hookMinimize = IntPtr.Zero; }
        if (_hookShow != IntPtr.Zero) { User32.UnhookWinEvent(_hookShow); _hookShow = IntPtr.Zero; }
        if (_hookReorder != IntPtr.Zero) { User32.UnhookWinEvent(_hookReorder); _hookReorder = IntPtr.Zero; }
        if (_hookMoveSize != IntPtr.Zero) { User32.UnhookWinEvent(_hookMoveSize); _hookMoveSize = IntPtr.Zero; }
    }

    private HashSet<uint> GetKakaoPids()
    {
        var now = DateTime.UtcNow;
        if ((now - _pidCacheStamp).TotalMilliseconds < 1000)
            return _kakaoPidsCache;

        var pids = new HashSet<uint>();
        Process[] procs;
        try { procs = Process.GetProcessesByName("KakaoTalk"); }
        catch { return _kakaoPidsCache; }

        try
        {
            foreach (var p in procs)
            {
                try { pids.Add((uint)p.Id); } catch { }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }

        _kakaoPidsCache = pids;
        _pidCacheStamp = now;
        return pids;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != User32.OBJID_WINDOW) return;

        if (eventType == User32.EVENT_SYSTEM_MOVESIZESTART)
        {
            if (_trackedHwnds.Contains(hwnd))
            {
                Post(() => { DragStateChanged?.Invoke(hwnd, true); Reevaluate(); });
            }
            return;
        }
        if (eventType == User32.EVENT_SYSTEM_MOVESIZEEND)
        {
            if (_trackedHwnds.Contains(hwnd))
            {
                Post(() => { DragStateChanged?.Invoke(hwnd, false); Reevaluate(); });
            }
            return;
        }

        // 비-카카오 프로세스 이벤트는 빠르게 거른다 (foreground/reorder는 z-order 추적 위해 통과).
        if (eventType == User32.EVENT_OBJECT_LOCATIONCHANGE
            || eventType == User32.EVENT_OBJECT_SHOW
            || eventType == User32.EVENT_OBJECT_HIDE
            || eventType == User32.EVENT_OBJECT_DESTROY)
        {
            if (hwnd == IntPtr.Zero) return;
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            var pids = GetKakaoPids();
            if (!pids.Contains(pid)) return;
        }

        // z-order 변경(클릭으로 인한 활성화)은 디바운스 없이 즉시 처리.
        if (eventType == User32.EVENT_SYSTEM_FOREGROUND
            || eventType == User32.EVENT_OBJECT_REORDER)
        {
            Post(Reevaluate);
            return;
        }

        // 추적 중인 카카오 top-level 창의 위치 변경도 즉시 처리 → 드래그 시 호스트가 한 박자 안 늦음.
        // 자식 컨트롤(메시지 항목 추가·스크롤 등)의 LOCATIONCHANGE는 디바운스로 처리.
        if (eventType == User32.EVENT_OBJECT_LOCATIONCHANGE && _trackedHwnds.Contains(hwnd))
        {
            Post(Reevaluate);
            return;
        }

        Post(ScheduleReevaluate);
    }

    private void Reevaluate()
    {
        if (!_enabled) return;

        var pids = GetKakaoPids();
        if (pids.Count == 0)
        {
            EmitEmpty();
            return;
        }

        if (_pauseWhenInactive)
        {
            var fg = User32.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                User32.GetWindowThreadProcessId(fg, out uint fgPid);
                if (!pids.Contains(fgPid))
                {
                    EmitEmpty();
                    return;
                }
            }
        }

        var list = EnumerateKakaoVisibleWindows(pids);
        if (list.Count == 0)
        {
            EmitEmpty();
            return;
        }

        // 다음 LOCATIONCHANGE 즉시 처리용으로 hwnd 집합 갱신.
        var newSet = new HashSet<IntPtr>(list.Count);
        foreach (var w in list) newSet.Add(w.Hwnd);
        _trackedHwnds = newSet;

        _lastReportedAny = true;
        KakaoWindowsChanged?.Invoke(list);
    }

    private void EmitEmpty()
    {
        _trackedHwnds.Clear();
        if (!_lastReportedAny) return;
        _lastReportedAny = false;
        KakaoWindowsChanged?.Invoke(new List<KakaoWindowInfo>());
    }

    private static List<KakaoWindowInfo> EnumerateKakaoVisibleWindows(HashSet<uint> pids)
    {
        var result = new List<KakaoWindowInfo>();
        var sb = new StringBuilder(256);

        User32.EnumWindows((hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd)) return true;
            if (User32.IsIconic(hwnd)) return true;

            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains(pid)) return true;

            if (!User32.GetWindowRect(hwnd, out RECT r)) return true;
            // 트레이 알림·IME 후보·작은 시스템 popup 거른다.
            // 카카오톡 하단 광고 popup(380×91)도 다크 처리 대상이라 height는 50까지 낮춤.
            if (r.Width < 200 || r.Height < 50) return true;

            // 클래스명은 카카오톡 빌드/창 종류마다 달라 (EVA_Window_Dblclk, EVA_ChildWindow, RichEditClass,
            // #32770 등) 필터하지 않는다. 일부 비-UI 시스템 클래스만 거른다.
            sb.Clear();
            User32.GetClassName(hwnd, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls == "IME" || cls == "MSCTFIME UI" || cls == "Default IME") return true;

            result.Add(new KakaoWindowInfo(hwnd, r));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private void Post(Action a) => _syncCtx.Post(_ => a(), null);

    public void Dispose()
    {
        Stop();
        _findTimer.Dispose();
        _debounceTimer.Dispose();
    }
}

public readonly record struct KakaoWindowInfo(IntPtr Hwnd, RECT Rect);
