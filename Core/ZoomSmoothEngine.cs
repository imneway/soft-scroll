using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;

namespace SoftScroll.Core;

public sealed class ZoomSmoothEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);
    private double _remainingDelta;   // signed, in wheel mouseData waiting to be emitted
    private double _emitAccum;        // fractional mouseData carry between frames

    // Chromium-based apps (Figma, browsers, VS Code…) zoom proportionally to wheel-delta
    // magnitude, so we SUBDIVIDE each notch into fine sub-notch pulses eased out over this
    // window — that's what makes the zoom look smooth (a whole-notch pulse just jumps a step).
    private const double ZOOM_DURATION_MS = 110;
    // Cap emitted mouseData per frame so a large backlog can't zoom a huge amount in one frame.
    private const int MAX_ZOOM_PER_FRAME = 60;
    // Cap the backlog so a fast spin can't keep zooming for seconds after the wheel stops
    // (also bounds any stray steps if Ctrl happens to be released mid-glide).
    private const double MAX_BACKLOG = 6 * ScrollConstants.WHEEL_DELTA;
    private const double FRAME_MS = ScrollConstants.FRAME_MS;
    private const int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "ZoomSmoothEngine" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _remainingDelta = 0;
            _emitAccum = 0;
        }
        _thread?.Join(1000);
    }

    public void OnZoom(int delta)
    {
        lock (_lock)
        {
            _remainingDelta = Math.Clamp(_remainingDelta + delta, -MAX_BACKLOG, MAX_BACKLOG);
        }
        _signal.Set();
    }

    private void Worker()
    {
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            try
            {
                bool workAvailable;
                lock (_lock)
                {
                    workAvailable = Math.Abs(_remainingDelta) >= 0.1;
                }

                // Zoom only happens while Ctrl is physically held (the injected wheel inherits
                // MK_CONTROL). If the user released Ctrl with a backlog still queued, those
                // pulses would land as plain scrolling — so drop the backlog and go idle.
                if (workAvailable && !CtrlDown())
                {
                    lock (_lock) { _remainingDelta = 0; _emitAccum = 0; }
                    workAvailable = false;
                }

                if (!workAvailable)
                {
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter on the next gesture.
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;

                int output;
                lock (_lock)
                {
                    output = Step(dt);
                }

                // Emit the wheel directly. We do NOT inject Ctrl — Ctrl+wheel zoom is triggered
                // by the user physically holding Ctrl, and an injected wheel inherits the live
                // modifier state (MK_CONTROL), so the app still zooms. The old code pressed AND
                // released Ctrl; the release cancelled the user's physical Ctrl mid-gesture, so
                // subsequent pulses were treated as a plain scroll instead of zoom.
                if (output != 0)
                    SendWheel(output);

                var sleep = FRAME_MS - (sw.Elapsed.TotalMilliseconds - nowMs);
                if (sleep > 0) Thread.Sleep((int)Math.Round(sleep));
                else Thread.SpinWait(ScrollConstants.SPIN_WAIT_COUNT);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ZoomSmoothEngine worker: {ex.Message}");
            }
        }
    }

    private int Step(double dtMs)
    {
        if (Math.Abs(_remainingDelta) < 0.5)
        {
            _remainingDelta = 0;
            _emitAccum = 0;
            return 0;
        }

        // Ease a fraction of the remaining zoom out this frame (cubic-out) and emit it as raw
        // sub-notch mouseData, so a notch's worth of zoom spreads smoothly over ZOOM_DURATION_MS
        // instead of landing as one jump. Rapid spins accumulate in _remainingDelta and ease
        // out together; the fractional remainder carries to the next frame.
        var t = Math.Min(1.0, dtMs / ZOOM_DURATION_MS);
        var frac = 1.0 - Math.Pow(1.0 - t, 3);
        var emit = _remainingDelta * frac;
        _remainingDelta -= emit;

        _emitAccum += emit;
        int whole = (int)_emitAccum;
        if (whole == 0) return 0;
        whole = Math.Clamp(whole, -MAX_ZOOM_PER_FRAME, MAX_ZOOM_PER_FRAME);
        _emitAccum -= whole;
        return whole;
    }

    private static bool CtrlDown() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

    private static void SendWheel(int mouseData)
    {
        var inp = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = mouseData } }
        };
        NativeMethods.SendInput(1, [inp], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public void Dispose() => Stop();
}
