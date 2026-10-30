using System;
using System.Runtime.InteropServices;

namespace MiniPlayer;

/// <summary>
/// A stand-in shown while a WPF window is being zoomed: a plain window showing a live, DWM-scaled copy of it.
/// Resizing a WPF window costs ~17ms per step (too slow for smooth pinch); resizing this costs ~3ms,
/// and the GPU does the scaling. When zooming ends, the real window takes over again at the final size.
/// </summary>
internal sealed class ZoomProxy : IDisposable
{
    public IntPtr Handle { get; }

    /// <summary>Wheel messages that land on the proxy while zooming.</summary>
    public event Action<int, IntPtr>? WheelMessage;

    private readonly IntPtr _source;
    private IntPtr _thumb;
    private readonly WndProcDelegate _proc; // kept alive while the window exists
    private readonly string _className = "MiniPlayerZoom" + Guid.NewGuid().ToString("N");

    public ZoomProxy(IntPtr source)
    {
        _source = source;
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
            hbrBackground = CreateSolidBrush(0x00221F1E), // the welcome window's dark background (BGR)
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);

        // Owned by the source window, so it always sits right above it.
        Native.GetWindowRect(source, out var r);
        Handle = CreateWindowEx(Native.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, _className, "", WS_POPUP,
            r.Left, r.Top, r.Width, r.Height, source, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        int round = 2; // Windows 11 rounded corners, like the real window
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmRegisterThumbnail(Handle, source, out _thumb);
        UpdateThumbnail();
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    // ---- Still picture (used while handing back to the real window) ----

    private IntPtr _stillDc, _stillBitmap, _stillOld;
    private int _stillWidth, _stillHeight;

    /// <summary>
    /// Stop mirroring the real window and show a fixed picture instead (32-bit BGRA, top-down rows).
    /// The real window can then resize and redraw behind this without its half-drawn frames showing through.
    /// </summary>
    public void ShowStill(int width, int height, int[] pixels)
    {
        FreeStill();
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _stillBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, width * height);
        _stillDc = CreateCompatibleDC(IntPtr.Zero);
        _stillOld = SelectObject(_stillDc, _stillBitmap);
        _stillWidth = width;
        _stillHeight = height;

        // Paint it now (underneath the live copy), then drop the live copy so the picture shows.
        InvalidateRect(Handle, IntPtr.Zero, false);
        UpdateWindow(Handle);
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
    }

    /// <summary>Go back to mirroring the real window live (a new zoom started during the hand-back).</summary>
    public void ShowLive()
    {
        if (_thumb == IntPtr.Zero)
        {
            Native.DwmRegisterThumbnail(Handle, _source, out _thumb);
            UpdateThumbnail();
        }
        FreeStill();
    }

    private void FreeStill()
    {
        if (_stillDc == IntPtr.Zero)
            return;
        SelectObject(_stillDc, _stillOld);
        DeleteDC(_stillDc);
        DeleteObject(_stillBitmap);
        _stillDc = _stillBitmap = IntPtr.Zero;
    }

    private void UpdateThumbnail()
    {
        if (_thumb == IntPtr.Zero)
            return;
        Native.GetClientRect(Handle, out var client);
        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE
                      | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = client,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };
        Native.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_SIZE:
                UpdateThumbnail(); // same frame as the resize, so the copy never lags behind
                break;
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case Native.WM_MOUSEWHEEL:
                WheelMessage?.Invoke(msg, wParam);
                return IntPtr.Zero;
            case WM_ERASEBKGND when _stillDc != IntPtr.Zero:
                return new IntPtr(1);
            case WM_PAINT when _stillDc != IntPtr.Zero:
                var dc = BeginPaint(hwnd, out var ps);
                BitBlt(dc, 0, 0, _stillWidth, _stillHeight, _stillDc, 0, 0, SRCCOPY);
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        DestroyWindow(Handle);
        UnregisterClass(_className, Native.GetModuleHandle(null));
        FreeStill();
    }

    // ---- Win32 ----

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    private const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public Native.RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);

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
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
}
