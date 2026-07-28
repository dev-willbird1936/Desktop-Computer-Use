// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Drawing2D;
using ShadowUse.Native;

namespace ShadowUse.Overlay;

/// <summary>
/// The virtual cursor: a purely cosmetic fake pointer that shows what the agent is doing.
/// Layered (per-pixel alpha), topmost, click-through, non-activating, excluded from
/// screen capture — it never touches the real cursor, focus, or input. Pure theater.
/// </summary>
internal sealed class VirtualCursorOverlay : IDisposable
{
    private readonly Thread _thread;
    private CursorForm? _form;
    private readonly ManualResetEventSlim _ready = new(false);
    private int _disposed;

    private static VirtualCursorOverlay? _instance;
    public static VirtualCursorOverlay Instance => _instance ??= new VirtualCursorOverlay();

    public VirtualCursorOverlay()
    {
        _thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            _form = new CursorForm();
            _ready.Set();
            Application.Run(_form);
        })
        { IsBackground = true, Name = "ShadowUse.CursorOverlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Move the virtual cursor to a screen point (animated), with an optional click pulse on arrival.</summary>
    public void MoveTo(int screenX, int screenY, bool clickPulse = false)
    {
        if (Volatile.Read(ref _disposed) != 0 || _form == null) return;
        try { _form.BeginInvoke(() => _form.AnimateTo(screenX, screenY, clickPulse)); } catch { }
    }

    /// <summary>Hide the virtual cursor.</summary>
    public void Hide()
    {
        if (Volatile.Read(ref _disposed) != 0 || _form == null) return;
        try { _form.BeginInvoke(() => _form.HideCursor()); } catch { }
    }

    private sealed class CursorForm : Form
    {
        private const int CursorSize = 28;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _idleTimer;
        private readonly CursorIdleState _idleState = new(TimeSpan.FromMinutes(1));
        private PointF _pos;            // current animated position (top-left of glyph)
        private PointF _target;
        private bool _pulseOnArrive;
        private float _pulseRadius = -1; // <0 = no pulse
        private DateTime _lastTick = DateTime.UtcNow;
        private bool _visible;
        private Bitmap? _frame;

        public CursorForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(CursorSize * 2, CursorSize * 2);
            TopMost = true;
            _animTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
            _animTimer.Tick += (_, _) => Tick();
            _idleTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
            _idleTimer.Tick += (_, _) =>
            {
                if (_idleState.ShouldHide(DateTimeOffset.UtcNow))
                    HideCursor();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCaptureExclusion();
        }

        private void ApplyCaptureExclusion()
        {
            // WDA_EXCLUDEFROMCAPTURE — ghost must not appear in screenshots/recordings
            if (!NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
                System.Diagnostics.Debug.WriteLine($"ghost: SetWindowDisplayAffinity failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_POPUP = unchecked((int)0x80000000);
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                            | NativeMethods.WS_EX_TRANSPARENT
                            | NativeMethods.WS_EX_TOPMOST
                            | NativeMethods.WS_EX_NOACTIVATE
                            | NativeMethods.WS_EX_TOOLWINDOW;
                cp.Style = WS_POPUP;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyCaptureExclusion();
            Visible = false; // start hidden; shown on first MoveTo
        }

        public void AnimateTo(int x, int y, bool clickPulse)
        {
            _idleState.RecordActivity(DateTimeOffset.UtcNow);
            _idleTimer.Start();
            if (!_visible)
            {
                _pos = new PointF(x, y); // first appearance: no cross-screen swoop
                _visible = true;
            }
            _target = new PointF(x, y);
            _pulseOnArrive = clickPulse;
            _animTimer.Start();
        }

        public void HideCursor()
        {
            _animTimer.Stop();
            _idleTimer.Stop();
            Visible = false;
            _visible = false;
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            float dt = Math.Clamp((float)(now - _lastTick).TotalSeconds, 0.001f, 0.05f);
            _lastTick = now;

            // critically-damped-ish spring toward target
            float dx = _target.X - _pos.X, dy = _target.Y - _pos.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            bool arrived = dist < 1.5f;
            if (arrived)
            {
                _pos = _target;
                if (_pulseOnArrive && _pulseRadius < 0) { _pulseRadius = 4; _pulseOnArrive = false; }
            }
            else
            {
                float speed = MathF.Max(400f, dist * 6f); // fast, eases in
                float step = MathF.Min(dist, speed * dt);
                _pos = new PointF(_pos.X + dx / dist * step, _pos.Y + dy / dist * step);
            }

            if (_pulseRadius >= 0)
            {
                _pulseRadius += 140 * dt;
                if (_pulseRadius > CursorSize * 1.2f) _pulseRadius = -1;
            }
            Render();

            // Only stop once truly settled — a pulse finishing while a newer AnimateTo
            // call is still mid-flight to a different target must not freeze the cursor
            // there until some later call happens to restart the timer.
            if (arrived && _pulseRadius < 0)
                _animTimer.Stop();
        }

        private void Render()
        {
            int w = CursorSize * 2, h = CursorSize * 2;
            _frame?.Dispose();
            _frame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int cx = CursorSize, cy = CursorSize;

                // click pulse ring
                if (_pulseRadius >= 0)
                {
                    float alpha = Math.Clamp(1f - _pulseRadius / (CursorSize * 1.2f), 0f, 1f);
                    using var pulsePen = new Pen(Color.FromArgb((int)(200 * alpha), 30, 255, 90), 2.5f);
                    g.DrawEllipse(pulsePen, cx - _pulseRadius, cy - _pulseRadius, _pulseRadius * 2, _pulseRadius * 2);
                }

                // cursor glyph: green-tinted pointer
                PointF[] glyph =
                [
                    new PointF(cx, cy), new PointF(cx, cy + 17), new PointF(cx + 4.5f, cy + 12.5f),
                    new PointF(cx + 7, cy + 19), new PointF(cx + 9.5f, cy + 18), new PointF(cx + 7, cy + 11.5f),
                    new PointF(cx + 12, cy + 11.5f),
                ];
                using var fill = new SolidBrush(Color.FromArgb(235, 30, 255, 90));
                using var outline = new Pen(Color.FromArgb(255, 10, 60, 20), 1.4f);
                g.FillPolygon(fill, glyph);
                g.DrawPolygon(outline, glyph);
            }

            // per-pixel alpha via UpdateLayeredWindow
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var memDc = NativeMethodsGdi.CreateCompatibleDC(screenDc);
            var hBitmap = _frame.GetHbitmap(Color.FromArgb(0));
            var old = NativeMethodsGdi.SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeMethods.SIZE { cx = w, cy = h };
                var src = new NativeMethods.POINT { X = 0, Y = 0 };
                var dst = new NativeMethods.POINT { X = (int)_pos.X - CursorSize, Y = (int)_pos.Y - CursorSize };
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                };
                if (!Visible && _visible)
                {
                    ApplyCaptureExclusion();
                    Visible = true;
                    NativeMethods.SetWindowPos(Handle, (IntPtr)(-1), dst.X, dst.Y, w, h,
                        (uint)(NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW));
                }
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                NativeMethodsGdi.SelectObject(memDc, old);
                NativeMethodsGdi.DeleteObject(hBitmap);
                NativeMethodsGdi.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _animTimer.Dispose(); _idleTimer.Dispose(); _frame?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _form?.BeginInvoke(() => Application.ExitThread()); } catch { }
        _thread.Join(1000);
    }
}

internal sealed class CursorIdleState
{
    private readonly TimeSpan _idleTimeout;
    private DateTimeOffset? _lastActivity;

    public CursorIdleState(TimeSpan idleTimeout)
    {
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _idleTimeout = idleTimeout;
    }

    public void RecordActivity(DateTimeOffset now) => _lastActivity = now;

    public bool ShouldHide(DateTimeOffset now)
        => _lastActivity is DateTimeOffset lastActivity
            && now - lastActivity >= _idleTimeout;
}

internal static partial class NativeMethodsGdi
{
    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr ho);

    [System.Runtime.InteropServices.LibraryImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);
}
