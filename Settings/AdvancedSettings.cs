using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftScroll.Settings;

public class AccessibilitySettings
{
    public bool EnableScreenReaderAnnouncements { get; set; } = false;
    public bool AnnounceScrollPosition { get; set; } = true;
    public bool AnnounceScrollSpeed { get; set; } = false;
    public bool HighContrastMode { get; set; } = false;
    public bool EnableAudioFeedback { get; set; } = false;
    public float AudioVolume { get; set; } = 0.5f;
    public SoundType ScrollSound { get; set; } = SoundType.SoftTick;
}

public enum SoundType
{
    SoftTick,
    Click,
    Custom
}

public enum CursorStyle
{
    Arrow,
    Hand,
    Custom
}

public class MiddleClickSettings
{
    public bool ShowCursor { get; set; } = true;
    public CursorStyle CursorStyle { get; set; } = CursorStyle.Arrow;
    public int CursorSize { get; set; } = 32;
    public bool InvertScrollDirection { get; set; } = false;
    public bool EnableEdgeBounce { get; set; } = true;
    public int BounceStrength { get; set; } = 20;
}

public class AppProfile
{
    public string AppName { get; set; } = "";
    public string ProcessName { get; set; } = "";

    // Every tunable field is nullable: null = follow the global setting (live — a later global
    // change is picked up automatically), a value = override. Serialized with WhenWritingNull so a
    // fully-following profile collapses to just {AppName, ProcessName, Enabled} on disk.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? StepSizePx { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AnimationTimeMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EasingMode? EasingMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AccelerationDeltaMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AccelerationMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TailToHeadRatio { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AnimationEasing { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? HorizontalStepSizePx { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? HorizontalAccelerationMax { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? MomentumEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MomentumFriction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MomentumFlickThreshold { get; set; }
    // Per-app override of the global "Ctrl + horizontal wheel = zoom" toggle (e.g. zoom-on-thumb-
    // wheel only in design apps). null follows the global flag.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CtrlHorizontalZoom { get; set; }
    // Per-app override of the global "horizontal wheel scrolls vertically" toggle. Lets the thumb
    // wheel act as vertical scroll everywhere but stay true horizontal in a specific app (Figma).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? HorizontalToVertical { get; set; }
    // Per-app reverse of the horizontal (thumb) wheel direction — e.g. flip the mapped vertical
    // scroll without touching real horizontal scroll in another app.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ReverseHorizontalDirection { get; set; }
    public bool Enabled { get; set; } = true;

    // Resolve to a concrete AppSettings for the engine. Starts from a copy of the LIVE global
    // settings so every field this profile does not override — including global-only ones the
    // profile doesn't expose (ReverseWheelDirection, smoothing flags, …) — comes from global.
    public AppSettings ToAppSettings(AppSettings global)
    {
        var s = global.Clone();
        if (StepSizePx.HasValue) s.StepSizePx = StepSizePx.Value;
        if (AnimationTimeMs.HasValue) s.AnimationTimeMs = AnimationTimeMs.Value;
        if (this.EasingMode.HasValue) s.EasingMode = this.EasingMode.Value;
        if (AccelerationDeltaMs.HasValue) s.AccelerationDeltaMs = AccelerationDeltaMs.Value;
        if (AccelerationMax.HasValue) s.AccelerationMax = AccelerationMax.Value;
        if (TailToHeadRatio.HasValue) s.TailToHeadRatio = TailToHeadRatio.Value;
        if (AnimationEasing.HasValue) s.AnimationEasing = AnimationEasing.Value;
        if (HorizontalStepSizePx.HasValue) s.HorizontalStepSizePx = HorizontalStepSizePx.Value;
        if (HorizontalAccelerationMax.HasValue) s.HorizontalAccelerationMax = HorizontalAccelerationMax.Value;
        if (MomentumEnabled.HasValue) s.MomentumEnabled = MomentumEnabled.Value;
        if (MomentumFriction.HasValue) s.MomentumFriction = MomentumFriction.Value;
        if (MomentumFlickThreshold.HasValue) s.MomentumFlickThreshold = MomentumFlickThreshold.Value;
        if (CtrlHorizontalZoom.HasValue) s.CtrlHorizontalZoom = CtrlHorizontalZoom.Value;
        if (HorizontalToVertical.HasValue) s.HorizontalToVertical = HorizontalToVertical.Value;
        if (ReverseHorizontalDirection.HasValue) s.ReverseHorizontalDirection = ReverseHorizontalDirection.Value;
        return s;
    }

    // A brand-new profile follows global for everything (all fields null).
    public static AppProfile CreateFollowing(string appName, string processName)
        => new() { AppName = appName, ProcessName = processName };

    // Clamp only the overridden (non-null) fields; following fields stay null.
    public void Clamp()
    {
        if (StepSizePx.HasValue) StepSizePx = Math.Clamp(StepSizePx.Value, 10, 500);
        if (AnimationTimeMs.HasValue) AnimationTimeMs = Math.Clamp(AnimationTimeMs.Value, 10, 2000);
        if (AccelerationDeltaMs.HasValue) AccelerationDeltaMs = Math.Clamp(AccelerationDeltaMs.Value, 0, 500);
        if (AccelerationMax.HasValue) AccelerationMax = Math.Clamp(AccelerationMax.Value, 1, 20);
        if (TailToHeadRatio.HasValue) TailToHeadRatio = Math.Clamp(TailToHeadRatio.Value, 1, 20);
        if (HorizontalStepSizePx.HasValue) HorizontalStepSizePx = Math.Clamp(HorizontalStepSizePx.Value, 1, 500);
        if (HorizontalAccelerationMax.HasValue) HorizontalAccelerationMax = Math.Clamp(HorizontalAccelerationMax.Value, 1, 20);
        if (MomentumFriction.HasValue) MomentumFriction = Math.Clamp(MomentumFriction.Value, 0, 100);
        if (MomentumFlickThreshold.HasValue) MomentumFlickThreshold = Math.Clamp(MomentumFlickThreshold.Value, 0, 5000);
    }
}

