using System;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

public enum WheelSource
{
    NativeVertical,            // physical WM_MOUSEWHEEL
    NativeHorizontal,          // physical WM_MOUSEHWHEEL (tilt / thumb wheel)
    ShiftVerticalAsHorizontal, // WM_MOUSEWHEEL + Shift, converted to horizontal by us
}

public sealed class MouseWheelEventArgs : EventArgs
{
    public int Delta { get; }
    public bool Handled { get; set; }
    public WheelSource Source { get; }
    // True when this zoom event was routed to zoom only because a live Ctrl re-check overrode a
    // stale (sampler-cached) "Ctrl up" — i.e. a misroute to accelerated scroll was just prevented.
    // Diagnostic only; lets the zoom worker log that a "sudden large zoom" was caught.
    public bool CtrlRecovered { get; set; }
    public MouseWheelEventArgs(int delta, WheelSource source = WheelSource.NativeVertical)
    {
        Delta = delta;
        Source = source;
    }
}

public sealed class MousePositionEventArgs : EventArgs
{
    public int X { get; }
    public int Y { get; }
    public MousePositionEventArgs(int x, int y) { X = x; Y = y; }
}

// Low-level mouse hook with ability to mark wheel events handled
public sealed class GlobalMouseHook : IDisposable
{
    private IntPtr _hook = IntPtr.Zero;
    private NativeMethods.HookProc? _proc;

    private readonly KeyboardStateSampler _keyboard = new();

    // Track middle-click state to avoid firing MouseMoved on every normal mouse move

    // Track middle-click state to avoid firing MouseMoved on every normal mouse move
    private volatile bool _middleClickActive;

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>
    /// When true, holding Shift will convert vertical scroll to horizontal.
    /// </summary>
    public bool ShiftKeyHorizontal { get; set; } = true;

    public event EventHandler<MouseWheelEventArgs>? MouseWheel;
    public event EventHandler<MouseWheelEventArgs>? MouseHWheel;
    public event EventHandler<MouseWheelEventArgs>? MouseZoomWheel;

    /// <summary>Fired (on a background sampler thread) when Ctrl transitions from up to down.</summary>
    public event Action? CtrlPressed
    {
        add => _keyboard.CtrlPressed += value;
        remove => _keyboard.CtrlPressed -= value;
    }
    public event EventHandler<MousePositionEventArgs>? MiddleButtonDown;
    public event EventHandler? MiddleButtonUp;
    public event EventHandler<MousePositionEventArgs>? MouseMoved;

    public void Install()
    {
        if (IsInstalled) return;
        _keyboard.Start();
        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _keyboard.Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();

            // Hottest path by far: bare cursor moves arrive hundreds/sec. Unless middle-click
            // scrolling is active we ignore them entirely, so bail BEFORE marshalling the struct
            // (a per-event copy that was otherwise built and discarded on every move).
            if (msg == NativeMethods.WM_MOUSEMOVE && !_middleClickActive)
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            if ((data.flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) != 0)
                return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

            if (msg == NativeMethods.WM_MOUSEWHEEL)
            {
                int delta = (short)((data.mouseData >> 16) & 0xffff);

                // Zoom-vs-scroll routing hinges on Ctrl. The modifier sampler runs at ~60fps, so
                // the FIRST wheel event right after Ctrl goes down can land in that <=16ms gap and
                // read Ctrl as UP — misrouting a Ctrl+wheel zoom to the regular vertical scroll.
                // That path applies step size + acceleration + momentum and then injects a wheel
                // while Ctrl is physically held, so the app reads it as a sudden LARGE zoom. Refresh
                // the snapshot from the live key state for this one decision — wheel events are
                // infrequent (the cache exists for WM_MOUSEMOVE, hundreds/sec, not the wheel).
                bool cachedCtrl = _keyboard.IsCtrlPressed;
                _keyboard.ForceUpdate();
                bool ctrlRecovered = _keyboard.IsCtrlPressed && !cachedCtrl;

                if (_keyboard.IsCtrlPressed)
                {
                    var args = new MouseWheelEventArgs(delta) { CtrlRecovered = ctrlRecovered };
                    MouseZoomWheel?.Invoke(this, args);
                    if (args.Handled) return (IntPtr)1;
                }
                else if (ShiftKeyHorizontal && _keyboard.IsShiftPressed)
                {
                    // Shift turns the vertical wheel into horizontal scroll. Direction is applied
                    // uniformly with the physical horizontal wheel in the App handler (a single
                    // negation for ALL true-horizontal output), so pass the raw delta through here.
                    var args = new MouseWheelEventArgs(delta, WheelSource.ShiftVerticalAsHorizontal);
                    MouseHWheel?.Invoke(this, args);
                    if (args.Handled) return (IntPtr)1;
                }
                else
                {
                    var args = new MouseWheelEventArgs(delta, WheelSource.NativeVertical);
                    MouseWheel?.Invoke(this, args);
                    if (args.Handled) return (IntPtr)1;
                }
            }
            else if (msg == NativeMethods.WM_MOUSEHWHEEL)
            {
                // Routing of Ctrl+horizontal (scroll vs zoom) is decided in the App handler, which
                // has the settings + per-app profile (the "Ctrl+horizontal = zoom" toggle is
                // opt-in and per-profile overridable). The handler reads the live Ctrl state itself.
                int delta = (short)((data.mouseData >> 16) & 0xffff);
                var args = new MouseWheelEventArgs(delta, WheelSource.NativeHorizontal);
                MouseHWheel?.Invoke(this, args);
                if (args.Handled)
                    return (IntPtr)1;
            }
            else if (msg == NativeMethods.WM_MBUTTONDOWN)
            {
                _middleClickActive = true;
                MiddleButtonDown?.Invoke(this, new MousePositionEventArgs(data.pt.x, data.pt.y));
            }
            else if (msg == NativeMethods.WM_MBUTTONUP)
            {
                _middleClickActive = false;
                MiddleButtonUp?.Invoke(this, EventArgs.Empty);
            }
            else if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // Reached only while middle-click scrolling is active — non-active moves bailed
                // before marshalling above — so no second _middleClickActive check is needed.
                MouseMoved?.Invoke(this, new MousePositionEventArgs(data.pt.x, data.pt.y));
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
