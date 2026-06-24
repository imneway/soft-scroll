using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;
using SoftScroll.Settings;

namespace SoftScroll.Core;

public sealed class SmoothScrollEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);

    private Axis _v = new();
    private Axis _h = new();

    // Unsmoothed (HorizontalSmoothness=off) horizontal wheel units waiting to be emitted
    // 1:1 by the WORKER thread. Never emitted from the hook thread (see OnHWheel).
    private int _hRawPending;

    // Diagnostics for the intermittent "first horizontal flick scrolls too far" report.
    // Count native horizontal events (and their total delta) that land between worker frames:
    // >1 notch in a single frame means the (free-spinning) thumb wheel sent a burst, which we
    // scale per-notch. Aggregated on the hook thread (just int adds), logged by the WORKER so
    // there is never any file I/O on the hook callback.
    private int _hDiagCount;
    private int _hDiagTotalDelta;

    // Wheel-delivery target captured at the last notch. While a scroll/momentum tail is still
    // animating, the worker drops all residual the moment the cursor leaves this target, so the
    // tail can't leak into a different scroll region or window. Re-anchored on every notch.
    private IntPtr _anchorHwnd;
    private NativeMethods.POINT _anchorPos;
    private bool _hasAnchor;

    private AppSettings _s = AppSettings.CreateDefault();

    // Use constants from ScrollConstants
    private static readonly int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;
    private static readonly double BASE_STEP_PX = ScrollConstants.BASE_STEP_PX;
    // Max wheel mouseData emitted in a single frame — caps a fast flick so it can't dump a huge
    // jump at once; the remainder carries to the next frame. (Was PULSE_CLAMP_MAX * EMIT_UNIT.)
    private const int MAX_WHEEL_PER_FRAME = 240;

    // How far the cursor may move from the last notch before the residual scroll is considered
    // aimed at a different region (for single-HWND apps where WindowFromPoint can't tell panes
    // apart). A window/child-region switch is caught by WindowFromPoint regardless of distance.
    private const int TARGET_MOVE_THRESHOLD_PX = 40;

    // Display refresh rate — detected lazily on first Start() to avoid blocking startup
    private static int? DisplayRefreshRate;
    private static readonly object _refreshLock = new();

    // Adaptive frame rate: match display Hz for smoothness, drop to 60fps when idle
    private double _targetFrameMs = 1000.0 / 120; // default 120fps for new instances
    private long _lastWorkTime;

    private const double SPIN_WAIT_COUNT = 10;
    private const int IDLE_TIMEOUT_MS = 2000; // drop to 60fps after 2s idle

    // Momentum is a single velocity (px/ms) decayed by friction each frame. Below this speed
    // the glide is treated as stopped (its remaining tail is sub-pixel and imperceptible).
    private const double MOMENTUM_STOP_VELOCITY = 0.03;
    // Friction (0..100) maps to an exponential decay time constant tau (ms): low friction =
    // long, soft "icy" glide; high friction = short, snappy stop. Velocity *= exp(-dt/tau).
    private const double MOMENTUM_TAU_MAX_MS = 700.0; // friction 0   → longest glide
    private const double MOMENTUM_TAU_MIN_MS = 150.0; // friction 100 → quickest stop
    // A notch is only treated as part of a flick (and can seed a glide) if it lands within this
    // gap of the previous one. A notch after a longer pause is a fresh, deliberate scroll.
    private const double MOMENTUM_FLICK_WINDOW_MS = 250.0;

    private static double MomentumTauMs(int friction)
    {
        var f = Math.Clamp(friction, 0, 100) / 100.0;
        return MOMENTUM_TAU_MAX_MS - f * (MOMENTUM_TAU_MAX_MS - MOMENTUM_TAU_MIN_MS);
    }

    public SmoothScrollEngine(AppSettings settings)
    {
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings s)
    {
        lock (_lock)
        {
            _s = s;
        }
    }

    public void Start()
    {
        // Detect display refresh rate on first start (lazy to avoid blocking app startup)
        if (!DisplayRefreshRate.HasValue)
        {
            lock (_refreshLock)
            {
                if (!DisplayRefreshRate.HasValue)
                    DisplayRefreshRate = NativeMethods.GetDisplayRefreshRate();
            }
            // Target frame rate: match display refresh if >= 60Hz, floor at 120fps
            _targetFrameMs = DisplayRefreshRate.Value >= 120 ? 1000.0 / DisplayRefreshRate.Value : 1000.0 / 120;
        }

        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "SmoothScrollEngine" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            // Reset axis state inside lock to avoid race with worker thread
            _v = new();
            _h = new();
            _hRawPending = 0;
            _hDiagCount = 0;
            _hDiagTotalDelta = 0;
            _hasAnchor = false;
            _anchorHwnd = IntPtr.Zero;
        }
        _signal.Set();
        _thread?.Join(1000);
    }

    public void OnWheel(int delta)
    {
        lock (_lock)
        {
            // Axis lock: a vertical notch cancels any in-flight horizontal scroll/momentum.
            _h.Reset();
            var dir = _s.ReverseWheelDirection ? -1 : 1;
            var now = Environment.TickCount64;
            _v.RegisterNotch(now, delta * dir, _s, _s.MomentumEnabled);
            UpdateAnchor();
        }
        _signal.Set();
    }

    public void OnWheelWithSettings(int delta, AppSettings customSettings)
    {
        lock (_lock)
        {
            // Axis lock: a vertical notch cancels any in-flight horizontal scroll/momentum.
            _h.Reset();
            var dir = customSettings.ReverseWheelDirection ? -1 : 1;
            var now = Environment.TickCount64;
            // Effective momentum = global master AND this profile's toggle (master switch).
            _v.RegisterNotch(now, delta * dir, customSettings, _s.MomentumEnabled && customSettings.MomentumEnabled);
            UpdateAnchor();
        }
        _signal.Set();
    }

    public void OnHWheel(int delta) => OnHWheelCore(delta, _s);

    /// <summary>
    /// Horizontal wheel using a specific (app-profile) settings object for sensitivity,
    /// momentum, etc. The global HorizontalSmoothness still decides smoothed vs raw — it's a
    /// master switch, not a per-profile toggle.
    /// </summary>
    public void OnHWheelWithSettings(int delta, AppSettings customSettings) => OnHWheelCore(delta, customSettings);

    private void OnHWheelCore(int delta, AppSettings s)
    {
        lock (_lock)
        {
            _hDiagCount++;
            _hDiagTotalDelta += delta;
            var dir = s.ReverseWheelDirection ? -1 : 1;
            // Axis lock: a horizontal notch cancels any in-flight vertical scroll/momentum.
            _v.Reset();
            if (_s.HorizontalSmoothness)
            {
                _h.RegisterNotch(Environment.TickCount64, delta * dir, s, _s.MomentumEnabled && s.MomentumEnabled, horizontal: true);
            }
            else
            {
                // Smoothing off: queue a 1:1 raw pulse for the WORKER thread to emit.
                // Do NOT SendInput here — OnHWheel runs on the WH_MOUSE_LL hook callback
                // thread, and injecting input from that thread can wedge the global mouse
                // hook and deadlock ALL desktop input (unkillable hang, reboot required).
                // The native event is swallowed upstream, so the worker re-emits it; this
                // covers both native HWHEEL and Shift+wheel converted to horizontal.
                _hRawPending += delta * dir;
            }
            UpdateAnchor();
        }
        _signal.Set();
    }

    // Records the wheel-delivery target (window + cursor pos) for the notch just registered, so
    // the worker can abort the glide if the cursor later moves to a different region/window.
    // Called under _lock from the hook thread; WindowFromPoint here is a cheap read (the same
    // call CachedProcessHelper already makes per event), never SendInput, so it's hook-safe.
    private void UpdateAnchor()
    {
        if (CursorTarget.TryCapture(out var hwnd, out var pos))
        {
            _anchorHwnd = hwnd;
            _anchorPos = pos;
            _hasAnchor = true;
        }
    }

    /// <summary>
    /// Cancel any in-flight vertical scroll/momentum without emitting anything. Used when a
    /// native horizontal wheel is bypassed (HorizontalSmoothness off): the horizontal event
    /// flows natively, but we still axis-lock so the vertical animation doesn't keep gliding
    /// alongside it (the staircase). Safe on the hook thread — no SendInput, just a state reset.
    /// </summary>
    public void CancelVertical()
    {
        lock (_lock) { _v.Reset(); }
    }

    private void Worker()
    {
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            try
            {
                // Check if there's anything to emit
                bool workAvailable;
                double remainingTotal;
                lock (_lock)
                {
                    // Gate on HasWork (not RemainingPx alone) so an active/pending momentum
                    // glide keeps the worker running on its own axis. Also wake for any
                    // pending unsmoothed horizontal pulse.
                    workAvailable = _v.HasWork(_s) || _h.HasWork(_s) || _hRawPending != 0;
                    remainingTotal = Math.Abs(_v.RemainingPx) + Math.Abs(_h.RemainingPx);

                    // If the cursor has left the region/window this scroll was aimed at, drop every
                    // residual (both axes + raw pending) instead of leaking the tail into the new
                    // target. Re-anchored on each notch, so active scrolling that drifts is fine.
                    // Done in the SAME lock as the live anchor it reads, so a notch arriving
                    // mid-frame (which re-anchors to the new spot) can't be wiped by a stale
                    // compare. GetCursorPos/WindowFromPoint are cheap reads (no SendInput), safe
                    // to hold the lock across.
                    if (workAvailable && _hasAnchor &&
                        CursorTarget.HasChanged(_anchorHwnd, _anchorPos, TARGET_MOVE_THRESHOLD_PX))
                    {
                        _v.Reset();
                        _h.Reset();
                        _hRawPending = 0;
                        _hDiagCount = 0;
                        _hDiagTotalDelta = 0;
                        _hasAnchor = false;
                        workAvailable = false;
                    }
                }

                if (!workAvailable)
                {
                    // Block until a wheel event signals us or timeout elapses.
                    // Timeout guarantees eventual shutdown even if no signal arrives.
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter on new notch
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    _lastWorkTime = Environment.TickCount64;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;
                _lastWorkTime = Environment.TickCount64;

                // Adaptive frame rate computation
                var frameMs = ComputeAdaptiveFrameMs(remainingTotal);

                int outV, outH, diagCount, diagTotal;
                lock (_lock)
                {
                    outV = _v.Step(dt, _s);
                    // When horizontal smoothing is off, _h stays empty (native horizontal is
                    // bypassed upstream; only Shift+wheel-as-horizontal feeds _hRawPending).
                    outH = _h.Step(dt, _s);

                    // Flush any unsmoothed horizontal 1:1 (Shift+wheel converted, smoothing off).
                    if (_hRawPending != 0)
                    {
                        outH += _hRawPending;
                        _hRawPending = 0;
                    }

                    diagCount = _hDiagCount; diagTotal = _hDiagTotalDelta;
                    _hDiagCount = 0; _hDiagTotalDelta = 0;
                }

                // SendInput OUTSIDE the lock. Never hold _lock across SendInput: a physical wheel
                // event on the hook thread must not wait on the lock while we inject (risky with
                // other global hooks like Logi / X-Mouse in the chain). Worst case is one ~frame
                // of an old-axis pulse at a switch instant — a few px, imperceptible; the axis-lock
                // Reset already cancels the real cross-axis tail/momentum.
                if (outV != 0 || outH != 0) SendWheel(outV, outH);

                // Diagnostic (worker thread, never the hook thread): flag a horizontal burst —
                // more than one event, or more than one notch of delta, landing in a single frame.
                if (diagCount > 1 || Math.Abs(diagTotal) > WHEEL_DELTA)
                    Serilog.Log.Debug("[HWheel] burst: {Count} event(s)/frame, totalDelta={Total} (~{Notches:F1} notches), dt={Dt:F1}ms",
                        diagCount, diagTotal, diagTotal / (double)WHEEL_DELTA, dt);

                var sleep = frameMs - (sw.Elapsed.TotalMilliseconds - nowMs);
                if (sleep > 0.5) Thread.Sleep((int)Math.Round(sleep));
                else Thread.SpinWait((int)SPIN_WAIT_COUNT);
            }
            catch (Exception ex)
            {
                // Prevent worker thread from dying silently
                System.Diagnostics.Debug.WriteLine($"SmoothScrollEngine worker: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adaptive frame rate: scales from target (display Hz / 120) down to 60fps when idle.
    /// When remaining scroll is small (&lt; 50px) and no recent notch, drop to 60fps to save CPU.
    /// When remaining is large or recent rapid notches, ramp up to target Hz.
    /// </summary>
    private double ComputeAdaptiveFrameMs(double remainingPx)
    {
        var idleTime = Environment.TickCount64 - _lastWorkTime;

        // Idle ≥ 2s → drop to 60fps
        if (idleTime >= IDLE_TIMEOUT_MS)
            return 1000.0 / 60;

        // Active scrolling: use target (display-matched) frame rate
        return _targetFrameMs;
    }

    private static void SendWheel(int mouseData, int hMouseData)
    {
        var size = Marshal.SizeOf<NativeMethods.INPUT>();

        NativeMethods.INPUT Vert() => new() { type = 0, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = mouseData } } };
        NativeMethods.INPUT Horz() => new() { type = 0, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = hMouseData } } };

        // Emit only the axes that actually have a delta — never inject a zero-delta wheel
        // (a stray mouseData=0 vertical wheel could confuse downstream apps/hooks).
        if (mouseData != 0 && hMouseData != 0)
            NativeMethods.SendInput(2, [Vert(), Horz()], size);
        else if (mouseData != 0)
            NativeMethods.SendInput(1, [Vert()], size);
        else if (hMouseData != 0)
            NativeMethods.SendInput(1, [Horz()], size);
    }

    public void Dispose() => Stop();

    public static double ComputeEasingFraction(double dtMs, double duration, EasingMode mode, double tailToHeadRatio, bool easingEnabled)
    {
        if (!easingEnabled || mode == EasingMode.Linear)
        {
            return Math.Min(1.0, dtMs / duration);
        }

        var t = dtMs / duration;

        return mode switch
        {
            EasingMode.CubicOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 3),
            EasingMode.QuinticOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 5),
            _ => 1.0 - Math.Exp(-(2.0 + tailToHeadRatio) * t) // ExponentialOut (default)
        };
    }

    private struct Axis
    {
        public double RemainingPx;
        public long LastNotchTime;
        public int AccelFactor;
        public double UnitAccum;

        // Momentum: a single velocity (px/ms). RegisterNotch feeds it impulses while the user
        // scrolls; Step decays it by friction. One continuous glide — no separate easing phase.
        public double Velocity;       // px/ms

        // Settings captured at the last notch — the per-app-profile (or global) settings
        // this scroll/momentum should animate with. The worker passes global settings as a
        // fallback, but Step/HasWork prefer these so an app profile fully controls the feel
        // (duration, easing, momentum on/off + friction), not just the per-notch distance.
        public AppSettings? ActiveSettings;

        /// <summary>
        /// Clears all animation state. Used for axis-lock: when the user scrolls on one
        /// axis, the other axis is reset so its residual scroll/momentum cannot leak out
        /// alongside the new axis (no diagonal/staircase drift, no ghost momentum).
        /// </summary>
        public void Reset()
        {
            RemainingPx = 0;
            UnitAccum = 0;
            Velocity = 0;
            AccelFactor = 0;     // next notch restarts acceleration at 1x
            LastNotchTime = 0;   // next notch is treated as a fresh gesture
            ActiveSettings = null;
        }

        /// <summary>
        /// True when this axis still has scrolling to emit: pending pixels, an active
        /// momentum glide, or a fresh velocity that is about to arm into momentum.
        /// The worker must gate on this (not RemainingPx alone) so a momentum glide keeps
        /// running on its own — otherwise momentum freezes the instant RemainingPx hits ~0
        /// and only animates when the OTHER axis happens to keep the worker awake.
        /// </summary>
        public bool HasWork(AppSettings s)
        {
            if (Math.Abs(RemainingPx) >= 0.1) return true;
            // A momentum glide keeps the worker awake on its own axis until it decays out.
            // Gated by the global master switch AND the profile that started the scroll.
            var st = ActiveSettings ?? s;
            if (s.MomentumEnabled && st.MomentumEnabled && Math.Abs(Velocity) > MOMENTUM_STOP_VELOCITY) return true;
            return false;
        }

        // momentumActive is the EFFECTIVE momentum flag (global master AND the active profile),
        // computed by the caller. RegisterNotch must agree with Step/HasWork on which mode this
        // scroll uses: if it wrote Velocity while Step ran the easing path (or vice-versa), the
        // swallowed wheel would emit nothing. So the mode decision lives in one flag, not in
        // s.MomentumEnabled (which for a profile ignores the global off switch).
        public void RegisterNotch(long nowMs, int delta, AppSettings s, bool momentumActive, bool horizontal = false)
        {
            // Capture the settings this scroll should animate with (app profile or global)
            // so Step/HasWork use them later instead of whatever global settings are live then.
            ActiveSettings = s;

            // Horizontal has its own step size and acceleration cap (independent of vertical)
            // so a free-spinning thumb wheel can be tamed without touching vertical feel.
            var stepPx = horizontal ? s.HorizontalStepSizePx : s.StepSizePx;
            var accelMax = horizontal ? s.HorizontalAccelerationMax : s.AccelerationMax;

            var timeSinceLast = nowMs - LastNotchTime;

            if (timeSinceLast <= s.AccelerationDeltaMs)
                AccelFactor = Math.Min(accelMax, Math.Max(1, AccelFactor + 1));
            else
                AccelFactor = 1;

            LastNotchTime = nowMs;

            var notches = delta / (double)WHEEL_DELTA;
            var pixels = notches * stepPx * AccelFactor;

            // A direction reversal cancels any in-flight glide so the new direction starts crisp.
            if (Velocity != 0 && Math.Sign(pixels) != Math.Sign(Velocity))
                Velocity = 0;

            // The threshold picks this notch's MODE by input speed:
            //   slow (below threshold) → crisp easing target, no inertia;
            //   fast (at/above threshold) → the original velocity-friction momentum impulse.
            // So slow reading has no glide and a fast scroll gets the old inertia. The impulse is
            // pixels/tau — bounded, and it stacks with rapid notches — NOT vIn (pixels/gap), which
            // blows up for small gaps (that was the "flings too far"). A fresh notch after a pause
            // is never fast (large gap → low speed → easing). Both layers can coexist within a
            // gesture and Step emits them concurrently, so a slow→fast transition has no jump.
            bool fast = false;
            if (momentumActive && timeSinceLast > 0 && timeSinceLast <= MOMENTUM_FLICK_WINDOW_MS)
            {
                var speed = Math.Abs(pixels) / timeSinceLast;       // px/ms — input scroll speed
                fast = speed >= s.MomentumFlickThreshold / 1000.0;  // threshold px/s → px/ms
            }

            if (fast)
                Velocity += pixels / MomentumTauMs(s.MomentumFriction);
            else
                RemainingPx += pixels;
        }

        public int Step(double dtMs, AppSettings s)
        {
            // Use the settings captured at the last notch (app profile or global) so a profile
            // controls duration/easing and momentum friction, not just the per-notch distance.
            var st = ActiveSettings ?? s;

            // Momentum is gated by BOTH the global toggle (s) and the profile (st): turning
            // momentum off globally is a master switch — no glide anywhere, even in a profiled app.
            bool momentum = s.MomentumEnabled && st.MomentumEnabled;
            if (!momentum || Math.Abs(Velocity) < MOMENTUM_STOP_VELOCITY)
                Velocity = 0;

            bool easingDone = Math.Abs(RemainingPx) < 0.1;
            if (easingDone) RemainingPx = 0;

            if (easingDone && Velocity == 0)
            {
                UnitAccum = 0;
                return 0;
            }

            double emit = 0;

            // Easing base layer: front-loaded crisp response, settles over AnimationTimeMs.
            if (!easingDone)
            {
                var duration = Math.Max(1.0, st.AnimationTimeMs);
                var frac = ComputeEasingFraction(dtMs, duration, st.EasingMode, st.TailToHeadRatio, st.AnimationEasing);
                var e = RemainingPx * frac;
                RemainingPx -= e;
                emit += e;
            }

            // Momentum glide layer (concurrent): an inertial tail that decays by friction. It runs
            // ALONGSIDE the easing from the first frame, so when the easing finishes there is no
            // jump — the glide is already mid-decay and just becomes the sole contributor.
            if (Velocity != 0)
            {
                emit += Velocity * dtMs;
                Velocity *= Math.Exp(-dtMs / MomentumTauMs(st.MomentumFriction));
            }

            return EmitPixels(emit);
        }

        // Shared px → raw wheel-delta accumulator. Emits the whole accumulated mouseData EVERY
        // frame (1 px ≈ 1 mouseData here, since BASE_STEP_PX == WHEEL_DELTA). The old version
        // quantized to EMIT_UNIT (12) chunks, so at low speed — a momentum glide or the tail of
        // an easing curve — a pulse only crossed the threshold every few frames, which reads as
        // stepping / dropped frames. Emitting down to 1 mouseData/frame keeps slow motion smooth.
        private int EmitPixels(double px)
        {
            UnitAccum += (px / BASE_STEP_PX) * WHEEL_DELTA;
            int whole = (int)UnitAccum;
            if (whole == 0) return 0;
            whole = Math.Clamp(whole, -MAX_WHEEL_PER_FRAME, MAX_WHEEL_PER_FRAME);
            UnitAccum -= whole;
            return whole;
        }
    }
}
