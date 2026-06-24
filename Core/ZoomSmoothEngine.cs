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

    // Wheel-delivery target captured on the last zoom notch. If the cursor leaves it mid-glide
    // we drop the backlog so the residual zoom can't land in a different window/region.
    private IntPtr _anchorHwnd;
    private NativeMethods.POINT _anchorPos;
    private bool _hasAnchor;

    // Diagnostics for the intermittent "occasional large zoom" report on the Ctrl+wheel → zoom
    // path (e.g. a thumb wheel remapped to vertical, scrolled with Ctrl). Counts zoom events and
    // their total delta landing between worker frames: >1 event or >1 notch of delta in a single
    // frame is a burst the engine accumulates into one large eased zoom. Aggregated on the hook
    // thread (int adds only); the WORKER logs it, so there is never file I/O on the hook callback.
    private int _diagCount;
    private int _diagTotalDelta;
    // Counts zoom events this frame that only reached zoom because a live Ctrl re-check overrode a
    // stale "Ctrl up" — i.e. misroutes-to-accelerated-scroll that were prevented (the root cause of
    // the "sudden large zoom"). Logged by the worker as positive confirmation the guard fired.
    private int _diagRecoverCount;

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
    // Cursor move (px) past which the residual zoom is treated as aimed at a different region;
    // a window/child switch is caught by WindowFromPoint regardless of distance.
    private const int TARGET_MOVE_THRESHOLD_PX = 40;

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
            _hasAnchor = false;
            _anchorHwnd = IntPtr.Zero;
            _diagCount = 0;
            _diagTotalDelta = 0;
            _diagRecoverCount = 0;
        }
        _thread?.Join(1000);
    }

    /// <summary>
    /// Cancel any in-flight zoom glide without emitting. Used as a cross-engine mode-lock: when
    /// regular scrolling (vertical or horizontal) takes over, the zoom tail must stop instead of
    /// continuing to zoom — including the Ctrl-still-held case the <c>!CtrlDown()</c> guard can't
    /// catch. Safe on the hook thread — a locked state reset, no SendInput.
    /// </summary>
    public void Cancel()
    {
        lock (_lock) { _remainingDelta = 0; _emitAccum = 0; _hasAnchor = false; }
    }

    public void OnZoom(int delta, bool ctrlRecovered = false)
    {
        lock (_lock)
        {
            _diagCount++;
            _diagTotalDelta += delta;
            if (ctrlRecovered) _diagRecoverCount++;
            _remainingDelta = Math.Clamp(_remainingDelta + delta, -MAX_BACKLOG, MAX_BACKLOG);
            // Re-anchor each notch to the current target so the glide is aborted only if the
            // cursor later leaves it. WindowFromPoint here is a cheap read, safe on the hook thread.
            if (CursorTarget.TryCapture(out var hwnd, out var pos))
            {
                _anchorHwnd = hwnd;
                _anchorPos = pos;
                _hasAnchor = true;
            }
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
                double backlog;
                int diagCount, diagTotal, diagRecover;
                lock (_lock)
                {
                    workAvailable = Math.Abs(_remainingDelta) >= 0.1;
                    backlog = _remainingDelta; // post-accumulation, before any guard drops it
                    diagCount = _diagCount; diagTotal = _diagTotalDelta; diagRecover = _diagRecoverCount;
                    _diagCount = 0; _diagTotalDelta = 0; _diagRecoverCount = 0;

                    // Cursor left the window/region the zoom was aimed at → drop the backlog so the
                    // residual zoom can't land in a different target. Checked in the same lock as
                    // the live anchor (cheap reads, no SendInput) so a notch arriving mid-frame
                    // can't be wiped by a stale compare.
                    if (workAvailable && _hasAnchor &&
                        CursorTarget.HasChanged(_anchorHwnd, _anchorPos, TARGET_MOVE_THRESHOLD_PX))
                    {
                        _remainingDelta = 0;
                        _emitAccum = 0;
                        _hasAnchor = false;
                        workAvailable = false;
                    }
                }

                // Diagnostic (worker thread, never the hook thread): flag a zoom burst — more than
                // one event, or more than one notch of delta, accumulated in a single frame. Pair a
                // visible "sudden large zoom" with one of these lines (note its backlog ≈ how many
                // notches will be zoomed). If a big zoom has NO line here, it isn't this path.
                if (diagCount > 1 || Math.Abs(diagTotal) > WHEEL_DELTA)
                    Serilog.Log.Debug("[Zoom] burst: {Count} event(s)/frame, totalDelta={Total} (~{Notches:F1} notches), backlog={Backlog:F0}",
                        diagCount, diagTotal, diagTotal / (double)WHEEL_DELTA, backlog);

                // Positive confirmation that the live-Ctrl re-check caught a stale-Ctrl misroute —
                // the event(s) that would have become the "sudden large zoom" via accelerated scroll.
                if (diagRecover > 0)
                    Serilog.Log.Debug("[Zoom] recovered {Count} stale-Ctrl event(s)/frame — a Ctrl+wheel zoom the 60fps sampler would have misrouted to accelerated scroll (the sudden large zoom).",
                        diagRecover);

                // Zoom only happens while Ctrl is physically held (the injected wheel inherits
                // MK_CONTROL). If the user released Ctrl with a backlog still queued, those
                // pulses would land as plain scrolling — so drop the backlog and go idle.
                if (workAvailable && !CtrlDown())
                {
                    lock (_lock) { _remainingDelta = 0; _emitAccum = 0; _hasAnchor = false; }
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
