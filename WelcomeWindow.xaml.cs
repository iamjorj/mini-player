using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MiniPlayer;

/// <summary>
/// Short "how to use" window shown when the app starts (until "Don't show again").
/// It doubles as a live demo: drag it anywhere, pinch or scroll to resize it, just like the mini player.
/// </summary>
public partial class WelcomeWindow : Window
{
    private static readonly string HiddenFlag = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniPlayer", "welcome-hidden");

    public static bool ShouldShow => !File.Exists(HiddenFlag);

    private SmoothZoom? _zoom;
    private double _aspect = 480.0 / 330.0;

    public WelcomeWindow()
    {
        InitializeComponent();

        // Size the window to its content once, then lock that shape (it scales as one piece).
        Design.Measure(new Size(Design.Width, double.PositiveInfinity));
        _aspect = Design.Width / Design.DesiredSize.Height;
        Height = Width / _aspect;

        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        Closed += (_, _) =>
        {
            _zoom?.Dispose();
            _proxy?.Dispose();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // Windows 11 rounded corners
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        _hwnd = hwnd;
        _zoom = new SmoothZoom(hwnd, () => _aspect, minWidthDip: 300, minHeightDip: 300 / _aspect);
        _zoom.ZoomStarting += BeginZoom;
        _zoom.ZoomEnded += EndZoom;
    }

    // ---- Smooth zoom via a stand-in ----
    // Resizing this WPF window every frame is too slow (~17ms per step = bumpy). So while zooming, a cheap
    // stand-in window shows a GPU-scaled live copy of it and gets resized instead; this window waits
    // off-screen meanwhile (Windows doesn't draw copies of cloaked windows, and WPF undoes transparency
    // styles, but off-screen windows keep being drawn).
    // When zooming settles, the stand-in switches to a sharp still picture of this window at the final size,
    // this window resizes and redraws behind it (its half-drawn frames never show), then the stand-in goes.

    private IntPtr _hwnd;
    private ZoomProxy? _proxy;
    private int _zoomGeneration;

    private void BeginZoom()
    {
        _zoomGeneration++; // cancels a hand-back that hasn't finished yet
        if (_proxy == null)
        {
            _proxy = new ZoomProxy(_hwnd);
            _proxy.WheelMessage += (msg, wParam) => _zoom?.HandleMessage(msg, wParam);
        }
        _proxy.ShowLive();
        _zoom!.Target = _proxy.Handle;

        // Park the real window just left of all screens (same size, so the copy stays sharp).
        Native.GetWindowRect(_hwnd, out var r);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, Native.GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */) - r.Width - 100, r.Top,
            0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void EndZoom()
    {
        if (_proxy == null)
            return;

        // Freeze the stand-in on a sharp picture of the final result...
        Native.GetWindowRect(_proxy.Handle, out var r);
        _proxy.ShowStill(r.Width, r.Height, RenderStill(r.Width, r.Height));

        // ...then move the real window under it at the final size; it re-renders unseen.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _zoom!.Target = _hwnd;

        // Swap back once WPF has drawn the new size (a few frames to be safe; the picture is identical).
        int generation = _zoomGeneration, frames = 0;
        void OnRendered(object? s, EventArgs e)
        {
            if (++frames < 4)
                return;
            System.Windows.Media.CompositionTarget.Rendering -= OnRendered;
            if (generation != _zoomGeneration || _proxy == null)
                return; // a new zoom started meanwhile
            _proxy.Dispose();
            _proxy = null;
        }
        System.Windows.Media.CompositionTarget.Rendering += OnRendered;
    }

    /// <summary>Draw this window's content at the given pixel size, as 32-bit BGRA pixels.</summary>
    private int[] RenderStill(int width, int height)
    {
        double scale = width / Design.ActualWidth; // the content scales uniformly with the window
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale,
            System.Windows.Media.PixelFormats.Pbgra32);

        var background = new System.Windows.Media.DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Background, null, new Rect(0, 0, Design.ActualWidth, Design.ActualHeight + 1));
        bitmap.Render(background);
        bitmap.Render(Design);

        var pixels = new int[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }


    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_SIZING)
        {
            SmoothZoom.KeepAspectWhileResizing(wParam, lParam, _aspect);
            handled = true;
            return new IntPtr(1);
        }
        if (_zoom?.HandleMessage(msg, wParam) == true)
            handled = true;
        return IntPtr.Zero;
    }

    private void OnGotIt(object sender, RoutedEventArgs e)
    {
        if (DontShowAgain.IsChecked == true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HiddenFlag)!);
                File.WriteAllText(HiddenFlag, "");
            }
            catch (IOException) { }
        }
        Close();
    }
}
