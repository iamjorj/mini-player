using System;
using System.Runtime.InteropServices;
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

        // Safety net in case the mouse hook missed the cursor leaving.
        if (_interactive && Native.GetCursorPos(out var cursor) && !IsOverVideo(cursor))
            ExitInteract();

        // Minimized (e.g. via the taskbar)? Un-minimize it, still invisible, so the preview stays live.
        if (Native.IsIconic(Source))
            Native.ShowWindow(Source, Native.SW_SHOWNOACTIVATE);

        // Source window resized: refit the preview.
        if (SourceAspect() is double a && Math.Abs(a - _aspect) > 0.01)
        {
            _aspect = a;
            UpdateThumbnail();
        }
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

            case WM_LBUTTONDOWN:
                // Press and drag = move the player. Plain click = hand it to the app.
                Native.GetCursorPos(out var down);
                if (Native.DragDetect(hwnd, down))
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(hwnd, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
                }
                else if (EnterInteract())
                {
                    // Replay the click; the real window is now exactly under the cursor.
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }
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
    // The real window sits invisible on top. To interact, we line it up so the point you see under the
    // cursor in the mini player is the same point of the real window under the cursor, and turn off
    // click-through. Real clicks, hovering, scrolling and typing then go straight to the app.
    // While interacting, a low-level mouse hook keeps it lined up as the cursor moves,
    // and ends interaction when the cursor leaves the video.

    private Native.RECT _videoArea;          // where the preview is drawn, in mini player coordinates
    private bool _interactive;
    private int _frameOffsetX, _frameOffsetY; // real window's client area offset inside its frame
    private Native.RECT _sourceHome;          // where the real window was, restored on close
    private bool _sourceMoved;
    private IntPtr _mouseHook;
    private Native.HookProc? _hookProc;

    private bool EnterInteract()
    {
        if (_interactive)
            return true;
        Native.GetCursorPos(out var p);
        if (!IsOverVideo(p))
            return false;

        Native.GetWindowRect(Source, out var frame);
        var origin = new Native.POINT();
        Native.ClientToScreen(Source, ref origin);
        _frameOffsetX = origin.X - frame.Left;
        _frameOffsetY = origin.Y - frame.Top;

        _interactive = true;
        Native.SetWindowLong(Source, Native.GWL_EXSTYLE,
            Native.GetWindowLong(Source, Native.GWL_EXSTYLE) & ~Native.WS_EX_TRANSPARENT);
        AlignSourceUnder(p, async: false);
        Native.SetWindowPos(Source, Native.HWND_TOPMOST, 0, 0, 0, 0, // above the mini player
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        _hookProc ??= OnGlobalMouse;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
        return true;
    }

    private void ExitInteract()
    {
        if (!_interactive)
            return;
        _interactive = false;
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

    private bool IsOverVideo(Native.POINT p)
    {
        if (_hwnd == IntPtr.Zero)
            return false;
        Native.GetWindowRect(_hwnd, out var r);
        int edge = (int)Math.Round(ResizeBorderDip * Native.GetDpiForWindow(_hwnd) / 96.0);
        int left = r.Left + Math.Max(_videoArea.Left, edge), right = r.Left + Math.Min(_videoArea.Right, r.Width - edge);
        int top = r.Top + Math.Max(_videoArea.Top, edge), bottom = r.Top + Math.Min(_videoArea.Bottom, r.Height - edge);
        return p.X >= left && p.X < right && p.Y >= top && p.Y < bottom;
    }

    /// <summary>Move the (invisible) real window so the point shown under <paramref name="p"/> is really under it.</summary>
    private void AlignSourceUnder(Native.POINT p, bool async)
    {
        Native.GetWindowRect(_hwnd, out var pip);
        Native.GetClientRect(Source, out var client);
        double u = (p.X - pip.Left - _videoArea.Left) / (double)_videoArea.Width;
        double v = (p.Y - pip.Top - _videoArea.Top) / (double)_videoArea.Height;
        int x = (int)Math.Round(p.X - u * client.Width) - _frameOffsetX;
        int y = (int)Math.Round(p.Y - v * client.Height) - _frameOffsetY;

        // Async from the hook, so a busy app can never make the mouse lag.
        uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | (async ? Native.SWP_ASYNCWINDOWPOS : 0);
        Native.SetWindowPos(Source, IntPtr.Zero, x, y, 0, 0, flags);
        _sourceMoved = true;
    }

    private IntPtr OnGlobalMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _interactive && wParam.ToInt32() == Native.WM_MOUSEMOVE)
        {
            var p = new Native.POINT { X = Marshal.ReadInt32(lParam, 0), Y = Marshal.ReadInt32(lParam, 4) };
            if (IsOverVideo(p))
                AlignSourceUnder(p, async: true);
            else
                _watch.Dispatcher.BeginInvoke(new Action(ExitInteract)); // not inside the hook: it talks to the other app
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // ---- Win32 ----

    private const uint WS_POPUP = 0x80000000, WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    private const int WM_DESTROY = 0x0002, WM_NCDESTROY = 0x0082, WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_DPICHANGED = 0x02E0;
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
