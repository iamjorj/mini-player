using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace MiniPlayer;

/// <summary>
/// Runs in the background: a tray icon plus the Ctrl+Alt+P hotkey.
/// Hotkey pops the active window out into a mini player; pressing it again puts it back.
/// </summary>
public partial class App : Application
{
    private const int HotkeyId = 1;
    private const uint VK_P = 0x50;

    private Mutex? _singleInstance;
    private HwndSource? _messageWindow;
    private Forms.NotifyIcon? _tray;
    private PipWindow? _pip;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "MiniPlayer.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // If anything goes wrong, never leave a window stuck invisible.
        PipWindow.RecoverGhostedWindows();
        DispatcherUnhandledException += (_, _) => _pip?.CloseAndRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _pip?.CloseAndRestore();

        // Invisible message-only window that receives the hotkey.
        _messageWindow = new HwndSource(new HwndSourceParameters("MiniPlayerHotkey")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        });
        _messageWindow.AddHook(WndProc);

        bool registered = Native.RegisterHotKey(_messageWindow.Handle, HotkeyId,
            Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_P);

        CreateTray();

        if (!registered)
            _tray!.ShowBalloonTip(5000, "Mini Player", "Ctrl+Alt+P is already used by another app.", Forms.ToolTipIcon.Warning);
        else if (WelcomeWindow.ShouldShow)
            new WelcomeWindow().Show();
        else
            _tray!.ShowBalloonTip(3000, "Mini Player is running", "Press Ctrl+Alt+P in any window to pop it out.", Forms.ToolTipIcon.Info);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Pop out active window: Ctrl+Alt+P").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Mini Player (Ctrl+Alt+P)",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>The app logo at the tray's icon size (picks the matching image from the .ico).</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative))!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Toggle()
    {
        if (_pip != null)
        {
            _pip.CloseAndRestore();
            return;
        }

        IntPtr target = Native.GetForegroundWindow();
        if (!CanPopOut(target))
            return;

        _pip = new PipWindow(target);
        _pip.Closed += (_, _) => _pip = null;
        _pip.Show();
    }

    /// <summary>Append a line to %TEMP%\MiniPlayer.log (for diagnosing problems).</summary>
    internal static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MiniPlayer.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
    }

    /// <summary>Normal app windows only, not the desktop, taskbar or our own windows.</summary>
    internal static bool CanPopOut(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd))
            return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId)
            return false;

        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0)
            return false;

        return Native.ClassName(hwnd) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pip?.CloseAndRestore();
        if (_messageWindow != null)
        {
            Native.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            _messageWindow.Dispose();
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
