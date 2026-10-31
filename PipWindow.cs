using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace MiniPlayer;

/// <summary>
/// Small always-on-top window that shows a live DWM preview of another app's window.
/// Drag to move, drag edges or pinch/scroll to resize (keeps the aspect ratio).
/// Click to interact with the app itself (pause, click links, type); moving the cursor away ends that.
///
/// This is a plain Win32 window on purpose (not WPF): resizing it is ~4x cheaper, which keeps pinch smooth,
/// and Windows only allows smooth touchpad pinch (DirectManipulation) on plain windows.
/// </summary>
public sealed class PipWindow
{
    public IntPtr Source { get; }
    public event EventHandler? Closed;

    private const int MinWidthDip = 160, MinHeightDip = 90, ResizeBorderDip = 6;

    private IntPtr _hwnd;
    private IntPtr _thumb;
    private double _aspect = 16.0 / 9.0;
    private readonly DispatcherTimer _watch;
    private SmoothZoom? _zoom;
    private bool _closing;

    private WndProcDelegate? _proc; // kept alive while the window exists
    private readonly string _className = "MiniPlayer" + Guid.NewGuid().ToString("N");

    // Original look of the real window, restored when the mini player closes.
    private bool _ghosted;
    private int _sourceExStyle;
    private bool _sourceHadAlpha;
    private uint _sourceColorKey, _sourceLayerFlags;
    private byte _sourceAlpha;

    public PipWindow(IntPtr source)
    {
        Source = source;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watch.Tick += (_, _) => WatchSource();
    }

    public void Show()
    {
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0x8, // CS_DBLCLKS
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
            hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);

        // Borderless popup; WS_THICKFRAME only so Windows handles edge resizing for us.
        _hwnd = CreateWindowEx(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW, _className, "Mini Player",
            WS_POPUP | WS_THICKFRAME, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        int round = 2; // Windows 11 rounded corners
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // A minimized window isn't drawn, so the preview would be empty.
        if (Native.IsIconic(Source))
            Native.ShowWindow(Source, Native.SW_SHOWNOACTIVATE);

        if (Native.DwmRegisterThumbnail(_hwnd, Source, out _thumb) != 0)
        {
            Close();
            return;
        }

        _aspect = SourceAspect() ?? _aspect;
        PlaceBottomRight();
        GhostSource();
        UpdateThumbnail();
        _watch.Start();
        _zoom = new SmoothZoom(_hwnd, () => _aspect, MinWidthDip, MinHeightDip);
    }

    private double? SourceAspect()
    {
        Native.GetClientRect(Source, out var rc);
        return rc.Width > 0 && rc.Height > 0 ? (double)rc.Width / rc.Height : null;
    }

