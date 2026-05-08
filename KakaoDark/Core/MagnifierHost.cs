using System.Drawing;
using System.Windows.Forms;
using KakaoDark.Native;

namespace KakaoDark.Core;

/// 가장 단순한 형태 — 카카오톡 hwnd 위에 layered + transparent 호스트를 띄우고
/// 자식 매그니파이어 컨트롤이 카카오톡 영역을 캡처해 색반전을 적용한다.
public sealed class MagnifierHost : Form
{
    public static int OverlayPadding = 0;

    private IntPtr _magHwnd;
    private IntPtr _kakaoHwnd;
    private MAGCOLOREFFECT _currentEffect = ColorPresets.SmartInvert();
    private bool _magInitialized;
    private bool _shown;
    private RECT _lastRect;
    private int _activePadding; // 드래그 중에만 양수 (10px), 평소엔 0
    private System.Windows.Forms.Timer? _refreshTimer; // burst invalidate — trigger 시에만 일정 시간 동작
    private DateTime _refreshUntilUtc = DateTime.MinValue;

    public MagnifierHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        Visible = false;
        Bounds = new Rectangle(-32000, -32000, 100, 100);
        DoubleBuffered = false;
        BackColor = Color.Black;
    }

    protected override bool ShowWithoutActivation => true;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= unchecked((int)(
                User32.WS_EX_LAYERED |
                User32.WS_EX_TRANSPARENT |
                User32.WS_EX_TOOLWINDOW |
                User32.WS_EX_NOACTIVATE));
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);

        _magHwnd = User32.CreateWindowEx(
            0,
            Magnification.WC_MAGNIFIER,
            "KakaoDarkMagnifier",
            User32.WS_CHILD | User32.WS_VISIBLE,
            0, 0, ClientSize.Width, ClientSize.Height,
            Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_magHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create magnifier control");

        var excludeList = new[] { Handle };
        Magnification.MagSetWindowFilterList(_magHwnd, Magnification.MW_FILTERMODE_EXCLUDE,
                                             excludeList.Length, excludeList);
        Magnification.MagSetColorEffect(_magHwnd, ref _currentEffect);
        _magInitialized = true;

        // burst 갱신용 — 평소엔 정지, TriggerRefresh 호출 시 일정 시간만 30Hz로 invalidate.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _refreshUntilUtc)
            {
                _refreshTimer!.Stop();
                return;
            }
            if (_shown && _magHwnd != IntPtr.Zero && _activePadding == 0)
                User32.InvalidateRect(_magHwnd, IntPtr.Zero, false);
        };
    }

    /// 외부에서 스크롤·클릭·키 입력 등을 감지했을 때 호출 — 일정 시간 동안 매그니파이어를 강제 invalidate.
    public void TriggerRefresh(int durationMs = 300)
    {
        if (_refreshTimer == null) return;
        var newUntil = DateTime.UtcNow.AddMilliseconds(durationMs);
        if (newUntil > _refreshUntilUtc) _refreshUntilUtc = newUntil;
        if (!_refreshTimer.Enabled) _refreshTimer.Start();
    }

    public RECT LastRect => _lastRect;
    public IntPtr KakaoHwnd => _kakaoHwnd;
    public bool IsShown => _shown;
    public bool MagControlVisible => _magHwnd != IntPtr.Zero && User32.IsWindowVisible(_magHwnd);
    public string LastSyncDebug { get; private set; } = "";

    public void SyncTo(IntPtr kakaoHwnd, RECT kakaoRect)
    {
        if (!_magInitialized) return;
        if (kakaoRect.Width <= 0 || kakaoRect.Height <= 0) return;

        _kakaoHwnd = kakaoHwnd;

        // 평소 padding 0, 드래그 중에만 _activePadding (10px) 적용
        int pad = _activePadding != 0 ? _activePadding : OverlayPadding;
        if (pad != 0)
        {
            kakaoRect = new RECT
            {
                Left   = kakaoRect.Left   - pad,
                Top    = kakaoRect.Top    - pad,
                Right  = kakaoRect.Right  + pad,
                Bottom = kakaoRect.Bottom + pad,
            };
        }

        // host를 카카오톡 z-order 바로 위에 두기
        var insertAfter = User32.GetWindow(kakaoHwnd, User32.GW_HWNDPREV);

        bool firstShow = !_shown;
        bool rectChanged = firstShow || !RectEquals(_lastRect, kakaoRect);
        bool sizeChanged = firstShow ||
                           _lastRect.Width != kakaoRect.Width ||
                           _lastRect.Height != kakaoRect.Height;

        // 위치/사이즈는 Form.SetBounds로 — WinForms 내부 _bounds 캐시도 함께 갱신.
        // 직접 Win32 SetWindowPos만 호출하면 WinForms _bounds가 stale 상태로 남아
        // 다른 이벤트에서 OS 위치를 옛 값으로 reset할 수 있음.
        if (rectChanged)
        {
            this.SetBounds(kakaoRect.Left, kakaoRect.Top, kakaoRect.Width, kakaoRect.Height);
        }

        // z-order만 별도 처리
        uint flags = User32.SWP_NOACTIVATE | User32.SWP_NOMOVE | User32.SWP_NOSIZE;
        if (firstShow) flags |= User32.SWP_SHOWWINDOW;
        bool ok = User32.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0, flags);

        int err = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        User32.GetWindowRect(Handle, out RECT immAfter);
        LastSyncDebug = $"swp={ok}/err={err} req=({kakaoRect.Left},{kakaoRect.Top})-({kakaoRect.Right},{kakaoRect.Bottom}) imm=({immAfter.Left},{immAfter.Top})-({immAfter.Right},{immAfter.Bottom}) rectChanged={rectChanged} firstShow={firstShow}";

        _shown = true;

        if (sizeChanged)
        {
            User32.SetWindowPos(_magHwnd, IntPtr.Zero, 0, 0,
                kakaoRect.Width, kakaoRect.Height,
                User32.SWP_NOACTIVATE | User32.SWP_NOZORDER);
        }

        if (rectChanged)
        {
            Magnification.MagSetWindowSource(_magHwnd, kakaoRect);
            _lastRect = kakaoRect;
        }
    }

    public void HideOverlay()
    {
        if (_shown)
        {
            User32.ShowWindow(Handle, User32.SW_HIDE);
            _shown = false;
        }
    }

    public void ApplyPreset(ColorPreset preset)
    {
        _currentEffect = ColorPresets.FromPreset(preset);
        if (_magInitialized)
            Magnification.MagSetColorEffect(_magHwnd, ref _currentEffect);
    }

    public void ApplyIdentity()
    {
        _currentEffect = ColorPresets.IdentityMatrix();
        if (_magInitialized)
            Magnification.MagSetColorEffect(_magHwnd, ref _currentEffect);
    }

    // 드래그 중: padding 20px + 매그니파이어 자식 hide → 호스트 BackColor=Black만 보임 (전체 블록).
    // 카카오톡 본체보다 20px 큰 검정 사각형이 따라가 분리 완전히 가림.
    public void BeginDrag()
    {
        _activePadding = 20;
        // 매그니파이어 자식 hide → 호스트 검정 배경 노출
        if (_magHwnd != IntPtr.Zero)
            User32.ShowWindow(_magHwnd, User32.SW_HIDE);
        // 호스트 검정 배경 invalidate (검정 fill 강제)
        User32.InvalidateRect(Handle, IntPtr.Zero, true);
        // 현재 카카오 RECT로 즉시 padding 적용
        if (_kakaoHwnd != IntPtr.Zero && User32.GetWindowRect(_kakaoHwnd, out RECT r))
            SyncTo(_kakaoHwnd, r);
    }

    // 드래그 종료: padding 0 + 매그니파이어 다시 show → 다크 복귀.
    public void EndDrag()
    {
        _activePadding = 0;
        // 카카오 RECT로 SetBounds 갱신 (padding 0 적용)
        if (_kakaoHwnd != IntPtr.Zero && User32.GetWindowRect(_kakaoHwnd, out RECT r))
            SyncTo(_kakaoHwnd, r);
        // 매그니파이어 다시 show + 새 캡처 영역 알리기
        if (_magHwnd != IntPtr.Zero)
        {
            User32.ShowWindow(_magHwnd, User32.SW_SHOWNOACTIVATE);
            if (_lastRect.Width > 0 && _lastRect.Height > 0)
                Magnification.MagSetWindowSource(_magHwnd, _lastRect);
        }
        // drag 직후 200ms 동안 burst 갱신으로 다크 화면 안정화
        TriggerRefresh(200);
    }

    private static bool RectEquals(in RECT a, in RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    protected override void Dispose(bool disposing)
    {
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _refreshTimer = null;
        }
        if (_magHwnd != IntPtr.Zero)
        {
            User32.DestroyWindow(_magHwnd);
            _magHwnd = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }
}
