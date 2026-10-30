using System;
using System.Runtime.InteropServices;

namespace MiniPlayer;

/// <summary>
/// Smooth touchpad pinch using Windows DirectManipulation (what Chrome/Edge use).
/// Without it, Windows turns a pinch into a few coarse Ctrl+wheel clicks, which feels jumpy.
/// Reports the exact, continuous change in finger spread through <see cref="Pinched"/>.
/// </summary>
internal sealed class TouchpadPinch : IDisposable
{
    public const int DM_POINTERHITTEST = 0x0250;
    private const int PT_TOUCHPAD = 5;
    private const int ViewportSize = 1000;

    /// <summary>Relative size change since the last event (1.05 = 5% bigger).</summary>
    public event Action<double>? Pinched;

    private readonly IntPtr _hwnd;
    private readonly IDirectManipulationManager _manager;
    private readonly IDirectManipulationUpdateManager _updates;
    private readonly IDirectManipulationViewport _viewport;
    private readonly ViewportHandler _handler;
    private readonly uint _cookie;
    private bool _resetting;
    private double _lastScale = 1;

    public static string Step = "";

    public TouchpadPinch(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Step = "create";
        var type = Type.GetTypeFromCLSID(new Guid("54E211B6-3650-4F75-8334-FA359598E1C5"))!;
        _manager = (IDirectManipulationManager)Activator.CreateInstance(type)!;

        Step = "updates";
        var updatesId = typeof(IDirectManipulationUpdateManager).GUID;
        _manager.GetUpdateManager(ref updatesId, out object updates);
        _updates = (IDirectManipulationUpdateManager)updates;

        Step = "viewport";
        var viewportId = typeof(IDirectManipulationViewport).GUID;
        _manager.CreateViewport(IntPtr.Zero, hwnd, ref viewportId, out object viewport);
        _viewport = (IDirectManipulationViewport)viewport;

        // Only scaling matters; panning is enabled so two-finger moves don't get misread as pinches.
        _viewport.ActivateConfiguration(Config.Interaction | Config.Scaling
            | Config.TranslationX | Config.TranslationY | Config.RailsX | Config.RailsY);
        _viewport.SetViewportOptions(2 /* MANUALUPDATE: the owner calls Poll() every frame */);

        Step = "handler";
        _handler = new ViewportHandler(this);
        _viewport.AddEventHandler(hwnd, _handler, out _cookie);
        Step = "rect";
        var rect = new Native.RECT { Right = ViewportSize, Bottom = ViewportSize };
        _viewport.SetViewportRect(ref rect);

        Step = "activate";
        _manager.Activate(hwnd);
        Step = "enable";
        _viewport.Enable();
        Step = "ok";
    }

    /// <summary>True while a gesture is in progress; call <see cref="Poll"/> every frame then.</summary>
    public bool Active { get; private set; }

    /// <summary>Call for DM_POINTERHITTEST. Returns true if a touchpad gesture was taken over.</summary>
    public bool OnPointerHitTest(IntPtr wParam)
    {
        uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!GetPointerType(pointerId, out int type) || type != PT_TOUCHPAD)
            return false;