public enum IndicatorPosition
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft,
    Center
}

public class ScrollStatistics
{
    private static readonly ScrollStatistics _instance = new();
    public static ScrollStatistics Instance => _instance;

    private long _totalScrollEvents;
    private long _totalPixelsScrolled;
    private long _sessionScrollEvents;
    private long _sessionPixelsScrolled;
    private DateTime _sessionStart = DateTime.Now;
    private readonly object _lock = new();

    public long TotalScrollEvents => _totalScrollEvents;
    public long TotalPixelsScrolled => _totalPixelsScrolled;
    public long SessionScrollEvents => _sessionScrollEvents;
    public long SessionPixelsScrolled => _sessionPixelsScrolled;
    public TimeSpan ActiveTime => DateTime.Now - _sessionStart;
    public DateTime SessionStart => _sessionStart;

    public string FormattedActiveTime
    {
        get
        {
            var ts = ActiveTime;
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }

    public string FormattedTotalPixels
    {
        get
        {
            if (_totalPixelsScrolled >= 1_000_000)
                return $"{_totalPixelsScrolled / 1_000_000.0:F1}M px";
            if (_totalPixelsScrolled >= 1_000)
                return $"{_totalPixelsScrolled / 1_000.0:F1}K px";
            return $"{_totalPixelsScrolled} px";
        }
    }

    public string FormattedSessionPixels
    {
        get
        {
            if (_sessionPixelsScrolled >= 1_000_000)
                return $"{_sessionPixelsScrolled / 1_000_000.0:F1}M px";
            if (_sessionPixelsScrolled >= 1_000)
                return $"{_sessionPixelsScrolled / 1_000.0:F1}K px";
            return $"{_sessionPixelsScrolled} px";
        }
    }

    public void RecordScroll(int pixels)
    {
        // Lock-free: Interlocked already serializes each field, so the surrounding lock was pure
        // overhead on the hook thread (this runs per wheel event). Reset() still locks; a benign
        // race there only skews a counter by one event, which is fine for display stats.
        long px = Math.Abs((long)pixels);
        System.Threading.Interlocked.Increment(ref _totalScrollEvents);
        System.Threading.Interlocked.Increment(ref _sessionScrollEvents);
        System.Threading.Interlocked.Add(ref _totalPixelsScrolled, px);
        System.Threading.Interlocked.Add(ref _sessionPixelsScrolled, px);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _totalScrollEvents = 0;
            _totalPixelsScrolled = 0;
            _sessionScrollEvents = 0;
            _sessionPixelsScrolled = 0;
            _sessionStart = DateTime.Now;
        }
    }

    public void ResetSession()
    {
        lock (_lock)
        {
            _sessionScrollEvents = 0;
            _sessionPixelsScrolled = 0;
            _sessionStart = DateTime.Now;
        }
    }
}

public static class PresetManager
{
    public static AppSettings FromPreset(string presetName)
    {
        return presetName switch
        {
            "Reading" => new AppSettings
            {
                StepSizePx = 80,
                AnimationTimeMs = 500,
                AccelerationDeltaMs = 100,
                AccelerationMax = 3,
                TailToHeadRatio = 5,
                AnimationEasing = true,
                EasingMode = EasingMode.CubicOut,
                MomentumEnabled = true,
                MomentumFriction = 70
            },
            "Productivity" => new AppSettings
            {
                StepSizePx = 160,
                AnimationTimeMs = 250,
                AccelerationDeltaMs = 60,
                AccelerationMax = 10,
                TailToHeadRatio = 2,
                AnimationEasing = true,
                EasingMode = EasingMode.ExponentialOut,
                MomentumEnabled = true,
                MomentumFriction = 40
            },
            "Gaming" => new AppSettings
            {
                StepSizePx = 300,
                AnimationTimeMs = 50,
                AccelerationDeltaMs = 40,
                AccelerationMax = 5,
                TailToHeadRatio = 1,
                AnimationEasing = false,
                EasingMode = EasingMode.Linear,
                MomentumEnabled = false,
                MomentumFriction = 50
            },
            "Speed" => new AppSettings
            {
                StepSizePx = 200,
                AnimationTimeMs = 80,
                AccelerationDeltaMs = 50,
                AccelerationMax = 8,
                TailToHeadRatio = 1,
                AnimationEasing = true,
                EasingMode = EasingMode.Linear,
                MomentumEnabled = true,
                MomentumFriction = 30
            },
            "Precise" => new AppSettings
            {
                StepSizePx = 30,
                AnimationTimeMs = 100,
                AccelerationDeltaMs = 80,
                AccelerationMax = 2,
                TailToHeadRatio = 2,
                AnimationEasing = true,
                EasingMode = EasingMode.Linear,
                MomentumEnabled = true,
                MomentumFriction = 80
            },
            _ => AppSettings.CreateDefault()
        };
    }

    public static string[] GetPresetNames() => new[] { "Default", "Reading", "Productivity", "Gaming", "Speed", "Precise" };
}
