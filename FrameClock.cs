using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace MiniPlayer;

/// <summary>
/// Ticks on the UI thread once per screen refresh (synced to Windows' compositor via DwmFlush),
/// only while running. Used for animations so every size change lands on its own frame.
/// </summary>
internal sealed class FrameClock
{
    public event Action? Tick;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _generation;
    private int _pending;

    public bool Running { get; private set; }

    public void Start()
    {
        if (Running)
            return;
        Running = true;
        int generation = ++_generation;
        new Thread(() => Loop(generation)) { IsBackground = true, Name = "FrameClock" }.Start();
    }

    public void Stop()
    {
        Running = false;
        _generation++;
    }

    private void Loop(int generation)
    {
        while (generation == Volatile.Read(ref _generation))
        {
            if (DwmFlush() != 0)
                Thread.Sleep(16); // compositor unavailable: fall back to ~60 Hz

            // Never queue more than one tick: if the UI is busy, skip a frame instead of piling up.
            if (Interlocked.Exchange(ref _pending, 1) == 0)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    _pending = 0;
                    if (generation == _generation)
                        Tick?.Invoke();
                });
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