        _viewport.SetContact(pointerId);
        Active = true;
        return true;
    }

    /// <summary>Let DirectManipulation process input; raises <see cref="Pinched"/> as fingers move.</summary>
    public void Poll() => _updates.Update(IntPtr.Zero);

    private void OnStatusChanged(int current)
    {
        const int Ready = 5;
        if (current != Ready)
            return;

        // Gesture over: reset so the next pinch starts from scale 1 again.
        _lastScale = 1;
        _resetting = true;
        _viewport.ZoomToRect(0, 0, ViewportSize, ViewportSize, false);
        _resetting = false;
        Active = false;
    }

    private void OnContentUpdated(IDirectManipulationContent content)
    {
        var m = new float[6];
        content.GetContentTransform(m, 6);
        double scale = m[0];
        if (_resetting || scale <= 0)
            return;

        double factor = scale / _lastScale;
        _lastScale = scale;
        if (Math.Abs(factor - 1) > 1e-4)
            Pinched?.Invoke(factor);
    }

    public void Dispose()
    {
        Active = false;
        try
        {
            _viewport.RemoveEventHandler(_cookie);
            _viewport.Disable();
            _manager.Deactivate(_hwnd);
        }
        catch (COMException) { }
    }

    [DllImport("user32.dll")]
    private static extern bool GetPointerType(uint pointerId, out int type);

    // ---------------- COM interop ----------------

    private static class Config
    {
        public const int Interaction = 0x1, TranslationX = 0x2, TranslationY = 0x4, Scaling = 0x10;
        public const int RailsX = 0x100, RailsY = 0x200;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ViewportHandler : IDirectManipulationViewportEventHandler
    {
        private readonly TouchpadPinch _owner;
        public ViewportHandler(TouchpadPinch owner) => _owner = owner;

        public void OnViewportStatusChanged(IntPtr viewport, int current, int previous) => _owner.OnStatusChanged(current);
        public void OnViewportUpdated(IntPtr viewport) { }
        public void OnContentUpdated(IntPtr viewport, IDirectManipulationContent content) => _owner.OnContentUpdated(content);
    }

    [ComImport, Guid("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectManipulationManager
    {
        void Activate(IntPtr window);
        void Deactivate(IntPtr window);
        void RegisterHitTestTarget(IntPtr window, IntPtr hitTestWindow, int type);
        void ProcessInput(IntPtr message, out int handled);
        void GetUpdateManager(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void CreateViewport(IntPtr frameInfo, IntPtr window, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void CreateContent(IntPtr frameInfo, ref Guid clsid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectManipulationUpdateManager
    {
        void RegisterWaitHandleCallback(IntPtr handle, IntPtr handler, out uint cookie);
        void UnregisterWaitHandleCallback(uint cookie);
        void Update(IntPtr frameInfo);
    }

    [ComImport, Guid("28B85A3D-60A0-48BD-9BA1-5CE8D9EA3A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirectManipulationViewport
    {
        void Enable();
        void Disable();
        void SetContact(uint pointerId);
        void ReleaseContact(uint pointerId);
        void ReleaseAllContacts();
        void GetStatus(out int status);
        void GetTag(ref Guid riid, out IntPtr obj, out uint id);
        void SetTag(IntPtr obj, uint id);
        void GetViewportRect(out Native.RECT viewport);
        void SetViewportRect(ref Native.RECT viewport);
        void ZoomToRect(float left, float top, float right, float bottom, [MarshalAs(UnmanagedType.Bool)] bool animate);
        void SetViewportTransform(float[] matrix, uint pointCount);
        void SyncDisplayTransform(float[] matrix, uint pointCount);
        void GetPrimaryContent(ref Guid riid, out IntPtr obj);
        void AddContent(IntPtr content);
        void RemoveContent(IntPtr content);
        void SetViewportOptions(int options);
        void AddConfiguration(int configuration);
        void RemoveConfiguration(int configuration);
        void ActivateConfiguration(int configuration);
        void SetManualGesture(int configuration);
        void SetChaining(int enabledTypes);
        void AddEventHandler(IntPtr window, IDirectManipulationViewportEventHandler handler, out uint cookie);
        void RemoveEventHandler(uint cookie);
        void SetInputMode(int mode);
        void SetUpdateMode(int mode);
        void Stop();
        void Abandon();
    }

    [ComImport, Guid("952121DA-D69F-45F9-B0F9-F23944321A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectManipulationViewportEventHandler
    {
        void OnViewportStatusChanged(IntPtr viewport, int current, int previous);
        void OnViewportUpdated(IntPtr viewport);
        void OnContentUpdated(IntPtr viewport, IDirectManipulationContent content);
    }

    [ComImport, Guid("B89962CB-3D89-442B-BB58-5098FA0F9F16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectManipulationContent
    {
        void GetContentRect(out Native.RECT rect);
        void SetContentRect(ref Native.RECT rect);
        void GetViewport(ref Guid riid, out IntPtr obj);
        void GetTag(ref Guid riid, out IntPtr obj, out uint id);
        void SetTag(IntPtr obj, uint id);
        void GetOutputTransform([Out, MarshalAs(UnmanagedType.LPArray)] float[] matrix, uint pointCount);
        void GetContentTransform([Out, MarshalAs(UnmanagedType.LPArray)] float[] matrix, uint pointCount);
        void SyncContentTransform(float[] matrix, uint pointCount);
    }
}
