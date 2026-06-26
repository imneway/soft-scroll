using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftScroll.Infrastructure;

namespace SoftScroll.Settings;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EasingMode
{
    ExponentialOut,
    CubicOut,
    QuinticOut,
    Linear
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    // ── Scroll Settings ─────────────────────────────────────────────
    public int StepSizePx { get; set; } = 120;
    public int AnimationTimeMs { get; set; } = 360;
    public int AccelerationDeltaMs { get; set; } = 70;
    public int AccelerationMax { get; set; } = 7;
    public int TailToHeadRatio { get; set; } = 3;
    public bool AnimationEasing { get; set; } = true;
    public EasingMode EasingMode { get; set; } = EasingMode.ExponentialOut;

    // ── Direction & Horizontal ──────────────────────────────────────
    public bool ShiftKeyHorizontal { get; set; } = true;
    public bool HorizontalSmoothness { get; set; } = true;
    public bool ReverseWheelDirection { get; set; } = false;
    // Reverse ONLY the horizontal wheel (the thumb wheel), independent of the main wheel's
    // ReverseWheelDirection. Applies to both real horizontal scroll and the horizontal→vertical
    // mapping (it flips whichever direction the thumb wheel drives). Combined with the main reverse
    // via XOR, so natural-scroll + this toggle each flip the thumb wheel once.
    public bool ReverseHorizontalDirection { get; set; } = false;
    // Horizontal scroll has its own sensitivity, independent of the vertical StepSizePx /
    // AccelerationMax — a thumb wheel (e.g. MX Master) is far easier to over-spin, so it
    // gets a gentler default step and acceleration off by default (max = 1 = no accel).
    // Only applies when HorizontalSmoothness is on (otherwise the native event is bypassed).
    public int HorizontalStepSizePx { get; set; } = 80;
    public int HorizontalAccelerationMax { get; set; } = 1;
    // Map the horizontal wheel (e.g. a thumb wheel) onto VERTICAL scrolling — a built-in
    // replacement for remapping it with X-Mouse/Logi. When on, native WM_MOUSEHWHEEL is routed
    // through the (always-smoothed) vertical pipeline using the HORIZONTAL step/accel above, so
    // the thumb wheel keeps its own sensitivity independent of the main wheel. Off by default;
    // per-app overridable via AppProfile.HorizontalToVertical (e.g. keep real horizontal in Figma).
    // Shift+wheel-as-horizontal is never remapped — that gesture explicitly asks for horizontal.
    public bool HorizontalToVertical { get; set; } = false;

    // ── Startup & UI ────────────────────────────────────────────────
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public string Language { get; set; } = LocalizationManager.DetectSystemLanguage();

    // ── Advanced Features ──────────────────────────────────────────
    public bool ZoomSmoothing { get; set; } = true;
    // Treat Ctrl + horizontal wheel (e.g. a thumb wheel) as a ZOOM gesture, routing it to the
    // zoom engine instead of horizontal scroll. Off by default — not every mouse/app uses the
    // thumb wheel to zoom. Per-app overridable via AppProfile.CtrlHorizontalZoom.
    public bool CtrlHorizontalZoom { get; set; } = false;
    public bool MomentumEnabled { get; set; } = false;
    public int MomentumFriction { get; set; } = 50;
    // Flick threshold (px/s): momentum (the inertial glide) only engages when scroll speed
    // exceeds this, and only by the excess. Below it, scrolling is pure crisp easing — so slow
    // reading stays snappy and stops cleanly. 0 = glide on any motion; higher = needs a faster
    // flick. This is what makes inertia speed-based (light scroll → none, hard flick → strong).
    public int MomentumFlickThreshold { get; set; } = 1200;
    public bool MiddleClickScroll { get; set; } = true;
    public int MiddleClickDeadZone { get; set; } = 10;
    public bool AutoDisableOnTouchpad { get; set; } = true;

    // ── App Management ─────────────────────────────────────────────
    public List<string> ExcludedApps { get; set; } = new();
    public List<AppProfile> AppProfiles { get; set; } = new();
    public bool UseAppProfiles { get; set; } = true;

    // ── Quick Toggle ────────────────────────────────────────────────
    public bool EnableGlobalHotkey { get; set; } = true;
    public bool ShowTrayIconState { get; set; } = true;

    // ── Diagnostics & Statistics ───────────────────────────────────
    // Tally scroll events/pixels for the Statistics page. On by default; turning it off skips the
    // per-event RecordScroll bookkeeping (a few interlocked adds) on the hook thread.
    public bool CollectStatistics { get; set; } = true;
    // Verbose per-wheel-event routing trace to the log file (WheelTrace). Diagnostic only and off
    // by default — it writes a line per wheel event and is a measurable source of frame drops.
    public bool EnableDiagnosticTracing { get; set; } = false;

