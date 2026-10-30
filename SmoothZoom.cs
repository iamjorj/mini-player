using System;

namespace MiniPlayer;

/// <summary>
/// Smooth, aspect-locked resizing of a window (used by the mini player and the welcome window).
/// Pinch: the size follows the fingers exactly (DirectManipulation is already smooth).
/// Wheel: each notch moves a target size, and every frame the real size glides toward it.
/// Either way the point under the cursor stays in place, and the window stays on screen.
/// Frames come from FrameClock, synced to the screen refresh.
/// </summary>
internal sealed class SmoothZoom : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Func<double> _aspect;
    private readonly double _minWidthDip, _minHeightDip;
    private readonly FrameClock _clock = new();
    private readonly TouchpadPinch? _pinch;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("MINIPLAYER_TRACE") == "1";
    private double _zoomTarget, _zoomCurrent;     // window width in pixels
    private Native.POINT _zoomAnchor;             // screen point that stays under the cursor
    private double _anchorFx, _anchorFy;          // where that point sits inside the window (0..1)
    private bool _gliding;
    private long _lastFrame;
    private double _lastApplyMs;
    private bool _inSession;

    /// <summary>The window that actually gets resized (defaults to the one receiving input).</summary>
    public IntPtr Target { get; set; }

    /// <summary>Raised before the first resize of a pinch/wheel zoom, and once it has fully settled.</summary>
    public event Action? ZoomStarting, ZoomEnded;

    public SmoothZoom(IntPtr hwnd, Func<double> aspect, double minWidthDip, double minHeightDip)
    {
        _hwnd = hwnd;
        Target = hwnd;
        _aspect = aspect;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _clock.Tick += OnFrame;

        try
        {
            _pinch = new TouchpadPinch(hwnd);
            _pinch.Pinched += factor => ZoomBy(factor, followFingers: true);
        }
        catch (Exception error)
        {
            _pinch = null; // fall back to Ctrl+wheel pinch
            App.Log($"Smooth touchpad pinch unavailable (step {TouchpadPinch.Step}): {error.Message}");
        }
    }

    /// <summary>Feed window messages here; handles the wheel and touchpad gestures. True if handled.</summary>
    public bool HandleMessage(int msg, IntPtr wParam)
    {
        switch (msg)
        {
            case Native.WM_MOUSEWHEEL:
                int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ZoomBy(Math.Exp(delta / 120.0 * 0.15), followFingers: false); // one notch = ~16%
                return true;

            case TouchpadPinch.DM_POINTERHITTEST:
                if (_pinch?.OnPointerHitTest(wParam) != true)
                    return false;
                _clock.Start();
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        _clock.Stop();
        _pinch?.Dispose();
    }

    /// <summary>For WM_SIZING: keep the window's aspect ratio while its edges are dragged.</summary>
    public static void KeepAspectWhileResizing(IntPtr wParam, IntPtr lParam, double aspect)
    {
        const int Top = 3, TopLeft = 4, TopRight = 5, Bottom = 6; // WMSZ_* edges
        int edge = wParam.ToInt32();
        var r = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.RECT>(lParam);

        if (edge is Top or Bottom)
        {
            r.Right = r.Left + (int)Math.Round(r.Height * aspect);
        }
        else
        {
            int height = (int)Math.Round(r.Width / aspect);
            if (edge is TopLeft or TopRight)
                r.Top = r.Bottom - height;
            else
                r.Bottom = r.Top + height;
        }

        System.Runtime.InteropServices.Marshal.StructureToPtr(r, lParam, false);
    }

    private void OnFrame()
    {
        if (_pinch?.Active == true)
            _pinch.Poll(); // may call ZoomBy(followFingers: true)

        if (_gliding)
            GlideStep();

        if (!_gliding && _pinch?.Active != true)
        {
            _clock.Stop();
            if (_inSession)
            {
                _inSession = false;
                ZoomEnded?.Invoke();
            }
        }
    }

    private void ZoomBy(double factor, bool followFingers)
    {
        if (!_inSession)
        {
            _inSession = true;
            ZoomStarting?.Invoke(); // may switch Target
        }

        Native.GetWindowRect(Target, out var r);
        Native.GetCursorPos(out var cursor);
        _anchorFx = Math.Clamp((cursor.X - r.Left) / (double)r.Width, 0, 1);
        _anchorFy = Math.Clamp((cursor.Y - r.Top) / (double)r.Height, 0, 1);
        _zoomAnchor = new Native.POINT
        {
            X = r.Left + (int)Math.Round(_anchorFx * r.Width),
            Y = r.Top + (int)Math.Round(_anchorFy * r.Height),
        };

        if (!_gliding)
            _zoomCurrent = _zoomTarget = r.Width;

        double aspect = _aspect();
        var work = Native.WorkAreaFor(Target);
        double dpi = Native.GetDpiForWindow(Target) / 96.0;
        double minWidth = Math.Max(_minWidthDip, _minHeightDip * aspect) * dpi;
        double maxWidth = Math.Min(work.Width, work.Height * aspect);
        _zoomTarget = Math.Clamp(_zoomTarget * factor, minWidth, Math.Max(minWidth, maxWidth));

        if (followFingers)
        {
            _gliding = false;
            _zoomCurrent = _zoomTarget;
            ApplyZoom();
        }
        else if (!_gliding)
        {
            _gliding = true;
            _lastFrame = 0;
            _clock.Start();
        }
    }

    private void GlideStep()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _lastFrame == 0 ? 1 / 60.0 : (now - _lastFrame) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastFrame = now;

        // Ease toward the target (frame-rate independent).
        _zoomCurrent += (_zoomTarget - _zoomCurrent) * (1 - Math.Exp(-dt * 12));
        if (Math.Abs(_zoomTarget - _zoomCurrent) < 0.5)
        {
            _zoomCurrent = _zoomTarget;
            _gliding = false;
        }
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        int w = (int)Math.Round(_zoomCurrent);
        int h = (int)Math.Round(_zoomCurrent / _aspect());
        int x = (int)Math.Round(_zoomAnchor.X - _anchorFx * w);
        int y = (int)Math.Round(_zoomAnchor.Y - _anchorFy * h);

        var work = Native.WorkAreaFor(Target);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.SetWindowPos(Target, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (Trace)
        {
            double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double end = ms(System.Diagnostics.Stopwatch.GetTimestamp());
            App.Log($"TRACE gap={ms(t0) - _lastApplyMs:F1} setpos={end - ms(t0):F1} w={w}");
            _lastApplyMs = end;
        }
    }
}
