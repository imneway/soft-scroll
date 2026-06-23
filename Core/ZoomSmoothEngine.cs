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
    private double _remainingDelta;   // signed, in wheel units (multiples of WHEEL_DELTA)
    private double _sinceEmitMs;      // time since the last emitted zoom step (for pacing)

    // Zoom is inherently discrete (apps zoom in whole steps), so we never subdivide a notch —
    // we PACE bursts: the first step of a gesture fires immediately (responsive), and rapid
    // spins are released at most one step per interval (smooth, not one big jump).
    private const double ZOOM_STEP_INTERVAL_MS = 38;
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
            _sinceEmitMs = 0;
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

                if (!workAvailable)
                {
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter, and prime the
                    // pacer so the first step of the next gesture fires immediately.
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    lock (_lock) { _sinceEmitMs = ZOOM_STEP_INTERVAL_MS; }
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
        if (Math.Abs(_remainingDelta) < 0.1)
        {
            _remainingDelta = 0;
            return 0;
        }

        // Pace: release at most one zoom step per interval. The pacer is primed on idle so
        // the first step of a gesture fires immediately; rapid spins are smoothed into a
        // steady cadence instead of one big jump.
        _sinceEmitMs += dtMs;
        if (_sinceEmitMs < ZOOM_STEP_INTERVAL_MS)
            return 0;
        _sinceEmitMs = 0;

        // Emit one whole notch toward the target (or the sub-notch remainder if less).
        var sign = Math.Sign(_remainingDelta);
        int step = Math.Abs(_remainingDelta) >= WHEEL_DELTA
            ? sign * WHEEL_DELTA
            : (int)_remainingDelta;

        _remainingDelta -= step;
        return step;
    }

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