    // ── Visual Feedback ─────────────────────────────────────────────
    public bool ShowScrollIndicator { get; set; } = false;
    public int ScrollIndicatorDurationMs { get; set; } = 500;
    public IndicatorPosition IndicatorPosition { get; set; } = IndicatorPosition.TopRight;

    // ── Middle-Click Settings ──────────────────────────────────────
    public MiddleClickSettings MiddleClickConfig { get; set; } = new();

    // ── Accessibility ─────────────────────────────────────────────
    public AccessibilitySettings Accessibility { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    public static AppSettings CreatePreset(string presetName) => presetName switch
    {
        "Reading" => new()
        {
            StepSizePx = 80,
            AnimationTimeMs = 500,
            AccelerationDeltaMs = 100,
            AccelerationMax = 3,
            TailToHeadRatio = 5,
            AnimationEasing = true,
            EasingMode = EasingMode.CubicOut
        },
        "Productivity" => new()
        {
            StepSizePx = 160,
            AnimationTimeMs = 250,
            AccelerationDeltaMs = 60,
            AccelerationMax = 10,
            TailToHeadRatio = 2,
            AnimationEasing = true,
            EasingMode = EasingMode.ExponentialOut
        },
        "Gaming" => new()
        {
            StepSizePx = 120,
            AnimationTimeMs = 100,
            AccelerationDeltaMs = 40,
            AccelerationMax = 5,
            TailToHeadRatio = 1,
            AnimationEasing = false,
            EasingMode = EasingMode.Linear
        },
        _ => CreateDefault()
    };

    public static string GetConfigPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftScroll");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetConfigPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    s.Clamp();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AppSettings] Failed to load settings");
        }
        return CreateDefault();
    }

    /// <summary>
    /// Clamps all numeric properties to valid ranges. Call after deserialization
    /// to protect against corrupted or hand-edited settings files.
    /// </summary>
    internal void Clamp()
    {
        StepSizePx = Math.Clamp(StepSizePx, 10, 500);
        HorizontalStepSizePx = Math.Clamp(HorizontalStepSizePx, 1, 500);
        HorizontalAccelerationMax = Math.Clamp(HorizontalAccelerationMax, 1, 20);
        AnimationTimeMs = Math.Clamp(AnimationTimeMs, 10, 2000);
        AccelerationDeltaMs = Math.Clamp(AccelerationDeltaMs, 0, 500);
        AccelerationMax = Math.Clamp(AccelerationMax, 1, 20);
        TailToHeadRatio = Math.Clamp(TailToHeadRatio, 1, 20);
        MomentumFriction = Math.Clamp(MomentumFriction, 0, 100);
        MomentumFlickThreshold = Math.Clamp(MomentumFlickThreshold, 0, 5000);
        MiddleClickDeadZone = Math.Clamp(MiddleClickDeadZone, 0, 100);

        if (!LocalizationManager.SupportedLanguages.Contains(Language))
            Language = "en";

        ExcludedApps ??= new List<string>();
        AppProfiles ??= new List<AppProfile>();
        foreach (var p in AppProfiles) p.Clamp();   // clamp only each profile's overridden fields

        MiddleClickConfig ??= new MiddleClickSettings();
        Accessibility ??= new AccessibilitySettings();
        ScrollIndicatorDurationMs = Math.Clamp(ScrollIndicatorDurationMs, 100, 3000);

        if (MiddleClickConfig.CursorSize < 16) MiddleClickConfig.CursorSize = 16;
        if (MiddleClickConfig.CursorSize > 64) MiddleClickConfig.CursorSize = 64;
        if (MiddleClickConfig.BounceStrength < 0) MiddleClickConfig.BounceStrength = 0;
        if (MiddleClickConfig.BounceStrength > 100) MiddleClickConfig.BounceStrength = 100;

        if (Accessibility.AudioVolume < 0) Accessibility.AudioVolume = 0;
        if (Accessibility.AudioVolume > 1) Accessibility.AudioVolume = 1;
    }

    // Shallow copy — used to resolve a per-app profile against the live global settings
    // (AppProfile.ToAppSettings). The engine only reads scalar feel fields off the result, so the
    // shared list references (ExcludedApps/AppProfiles) are never mutated through the copy.
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Save()
    {
        try
        {
            var path = GetConfigPath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AppSettings] Failed to save settings");
        }
    }

    public bool IsExcluded(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var app in ExcludedApps)
        {
            if (string.Equals(app, processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public AppProfile? GetAppProfile(string? processName)
    {
        if (!UseAppProfiles || string.IsNullOrEmpty(processName))
            return null;

        foreach (var profile in AppProfiles)
        {
            if (string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return null;
    }
}
