using System;
using System.Threading;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

/// <summary>
/// Background-thread keyboard state sampler.
/// Polls modifier keys at fixed rate so the hook callback (hot path)
/// can read them without any P/Invoke overhead.
/// </summary>
public sealed class KeyboardStateSampler
{
    private const int PollIntervalMs = 16; // ~60fps

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _shift;
    private volatile bool _ctrl;
    private volatile bool _alt;

    public bool IsShiftPressed => _shift;
    public bool IsCtrlPressed => _ctrl;
    public bool IsAltPressed => _alt;

    /// <summary>
    /// Raised (on the sampler thread) when Ctrl transitions from up to down. Lets the app clear
    /// any in-flight smooth scroll the moment the user switches into a Ctrl+wheel zoom, so the
    /// scroll tail can't keep emitting wheel pulses that — with Ctrl now held — read as zoom.
    /// </summary>
    public event Action? CtrlPressed;

    public void ForceUpdate()
    {
        _shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        _ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        _alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        ForceUpdate(); // get initial state immediately
        _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "KeyboardStateSampler" };
        _thread.Start();
    }

    private void WorkerLoop()
    {
        // Track Ctrl in a local (not the shared _ctrl, which the hook's ForceUpdate also writes)
        // so the up→down edge is detected purely from this thread's 60fps sampling.
        bool prevCtrl = _ctrl;
        while (_running)
        {
            _shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            _ctrl = ctrl;
            _alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
            if (ctrl && !prevCtrl) CtrlPressed?.Invoke();
            prevCtrl = ctrl;
            Thread.Sleep(PollIntervalMs);
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
    }
}