    private void PlaceBottomRight()
    {
        var work = Native.WorkAreaFor(Source);
        int width = (int)(work.Width * 0.28);
        int height = (int)(width / _aspect);
        if (height > work.Height * 0.45)
        {
            height = (int)(work.Height * 0.45);
            width = (int)(height * _aspect);
        }

        int margin = 16;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST,
            work.Right - width - margin, work.Bottom - height - margin, width, height,
            Native.SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Hide the real window without letting it know it's hidden.
    /// Browsers (Chrome, Edge, Electron apps) stop drawing when fully covered, which freezes the preview.
    /// So instead we make the window fully see-through and click-through, but keep it on top:
    /// it keeps rendering, while you see and click whatever is underneath.
    /// </summary>
    private void GhostSource()
    {
        _sourceExStyle = Native.GetWindowLong(Source, Native.GWL_EXSTYLE);
        if ((_sourceExStyle & Native.WS_EX_LAYERED) != 0)
            _sourceHadAlpha = Native.GetLayeredWindowAttributes(Source, out _sourceColorKey, out _sourceAlpha, out _sourceLayerFlags);

        Native.GetWindowRect(Source, out _sourceHome);

        // Tag the window with its original style and position, so if this app is killed the next start can fix it.
        Native.SetProp(Source, GhostTag, new IntPtr(1));
        Native.SetProp(Source, GhostStyleTag, new IntPtr(_sourceExStyle));
        Native.SetProp(Source, GhostPosTag, new IntPtr(((long)_sourceHome.Left << 32) | (uint)_sourceHome.Top));

        Native.SetWindowLong(Source, Native.GWL_EXSTYLE,
            _sourceExStyle | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);
        Native.SetLayeredWindowAttributes(Source, 0, 1, Native.LWA_ALPHA);
        Native.SetWindowPos(Source, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _ghosted = true;

        // Hand keyboard focus to the next real window so you can keep working.
        for (IntPtr w = Native.GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = Native.GetWindow(w, Native.GW_HWNDNEXT))
        {
            if (w != Source && !Native.IsIconic(w) && App.CanPopOut(w))
            {
                Native.SetForegroundWindow(w);
                break;
            }
        }
    }

    /// <summary>Put the real window back exactly as it was.</summary>
    private void UnghostSource()
    {
        ExitInteract();
        if (!_ghosted || !Native.IsWindow(Source))
            return;
        _ghosted = false;

        if (_sourceMoved)
            Native.SetWindowPos(Source, IntPtr.Zero, _sourceHome.Left, _sourceHome.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        RestoreStyle(Source, _sourceExStyle);
        if (_sourceHadAlpha)
            Native.SetLayeredWindowAttributes(Source, _sourceColorKey, _sourceAlpha, _sourceLayerFlags);
    }

    private const string GhostTag = "MiniPlayer.Ghosted";
    private const string GhostStyleTag = "MiniPlayer.OriginalExStyle";
    private const string GhostPosTag = "MiniPlayer.OriginalPosition";

    private static void RestoreStyle(IntPtr window, int exStyle)
    {
        Native.RemoveProp(window, GhostPosTag);
        Native.SetWindowLong(window, Native.GWL_EXSTYLE, exStyle);
        bool wasTopmost = (exStyle & Native.WS_EX_TOPMOST) != 0;
        Native.SetWindowPos(window, wasTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.RemoveProp(window, GhostTag);
        Native.RemoveProp(window, GhostStyleTag);
    }

    /// <summary>On startup: un-hide any window a previous (killed) run left invisible.</summary>
    public static void RecoverGhostedWindows()
    {
        Native.EnumWindows((w, _) =>
        {
            if (Native.GetProp(w, GhostTag) != IntPtr.Zero)
            {
                int style = (int)Native.GetProp(w, GhostStyleTag).ToInt64();
                long pos = Native.GetProp(w, GhostPosTag).ToInt64();
                Native.SetWindowPos(w, IntPtr.Zero, (int)(pos >> 32), (int)pos, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                RestoreStyle(w, style);
                if ((style & Native.WS_EX_LAYERED) != 0)
                    Native.SetLayeredWindowAttributes(w, 0, 255, Native.LWA_ALPHA);
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>Fit the preview into our window without stretching it.</summary>
    private void UpdateThumbnail()
    {
        if (_thumb == IntPtr.Zero)
            return;

        Native.GetClientRect(_hwnd, out var client);
        int w = client.Width, h = (int)Math.Round(client.Width / _aspect);
        if (h > client.Height)
        {
            h = client.Height;
            w = (int)Math.Round(client.Height * _aspect);
        }
        int x = (client.Width - w) / 2, y = (client.Height - h) / 2;

        _videoArea = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE
                      | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = _videoArea,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };
        Native.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    private void WatchSource()
    {
        if (!Native.IsWindow(Source))
        {
            Close();
            return;
        }

        // Safety net in case the mouse hook missed the cursor leaving. While interacting, also
        // re-read the geometry: the source can resize itself behind our back (a video going
        // fullscreen resizes the whole browser), and stale numbers put clicks on the wrong spot.
        if (_interactive && Native.GetCursorPos(out var cursor))
        {
            if (!IsOverVideo(cursor))
                ExitInteract($"watch cursor={cursor.X},{cursor.Y}");
            else if (_lPhase == 0 && _rPhase == 0 && Environment.TickCount64 - _lastMoveTick > IdleSleepMs)
                ExitInteract("idle"); // a resting cursor hands pinch and wheel back to the player
            else
            {
                RefreshInteractGeometry();
                AlignSourceUnder(cursor.X, cursor.Y, async: true);
            }
        }

        // Minimized (e.g. via the taskbar)? Un-minimize it, still invisible, so the preview stays live.
        if (Native.IsIconic(Source))
            Native.ShowWindow(Source, Native.SW_SHOWNOACTIVATE);

        // Source window changed shape (e.g. a video went fullscreen): reshape the mini player to
        // match, so the picture fills it again instead of leaving black bars at the sides.
        if (SourceAspect() is double a && Math.Abs(a - _aspect) > 0.01)
        {
            _aspect = a;
            ResizeToAspect();
        }
    }

    /// <summary>Resize to the current aspect ratio, keeping the center and staying in the work area.</summary>
    private void ResizeToAspect()
    {
        Native.GetWindowRect(_hwnd, out var r);
        var work = Native.WorkAreaFor(_hwnd);
        int w = r.Width, h = (int)Math.Round(w / _aspect);
        if (h > work.Height)
        {
            h = work.Height;
            w = (int)Math.Round(h * _aspect);
        }
        if (w > work.Width)
        {
            w = work.Width;
            h = (int)Math.Round(w / _aspect);
        }
        int x = Math.Clamp(r.Left + (r.Width - w) / 2, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(r.Top + (r.Height - h) / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Close the mini player and give the real window back (and focus it, unless told not to).</summary>
    public void CloseAndRestore(bool focusSource = true)
    {
        if (_closing)
            return;

        UnghostSource();
        if (Native.IsWindow(Source) && focusSource)
        {
            if (Native.IsIconic(Source))
                Native.ShowWindow(Source, Native.SW_RESTORE);
            Native.SetForegroundWindow(Source);
        }
        Close();
    }

    private void Close()
    {
        if (_hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd); // cleanup happens in WM_DESTROY
        else
            OnDestroyed();
    }

    private void OnDestroyed()
    {
        if (_closing)
            return;
        _closing = true;
        _watch.Stop();
        _zoom?.Dispose();
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        UnghostSource();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    // ---- Window messages ----

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                return IntPtr.Zero; // no visible frame: the whole window is the video

            case WM_NCHITTEST:
                return new IntPtr(HitTest(hwnd, lParam));

            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE); // clicking the player doesn't steal focus from your work

            case Native.WM_MOUSEMOVE:
                // Just pointing at the player wakes it: hovering then reaches the real app
                // (video controls appear). Not during a pinch/zoom, which must stay ours, and
                // only if the cursor really moved: raising a window under a resting cursor makes
                // Windows synthesize a mouse move, which must not wake us right back up.
                if (!_interactive && _zoom?.Active != true && Native.GetCursorPos(out var moved)
                    && (Math.Abs(moved.X - _sleepX) > 1 || Math.Abs(moved.Y - _sleepY) > 1))
                    EnterInteract();
                break;

            case WM_LBUTTONDOWN:
                // Only reached while asleep (the hook owns clicks while awake): a click after the
                // cursor sat still. Press-and-drag moves the player; a plain click goes to the app.
                Native.GetCursorPos(out var down);
                if (Native.DragDetect(hwnd, down))
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(hwnd, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
                }
                else if (EnterInteract())
                    InjectClick();
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                ShowMenu();
                return IntPtr.Zero;

            case Native.WM_MOUSEWHEEL:
            case TouchpadPinch.DM_POINTERHITTEST:
                if (_zoom?.HandleMessage(msg, wParam) == true)
                    return IntPtr.Zero;
                break;

            case Native.WM_SIZING:
                SmoothZoom.KeepAspectWhileResizing(wParam, lParam, _aspect);
                return new IntPtr(1);

            case Native.WM_SIZE:
                UpdateThumbnail();
                if (_interactive)
                    RefreshInteractGeometry(); // keep the hook's cached rects honest
                break;

            case WM_GETMINMAXINFO:
                double dpi = Native.GetDpiForWindow(hwnd) / 96.0;
                Marshal.WriteInt32(lParam, 24, (int)(MinWidthDip * dpi));  // ptMinTrackSize.x
                Marshal.WriteInt32(lParam, 28, (int)(MinHeightDip * dpi)); // ptMinTrackSize.y
                return IntPtr.Zero;

            case WM_DPICHANGED:
                var r = Marshal.PtrToStructure<Native.RECT>(lParam);
                Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                return IntPtr.Zero;

            case WM_DESTROY:
                OnDestroyed();
                break;

            case WM_NCDESTROY:
                _hwnd = IntPtr.Zero;
                var result = DefWindowProc(hwnd, msg, wParam, lParam);
                UnregisterClass(_className, GetModuleHandle(null));
                return result;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Edges resize, everything else is "client" (drag / click / pinch).</summary>
    private static int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        Native.GetWindowRect(hwnd, out var r);
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        int edge = (int)Math.Round(ResizeBorderDip * Native.GetDpiForWindow(hwnd) / 96.0);

        bool left = x < r.Left + edge, right = x >= r.Right - edge;
        bool top = y < r.Top + edge, bottom = y >= r.Bottom - edge;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
    }

    private void ShowMenu()
    {
        const uint BackToApp = 1, CloseOnly = 2;
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, BackToApp, "Back to app");
        AppendMenu(menu, 0, CloseOnly, "Close mini player");

        Native.GetCursorPos(out var p);
        Native.SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        uint command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        if (command == BackToApp)
            CloseAndRestore();
        else if (command == CloseOnly)
            CloseAndRestore(focusSource: false);
    }

    // ---- Interacting with the real app ----
    // The real window sits invisible on top. Pointing at the video lines it up so the point you see
    // under the cursor is the same point of the real window under the cursor, and turns off
    // click-through: hovering, clicks and typing then go straight to the app, no click needed first.
    // While interacting, a low-level mouse hook keeps it lined up as the cursor moves, and also keeps
    // the player's own gestures working the same as ever: it takes the buttons and the wheel for
    // itself (drag moves the player, wheel resizes, right-click opens the menu) and plays a plain
    // click back into the app, so a click still hits exactly what you see. Interaction ends when the
    // cursor leaves the video or sits still for a moment — that is what gives the touchpad pinch
    // back to the player, since pinches start from a resting cursor.

    private Native.RECT _videoArea;          // where the preview is drawn, in mini player coordinates
    private bool _interactive;
    private int _frameOffsetX, _frameOffsetY; // real window's client area offset inside its frame
    private Native.RECT _sourceHome;          // where the real window was, restored on close
    private bool _sourceMoved;
    private IntPtr _mouseHook;
    private Native.HookProc? _hookProc;

    // Geometry cached for the mouse hook. The hook runs for every mouse event on the whole PC,
    // so it must stay tiny: no system calls, no window moves. It only remembers the cursor and
    // pokes the UI thread, which does the real work once per batch of events.
    private Native.RECT _videoScreen;        // the video area in screen coordinates
    private int _pipLeft, _pipTop;
    private int _srcClientW, _srcClientH;
    private int _lastAlignX = int.MinValue, _lastAlignY;
    private int _alignQueued;                // 1 while an alignment is waiting on the UI thread
    private int _hookX, _hookY;              // latest cursor position the hook saw
    private long _lastMoveTick;              // when the cursor last moved (for falling asleep)
    private int _lPhase;                     // left button: 0 up, 1 held (a click so far), 2 dragging the player
    private int _rPhase;                     // right button: 0 up, 1 held (menu on release)
    private int _downX, _downY;              // where the left button went down
    private int _dragPipX, _dragPipY;        // where the player was when the drag started
    private int _sleepX = int.MinValue, _sleepY; // where the cursor was when interaction ended
    private long _lastAlignTick;             // when the source was last moved (rate cap)
    private int _srcOriginX, _srcOriginY;    // where the source's client area starts on screen
    private bool _srcPinned;                 // fullscreen source: Windows/Chrome pin it, it cannot be moved
    private const int IdleSleepMs = 600, DragThresholdPx = 6;

    private bool EnterInteract()
    {
        if (_interactive)
            return true;
        Native.GetCursorPos(out var p);
        if (!IsOverVideo(p))
            return false;

        RefreshInteractGeometry();
        _lastAlignX = int.MinValue;
        _lastMoveTick = Environment.TickCount64;
        _lPhase = _rPhase = 0;

        _interactive = true;
        Native.SetWindowLong(Source, Native.GWL_EXSTYLE,
            Native.GetWindowLong(Source, Native.GWL_EXSTYLE) & ~Native.WS_EX_TRANSPARENT);
        AlignSourceUnder(p.X, p.Y, async: false);
        Native.SetWindowPos(Source, Native.HWND_TOPMOST, 0, 0, 0, 0, // above the mini player
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        _hookProc ??= OnGlobalMouse;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
        if (Environment.GetEnvironmentVariable("MINIPLAYER_TRACE") == "1")
            App.Log($"EnterInteract: cursor={p.X},{p.Y} video={_videoScreen.Left},{_videoScreen.Top}-{_videoScreen.Right},{_videoScreen.Bottom}");
        return true;
    }

    private void ExitInteract() => ExitInteract("direct");

    private void ExitInteract(string reason)
    {
        if (!_interactive)
            return;
        if (Environment.GetEnvironmentVariable("MINIPLAYER_TRACE") == "1")
            App.Log($"ExitInteract: {reason}");
        _interactive = false;
        _lPhase = _rPhase = 0;
        if (Native.GetCursorPos(out var sleep)) // wake again only once the cursor moves from here
        {
            _sleepX = sleep.X;
            _sleepY = sleep.Y;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (!Native.IsWindow(Source))
            return;

        Native.SetWindowLong(Source, Native.GWL_EXSTYLE,
            Native.GetWindowLong(Source, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT);
        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Re-read everything the alignment math uses. Cheap, and called often while interacting:
    /// the source can move or resize at any time (a video going fullscreen resizes the whole
    /// browser), and stale numbers would put hovers and clicks on the wrong spot.
    /// </summary>
    private void RefreshInteractGeometry()
    {
        Native.GetWindowRect(Source, out var frame);
        var origin = new Native.POINT();
        Native.ClientToScreen(Source, ref origin);
        _frameOffsetX = origin.X - frame.Left;
        _frameOffsetY = origin.Y - frame.Top;

        Native.GetWindowRect(_hwnd, out var pip);
        Native.GetClientRect(Source, out var client);
        _pipLeft = pip.Left;
        _pipTop = pip.Top;
        if (client.Width != _srcClientW || client.Height != _srcClientH)
            _lastAlignX = int.MinValue; // the source changed size: the last aligned spot means nothing now
        _srcClientW = client.Width;
        _srcClientH = client.Height;
        _srcOriginX = origin.X;
        _srcOriginY = origin.Y;
        _videoScreen = VideoScreenRect();

        // A window in real fullscreen (a video, a game) is pinned to its screen: Windows and the
        // app itself snap it back when moved, so lining it up under the cursor is impossible.
        // Clicks then warp the cursor to the matching screen point instead (see InjectClick).
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromWindow(Source, 2 /* nearest */), ref mi);
        _srcPinned = client.Width >= mi.rcMonitor.Width && client.Height >= mi.rcMonitor.Height;
    }

    /// <summary>The video area in screen coordinates, shrunk by the resize border.</summary>
    private Native.RECT VideoScreenRect()
    {
        Native.GetWindowRect(_hwnd, out var r);
        int edge = (int)Math.Round(ResizeBorderDip * Native.GetDpiForWindow(_hwnd) / 96.0);
        return new Native.RECT
        {
            Left = r.Left + Math.Max(_videoArea.Left, edge),
            Right = r.Left + Math.Min(_videoArea.Right, r.Width - edge),
            Top = r.Top + Math.Max(_videoArea.Top, edge),
            Bottom = r.Top + Math.Min(_videoArea.Bottom, r.Height - edge),
        };
    }

    private bool IsOverVideo(Native.POINT p)
    {
        if (_hwnd == IntPtr.Zero)
            return false;
        var r = VideoScreenRect();
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    /// <summary>Move the (invisible) real window so the point shown under the cursor is really under it.</summary>
    private void AlignSourceUnder(int px, int py, bool async)
    {
        if (_srcPinned)
            return; // fullscreen: it cannot be moved, and it already covers the cursor anyway

        double u = (px - _pipLeft - _videoArea.Left) / (double)_videoArea.Width;
        double v = (py - _pipTop - _videoArea.Top) / (double)_videoArea.Height;
        int x = (int)Math.Round(px - u * _srcClientW) - _frameOffsetX;
        int y = (int)Math.Round(py - v * _srcClientH) - _frameOffsetY;

        // Moving a big window is what costs, so skip moves that change nothing visible.
        // Near the app's real size that is almost all of them.
        if (Math.Abs(x - _lastAlignX) <= 1 && Math.Abs(y - _lastAlignY) <= 1)
            return;

        // Cap the rate too: a fast mouse must never queue up more moves than the app can
        // process, or everything behind them (like a click) arrives late.
        long now = Environment.TickCount64;
        if (async && now - _lastAlignTick < 7)
            return; // the next move, or the 400ms watch tick, will realign
        _lastAlignTick = now;
        _lastAlignX = x;
        _lastAlignY = y;

        // Async, so a busy app can never make the mouse lag; no redraw, the content is unchanged.
        uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE
                     | Native.SWP_NOREDRAW | Native.SWP_NOSENDCHANGING | (async ? Native.SWP_ASYNCWINDOWPOS : 0);
        Native.SetWindowPos(Source, IntPtr.Zero, x, y, 0, 0, flags);
        _sourceMoved = true;
    }

    // Marks the mouse events this app plays back itself, so the hook can tell them from the
    // user's (anything else, hardware or injected — a remote-control tool acts as the user).
    private static readonly UIntPtr InjectTag = new(0x4D505049); // "MPPI"

    // The hook runs for every mouse event on the whole PC, so the move path must stay tiny.
    // Button and wheel events are rare, so they may do a little more: the hook eats them
    // (returns 1) and the real work happens on the UI thread. Events we play back ourselves
    // carry InjectTag in dwExtraInfo and are let through, so they reach the app.
    private IntPtr OnGlobalMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_interactive)
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        int msg = wParam.ToInt32();
        int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
        bool injected = Marshal.ReadInt64(lParam, 24) == (long)InjectTag; // ours, being played back
        var r = _videoScreen;
        bool inside = x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                _hookX = x;
                _hookY = y;
                _lastMoveTick = Environment.TickCount64;
                if (inside || _lPhase != 0 || _rPhase != 0) // a held button keeps the session alive
                {
                    if (_lPhase == 1 && Math.Abs(x - _downX) + Math.Abs(y - _downY) > DragThresholdPx)
                        _lPhase = 2; // the press became a drag: the player follows the cursor
                    if (Interlocked.Exchange(ref _alignQueued, 1) == 0)
                        _watch.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ApplyQueuedAlign));
                }
                else
                {
                    int hx = x, hy = y; // not inside the hook: ExitInteract talks to the other app
                    var vr = r;
                    _watch.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                        new Action(() => ExitInteract($"hook cursor={hx},{hy} video={vr.Left},{vr.Top}-{vr.Right},{vr.Bottom}")));
                }
                break;

            case WM_LBUTTONDOWN when !injected:
                if (inside)
                {
                    _downX = x;
                    _downY = y;
                    Native.GetWindowRect(_hwnd, out var pip);
                    _dragPipX = pip.Left;
                    _dragPipY = pip.Top;
                    _lPhase = 1;
                }
                else // never let a click fall into the invisible window: let go first, then replay it
                    _watch.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                        new Action(() => ExitAndReplay(Native.MOUSEEVENTF_LEFTDOWN)));
                return new IntPtr(1);

            case WM_LBUTTONUP when !injected && _lPhase != 0:
                int phase = _lPhase;
                _lPhase = 0;
                _watch.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                    phase == 2 ? new Action(EndPlayerDrag) : new Action(InjectClick));
                return new IntPtr(1);

            case WM_RBUTTONDOWN when !injected:
                if (inside)
                    _rPhase = 1;
                else
                    _watch.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                        new Action(() => ExitAndReplay(Native.MOUSEEVENTF_RIGHTDOWN)));
                return new IntPtr(1);

            case WM_RBUTTONUP when !injected && _rPhase != 0:
                _rPhase = 0;
                _watch.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ExitInteract("menu"); // the menu must own the mouse, not this hook
                    ShowMenu();
                }));
                return new IntPtr(1);

            case Native.WM_MOUSEWHEEL when !injected && inside:
                short delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16); // high word of mouseData
                _watch.Dispatcher.BeginInvoke(new Action(() =>
                    _zoom?.HandleMessage(Native.WM_MOUSEWHEEL, new IntPtr((long)(ushort)delta << 16))));
                return new IntPtr(1); // the wheel resizes the player, never scrolls the app
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void ApplyQueuedAlign()
    {
        Interlocked.Exchange(ref _alignQueued, 0);
        if (!_interactive)
            return;
        if (_lPhase == 2) // dragging: the player follows the cursor; the app realigns on release
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, _dragPipX + _hookX - _downX, _dragPipY + _hookY - _downY,
                0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            RefreshInteractGeometry();
            return;
        }
        if (_lPhase == 1 || _rPhase == 1)
            return; // button held but no drag yet: hold everything still
        RefreshInteractGeometry(); // a handful of cheap reads, and the hook stays tiny
        AlignSourceUnder(_hookX, _hookY, async: true);
    }

    /// <summary>Play the click the hook ate back into the app, at the point the user sees.</summary>
    private void InjectClick()
    {
        if (!_interactive)
            return;
        RefreshInteractGeometry();
        Native.GetCursorPos(out var p);
        if (Environment.GetEnvironmentVariable("MINIPLAYER_TRACE") == "1")
            App.Log($"InjectClick: cursor={p.X},{p.Y} pinned={_srcPinned} src={_srcOriginX},{_srcOriginY} {_srcClientW}x{_srcClientH}");
        if (_srcPinned)
        {
            // Fullscreen source: it cannot be lined up under the cursor, and warping the cursor
            // to the point flashes a second pointer. Hand the click to the window directly.
            double u = Math.Clamp((p.X - _pipLeft - _videoArea.Left) / (double)_videoArea.Width, 0, 1);
            double v = Math.Clamp((p.Y - _pipTop - _videoArea.Top) / (double)_videoArea.Height, 0, 1);
            var pt = new Native.POINT
            {
                X = _srcOriginX + (int)Math.Round(u * _srcClientW),
                Y = _srcOriginY + (int)Math.Round(v * _srcClientH),
            };
            IntPtr target = InputChild(Source);
            Native.ScreenToClient(target, ref pt);
            IntPtr pos = new((pt.Y << 16) | (pt.X & 0xFFFF));
            Native.PostMessage(target, Native.WM_MOUSEMOVE, IntPtr.Zero, pos);
            Native.PostMessage(target, WM_LBUTTONDOWN, new IntPtr(1) /* MK_LBUTTON */, pos);
            Native.PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, pos);
            Native.SetForegroundWindow(Source); // a real click would focus the app too (Esc, typing)
            return;
        }
        AlignSourceUnder(p.X, p.Y, async: false);
        // Let the app drain whatever is still queued (hover moves), so the click is handled now.
        Native.SendMessageTimeout(Source, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero,
            Native.SMTO_ABORTIFHUNG, 150, out _);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, InjectTag);
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, InjectTag);
    }

    /// <summary>The child window that takes a browser's input (web content), or the window itself.</summary>
    private static IntPtr InputChild(IntPtr window)
    {
        IntPtr found = window;
        Native.EnumChildWindows(window, (child, _) =>
        {
            if (Native.ClassName(child) != "Chrome_RenderWidgetHostHWND")
                return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>A click outside the video: step out of the way first, then let the click happen.</summary>
    private void ExitAndReplay(uint buttonDown)
    {
        ExitInteract("click outside");
        Native.mouse_event(buttonDown, 0, 0, 0, InjectTag);
        Native.mouse_event(buttonDown << 1, 0, 0, 0, InjectTag); // the matching button-up
    }

    private void EndPlayerDrag()
    {
        if (!_interactive)
            return;
        RefreshInteractGeometry();
        Native.GetCursorPos(out var p);
        if (IsOverVideo(p))
        {
            _lastAlignX = int.MinValue;
            AlignSourceUnder(p.X, p.Y, async: true);
        }
        else
            ExitInteract("drag ended outside");
    }

    // ---- Win32 ----

    private const uint WS_POPUP = 0x80000000, WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    private const int WM_DESTROY = 0x0002, WM_NCDESTROY = 0x0082, WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_DPICHANGED = 0x02E0;
    private const int MA_NOACTIVATE = 3, HTCLIENT = 1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
