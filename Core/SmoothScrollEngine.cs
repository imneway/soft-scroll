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

    private AppSettings _s = AppSettings.CreateDefault();

    // Use constants from ScrollConstants
    private static readonly int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;
    private static readonly int EMIT_UNIT = ScrollConstants.EMIT_UNIT;
    private static readonly double BASE_STEP_PX = ScrollConstants.BASE_STEP_PX;
    private static readonly int PULSE_CLAMP_MIN = ScrollConstants.PULSE_CLAMP_MIN;
    private static readonly int PULSE_CLAMP_MAX = ScrollConstants.PULSE_CLAMP_MAX;

    // Display refresh rate — detected lazily on first Start() to avoid blocking startup
    private static int? DisplayRefreshRate;
    private static readonly object _refreshLock = new();

    // Adaptive frame rate: match display Hz for smoothness, drop to 60fps when idle
    private double _targetFrameMs = 1000.0 / 120; // default 120fps for new instances
    private long _lastWorkTime;

    private const double SPIN_WAIT_COUNT = 10;
    private const int IDLE_TIMEOUT_MS = 2000; // drop to 60fps after 2s idle

    // Notches farther apart than this start a fresh gesture and do NOT inherit velocity,
    // so a long-dead scroll cannot resurrect as momentum ("ghost inertia").
    private const int MOMENTUM_GESTURE_WINDOW_MS = 500;
    private const double MOMENTUM_MIN_VELOCITY = 0.05; // px/ms threshold to arm/keep momentum

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
            _v.RegisterNotch(now, delta * dir, _s);
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
            _v.RegisterNotch(now, delta * dir, customSettings);
        }
        _signal.Set();
    }

    public void OnHWheel(int delta)
    {
        lock (_lock)
        {
            // Axis lock: a horizontal notch cancels any in-flight vertical scroll/momentum.
            _v.Reset();
            var dir = _s.ReverseWheelDirection ? -1 : 1;
            var now = Environment.TickCount64;
            _h.RegisterNotch(now, delta * dir, _s);
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
                // Check if there's anything to emit
                bool workAvailable;
                double remainingTotal;
                lock (_lock)
                {
                    // Gate on HasWork (not RemainingPx alone) so an active/pending momentum
                    // glide keeps the worker running on its own axis.
                    workAvailable = _v.HasWork(_s) || _h.HasWork(_s);
                    remainingTotal = Math.Abs(_v.RemainingPx) + Math.Abs(_h.RemainingPx);
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

                int outV = 0, outH = 0;
                lock (_lock)
                {
                    outV = _v.Step(dt, _s);
                    if (_s.HorizontalSmoothness) outH = _h.Step(dt, _s); else outH = 0;
                }

                // Buffered SendInput: emit both axes in a single call
                if (outV != 0 || outH != 0) SendWheel(outV, outH);

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

        // Emit vertical and horizontal in a single SendInput call to reduce P/Invoke overhead
        if (hMouseData != 0)
        {
            var inputs = new NativeMethods.INPUT[]
            {
                new() { type = 0, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = mouseData } } },
                new() { type = 0, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = hMouseData } } },
            };
            NativeMethods.SendInput(2, inputs, size);
        }
        else if (mouseData != 0)
        {
            var inp = new NativeMethods.INPUT
            {
                type = 0,
                U = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = mouseData }
                }
            };
            NativeMethods.SendInput(1, [inp], size);
        }
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

        // Momentum fields
        public double Velocity;       // px/ms
        public bool InMomentum;
        private double _momentumAccum;

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
            InMomentum = false;
            _momentumAccum = 0;
            AccelFactor = 0;     // next notch restarts acceleration at 1x
            LastNotchTime = 0;   // next notch is treated as a fresh gesture
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
            if (InMomentum) return true;
            if (s.MomentumEnabled && Math.Abs(Velocity) > MOMENTUM_MIN_VELOCITY) return true;
            return false;
        }

        public void RegisterNotch(long nowMs, int delta, AppSettings s)
        {
            // Cancel momentum on new user input
            if (InMomentum)
            {
                InMomentum = false;
                Velocity = 0;
                _momentumAccum = 0;
            }

            if (nowMs - LastNotchTime <= s.AccelerationDeltaMs)
                AccelFactor = Math.Min(s.AccelerationMax, Math.Max(1, AccelFactor + 1));
            else
                AccelFactor = 1;

            var timeSinceLast = nowMs - LastNotchTime;
            LastNotchTime = nowMs;

            var notches = delta / (double)WHEEL_DELTA;
            var pixels = notches * s.StepSizePx * AccelFactor;
            RemainingPx += pixels;

            // Track velocity for momentum. Only notches within the gesture window count;
            // a notch after a longer gap starts a fresh gesture and must NOT inherit stale
            // velocity, otherwise a long-dead scroll can later resurrect as ghost momentum.
            if (s.MomentumEnabled && timeSinceLast > 0 && timeSinceLast <= MOMENTUM_GESTURE_WINDOW_MS)
            {
                Velocity = pixels / timeSinceLast;
            }
            else
            {
                Velocity = 0;
            }
        }

        public int Step(double dtMs, AppSettings s)
        {
            // Momentum phase: if normal scroll finished and velocity is significant
            if (s.MomentumEnabled && !InMomentum && Math.Abs(RemainingPx) < 0.1 && Math.Abs(Velocity) > MOMENTUM_MIN_VELOCITY)
            {
                var elapsed = Environment.TickCount64 - LastNotchTime;
                if (elapsed > 80) // Wait a short moment after last notch
                {
                    InMomentum = true;
                }
            }

            if (InMomentum)
            {
                // Friction: higher value = stops faster. Scale 0-100 to 0.001-0.02
                var friction = 0.001 + (s.MomentumFriction / 100.0) * 0.019;
                Velocity *= Math.Pow(1.0 - friction, dtMs);

                if (Math.Abs(Velocity) < 0.02)
                {
                    InMomentum = false;
                    Velocity = 0;
                    _momentumAccum = 0;
                    return 0;
                }

                var momentumPx = Velocity * dtMs;
                var wheelUnits = (momentumPx / BASE_STEP_PX) * WHEEL_DELTA;
                _momentumAccum += wheelUnits / EMIT_UNIT;

                int mPulses = 0;
                if (Math.Abs(_momentumAccum) >= 1.0)
                {
                    mPulses = (int)_momentumAccum;
                    _momentumAccum -= mPulses;
                }
                if (mPulses == 0) return 0;
                mPulses = Math.Clamp(mPulses, PULSE_CLAMP_MIN, PULSE_CLAMP_MAX);
                return mPulses * EMIT_UNIT;
            }

            // Normal smooth scroll
            if (Math.Abs(RemainingPx) < 0.1)
            {
                RemainingPx = 0;
                UnitAccum = 0;
                return 0;
            }

            var duration = Math.Max(1.0, s.AnimationTimeMs);
            var frac = ComputeEasingFraction(dtMs, duration, s.EasingMode, s.TailToHeadRatio, s.AnimationEasing);

            var emitPx = RemainingPx * frac;
            RemainingPx -= emitPx;

            var wUnits = (emitPx / BASE_STEP_PX) * WHEEL_DELTA;

            var units = wUnits / EMIT_UNIT;
            UnitAccum += units;

            int pulses = 0;
            if (Math.Abs(UnitAccum) >= 1.0)
            {
                pulses = (int)UnitAccum;
                UnitAccum -= pulses;
            }

            if (pulses == 0) return 0;
            pulses = Math.Clamp(pulses, PULSE_CLAMP_MIN, PULSE_CLAMP_MAX);
            return pulses * EMIT_UNIT;
        }
    }
}
