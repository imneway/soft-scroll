using System;
using System.Windows;
using Serilog;
using SoftScroll.Core;
using SoftScroll.Hooks;
using SoftScroll.Native;
using SoftScroll.Settings;
using SoftScroll.Infrastructure;
using SoftScroll.UI;

namespace SoftScroll;

public partial class App : System.Windows.Application
{
    private TrayIcon? _tray;
    private GlobalMouseHook? _hook;
    private GlobalHotkey? _hotkey;
    private SettingsViewModel? _vm;
    private SmoothScrollEngine? _engine;
    private ZoomSmoothEngine? _zoomEngine;
    private MiddleClickScrollEngine? _middleClickEngine;
    private MiddleClickOverlay? _middleClickOverlay;
    private ScrollIndicator? _scrollIndicator;
    private SettingsWindow? _settingsWindow;
    private InputDeviceDetector? _inputDetector;
    private RawInputListener? _rawInputListener;
    private AppSettings _settings = null!;

    // Static event for device state changes - used by SettingsWindow to update UI
    public static event EventHandler<DeviceStateEventArgs>? DeviceStateChanged;

    // Debounced exclusion: check process name every 50 ms instead of every wheel event
    private readonly object _exclusionLock = new();
    private string? _lastExcludedProcess;
    private long _lastExcludedCheck;
    private const long EXCLUSION_CHECK_MS = 50;

    protected override void OnStartup(StartupEventArgs e)
    {
        LoggingConfig.Configure();

        NativeMethods.timeBeginPeriod(1);

        base.OnStartup(e);

        _settings = AppSettings.Load();
        WheelTrace.Enabled = _settings.EnableDiagnosticTracing;
        _vm = new SettingsViewModel(_settings);
        _vm.SettingsChanged += (_, _) =>
        {
            _tray?.UpdateEnabled(_vm.Enabled);
            _tray?.RefreshLocalization();
            var snapshot = _vm.Snapshot();
            _settings = snapshot;
            WheelTrace.Enabled = snapshot.EnableDiagnosticTracing;
            _engine?.ApplySettings(snapshot);
            _middleClickEngine?.UpdateDeadZone(snapshot.MiddleClickDeadZone);
            if (_hook != null) _hook.ShiftKeyHorizontal = snapshot.ShiftKeyHorizontal;
        };

        _tray = new TrayIcon(_settings);
        _tray.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.EnabledToggled += (_, enabled) =>
        {
            if (_vm is null) return;
            _vm.Enabled = enabled;
            _settings.Enabled = enabled;
            _settings.Save();
            UpdateHookState();
        };
        _tray.ToggleHotkeyRequested += (_, _) => ToggleEnabled();

        _engine = new SmoothScrollEngine(_settings);
        _zoomEngine = new ZoomSmoothEngine();
        _middleClickEngine = new MiddleClickScrollEngine();

        _middleClickEngine.Activated += (x, y) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _middleClickOverlay ??= new MiddleClickOverlay();
                _middleClickOverlay.ShowAt(x, y);
            });
        };
        _middleClickEngine.Deactivated += () =>
        {
            Dispatcher.InvokeAsync(() => _middleClickOverlay?.HideOverlay());
        };
        _middleClickEngine.DirectionChanged += (nx, ny, magnitude) =>
        {
            _middleClickOverlay?.UpdateDirection(nx, ny, magnitude);
        };

        _hook = new GlobalMouseHook();
        _hook.ShiftKeyHorizontal = _settings.ShiftKeyHorizontal;

        // Initialize touchpad detection
        _inputDetector = new InputDeviceDetector();

        // Subscribe to device type changes to update UI
        _inputDetector.DeviceTypeChanged += (_, isTouchpad) =>
        {
            Dispatcher.InvokeAsync(() => {
                UpdateDeviceStateUI(isTouchpad, _inputDetector.TouchpadCount, _inputDetector.MouseCount);
                _tray?.UpdateTouchpadState(isTouchpad);
            });
        };

        // Initialize Raw Input listener for device tracking
        _rawInputListener = new RawInputListener(_inputDetector);

        // Subscribe to devices changed event for re-enumeration
        _rawInputListener.DevicesChanged += (_, _) =>
        {
            Dispatcher.InvokeAsync(() => UpdateDeviceStateUI(_inputDetector.ShouldDisableSmoothScroll(), _inputDetector.TouchpadCount, _inputDetector.MouseCount));
        };

        // Setup hotkey for quick toggle (Ctrl+Alt+S)
        if (_settings.EnableGlobalHotkey)
        {
            _hotkey = new GlobalHotkey(
                id: 1,
                modifiers: HotkeyConstants.MOD_CONTROL | HotkeyConstants.MOD_ALT | HotkeyConstants.MOD_NOREPEAT,
                key: HotkeyConstants.VK_S
            );
            _hotkey.HotkeyPressed += (_, _) => ToggleEnabled();
            _hotkey.Install();
        }

        _hook.MouseWheel += (_, args) =>
        {
            if (WheelTrace.Enabled) WheelTrace.Log($"V    delta={args.Delta} ctrlNow={CtrlDownNow()} proc={CachedProcessHelper.GetProcessUnderCursor()} en={_settings.Enabled}");
            if (!_settings.Enabled) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Auto-disable on touchpad if setting is enabled
            if (_settings.AutoDisableOnTouchpad)
            {
                // First check device state (fallback)
                _inputDetector?.OnScrollEvent();
                if (_inputDetector?.ShouldDisableSmoothScroll() == true)
                    return;
            }

            // Show scroll indicator if enabled
            if (_settings.ShowScrollIndicator)
            {
                ShowScrollIndicator(args.Delta);
            }

            // Mode-lock: a regular scroll cancels any in-flight zoom glide so the zoom tail
            // doesn't keep zooming after the user switches back to scrolling.
            _zoomEngine!.Cancel();

            // Check for app-specific profile
            string? procName;
            lock (_exclusionLock) { procName = _lastExcludedProcess; }
            var profile = _settings.GetAppProfile(procName ?? "");
            if (profile != null && profile.Enabled)
            {
                // Swallow the native wheel event; otherwise the target app receives BOTH
                // the native scroll and our injected smooth pulses (double scroll).
                args.Handled = true;
                // Apply app profile settings temporarily
                _engine!.OnWheelWithSettings(args.Delta, profile.ToAppSettings(_settings));
                if (_settings.CollectStatistics) ScrollStatistics.Instance.RecordScroll(args.Delta);
            }
            else
            {
                args.Handled = true;
                _engine!.OnWheel(args.Delta);
                if (_settings.CollectStatistics) ScrollStatistics.Instance.RecordScroll(args.Delta);
            }
        };
        _hook.MouseHWheel += (_, args) =>
        {
            if (WheelTrace.Enabled) WheelTrace.Log($"H    delta={args.Delta} src={args.Source} ctrlNow={CtrlDownNow()} smoothing={_settings.HorizontalSmoothness} proc={CachedProcessHelper.GetProcessUnderCursor()} en={_settings.Enabled}");
            if (!_settings.Enabled) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Auto-disable on touchpad if setting is enabled
            if (_settings.AutoDisableOnTouchpad)
            {
                // First check device state (fallback)
                _inputDetector?.OnScrollEvent();
                if (_inputDetector?.ShouldDisableSmoothScroll() == true)
                    return;
            }

            // Resolve the per-app profile once — it drives both the Ctrl+horizontal=zoom toggle
            // and horizontal scroll sensitivity/momentum (same lookup as the vertical path).
            string? hProcName;
            lock (_exclusionLock) { hProcName = _lastExcludedProcess; }
            var hProfile = _settings.GetAppProfile(hProcName ?? "");
            bool hProfiled = hProfile != null && hProfile.Enabled;

            // Ctrl + horizontal wheel = ZOOM (opt-in; a per-app profile overrides the global flag).
            // Route to the zoom engine so one notch is a bounded 1:1 zoom — NOT the horizontal
            // scroll smoother, which fans one notch into a Ctrl+HWHEEL pulse train the app
            // over-zooms on. Negate the delta so thumb-wheel direction matches the zoom direction.
            bool ctrlZoom = hProfiled ? (hProfile!.CtrlHorizontalZoom ?? _settings.CtrlHorizontalZoom) : _settings.CtrlHorizontalZoom;
            if (ctrlZoom && CtrlDownNow())
            {
                _engine!.CancelAll();                 // mode-lock: stop any in-flight scroll
                if (!_settings.ZoomSmoothing) return; // native Ctrl+HWHEEL flows; we just stopped our scroll
                args.Handled = true;
                _zoomEngine!.OnZoom(-args.Delta);
                return;
            }

            // Horizontal wheel = VERTICAL scroll (opt-in; a per-app profile overrides the global
            // flag). Routes a native thumb wheel through the vertical smoother instead of X-Mouse.
            // Only native horizontal is remapped — Shift+wheel-as-horizontal explicitly wants
            // horizontal, so it's left alone and falls through to the normal horizontal path.
            // Holding Shift is an "other-axis" override: while it's down, even a native horizontal
            // wheel skips the remap and scrolls horizontally as usual, so the user keeps a way to
            // scroll sideways with the mapping on.
            bool mapHToV = hProfiled ? (hProfile!.HorizontalToVertical ?? _settings.HorizontalToVertical) : _settings.HorizontalToVertical;
            if (mapHToV && args.Source == WheelSource.NativeHorizontal && !ShiftDownNow())
            {
                if (CtrlDownNow())
                {
                    // The thumb wheel now acts as a vertical wheel, so Ctrl+thumb = zoom — exactly
                    // like Ctrl+main-wheel. Route to the bounded zoom engine, NOT the vertical
                    // smoother: fanning one notch into a Ctrl+wheel pulse train is what the app
                    // over-zooms on (the bug fixed for Ctrl+horizontal). Same sign as a vertical
                    // Ctrl+wheel (no negation), so it zooms like the main wheel does.
                    _engine!.CancelAll();                 // mode-lock: stop any in-flight scroll
                    if (!_settings.ZoomSmoothing) return; // native Ctrl+wheel zoom flows; scroll stopped
                    args.Handled = true;
                    _zoomEngine!.OnZoom(args.Delta);
                    return;
                }
                _zoomEngine!.Cancel();   // mode-lock: scrolling cancels any in-flight zoom glide
                args.Handled = true;
                _engine!.OnHWheelAsVertical(args.Delta, hProfiled ? hProfile!.ToAppSettings(_settings) : _settings);
                if (_settings.CollectStatistics) ScrollStatistics.Instance.RecordScroll(args.Delta);
                return;
            }

            // Mode-lock: a horizontal scroll cancels any in-flight zoom glide.
            _zoomEngine!.Cancel();

            // Horizontal smoothing off + a native horizontal wheel: leave the event fully
            // untouched (don't swallow, don't re-emit) so it flows to the app / Logi / X-Mouse
            // natively. But still axis-lock — cancel any in-flight vertical scroll/momentum so
            // it doesn't keep gliding alongside the horizontal scroll (the staircase). No
            // swallow, no injection. Shift+wheel-as-horizontal still goes through the engine
            // below — it must convert the vertical wheel — emitted raw (unsmoothed) by worker.
            if (!_settings.HorizontalSmoothness && args.Source == WheelSource.NativeHorizontal)
            {
                _engine!.CancelVertical();
                return;
            }

            args.Handled = true;
            // True horizontal scroll — the single orientation point for ALL horizontal output.
            // Negate so the direction matches expectation, applied uniformly to BOTH sources that
            // reach here: the physical horizontal wheel (NativeHorizontal — case A: mapping off;
            // case C: Shift override while mapping is on) AND Shift+vertical-as-horizontal (D).
            // The H→V mapping's VERTICAL direction (OnHWheelAsVertical) and plain vertical scroll
            // are separate paths and untouched. The global "reverse horizontal" switch flips this
            // baseline once more (it also flips the H→V vertical — see OnHWheelAsVertical).
            int hDelta = -args.Delta;
            if (hProfiled)
                _engine!.OnHWheelWithSettings(hDelta, hProfile!.ToAppSettings(_settings));
            else
                _engine!.OnHWheel(hDelta);
            if (_settings.CollectStatistics) ScrollStatistics.Instance.RecordScroll(args.Delta);
        };
        _hook.MouseZoomWheel += (_, args) =>
        {
            if (WheelTrace.Enabled) WheelTrace.Log($"ZOOM delta={args.Delta} recovered={args.CtrlRecovered} zoomSmoothing={_settings.ZoomSmoothing} proc={CachedProcessHelper.GetProcessUnderCursor()} en={_settings.Enabled}");
            if (!_settings.Enabled) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Mode-lock: a zoom gesture cancels any in-flight regular scroll so its tail can't
            // keep gliding and — with Ctrl now held — get reinterpreted as zoom. Done even when
            // zoom smoothing is off, so a leftover scroll glide still stops the moment Ctrl+wheel
            // starts a (native) zoom.
            _engine!.CancelAll();

            if (!_settings.ZoomSmoothing) return; // native Ctrl+wheel zoom flows; we just stopped our scroll

            args.Handled = true;
            _zoomEngine!.OnZoom(args.Delta, args.CtrlRecovered);
        };
        // Mode-lock on the modifier edge: when Ctrl goes down the user is switching into a Ctrl+wheel
        // zoom, so clear any in-flight smooth scroll NOW. Otherwise its tail keeps emitting wheel
        // pulses between the Ctrl press and the first zoom notch — and with Ctrl held the app reads
        // them as zoom (a leftover scroll, esp. horizontal, bleeding into the zoom). Complements the
        // per-notch CancelAll/Cancel, which only fires when the next wheel event actually lands.
        _hook.CtrlPressed += () => _engine?.CancelAll();
        _hook.MiddleButtonDown += (_, args) =>
        {
            if (!_settings.Enabled || !_settings.MiddleClickScroll) return;
            _middleClickEngine!.OnMiddleDown(args.X, args.Y);
        };
        _hook.MiddleButtonUp += (_, _) =>
        {
            if (!_settings.MiddleClickScroll) return;
            _middleClickEngine!.OnMiddleUp();
        };
        _hook.MouseMoved += (_, args) =>
        {
            _middleClickEngine?.OnMouseMove(args.X, args.Y);
        };

        UpdateHookState();

        bool shouldStartMinimized = _settings.StartWithWindows && _settings.StartMinimized;

        if (!shouldStartMinimized)
        {
            ShowSettingsWindow(show: true, startMinimized: false);
        }
        else
        {
            // Show tray icon but don't show the settings window
            ShowSettingsWindow(show: false, startMinimized: true);
            Log.Information("Starting minimized to system tray");
        }

        // Ensure Raw Input listener initializes on the UI thread after window creation
        if (_settings.AutoDisableOnTouchpad)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_settingsWindow != null)
                    _rawInputListener?.Initialize(_settingsWindow);
            });
        }

        // Enumerate devices after Raw Input is registered
        Dispatcher.InvokeAsync(() => _inputDetector?.EnumerateDevices());

        Current.MainWindow = _settingsWindow;
    }

    private void ToggleEnabled()
    {
        var newState = !_settings.Enabled;
        _settings.Enabled = newState;
        _settings.Save();
        _vm!.Enabled = newState;
        _tray?.UpdateEnabled(newState);
        UpdateHookState();
        Log.Information("Soft Scroll {State}", newState ? "enabled" : "disabled");
    }

    private bool _isOwnWindow;
    private IntPtr _lastForegroundWindow;
    private long _lastOwnWindowCheck;
    private const long OWN_WINDOW_CHECK_MS = 50;
    private readonly int _ownProcessId = Environment.ProcessId;

    private bool IsOwnWindow()
    {
        var now = Environment.TickCount64;
        if (now - _lastOwnWindowCheck <= OWN_WINDOW_CHECK_MS)
            return _isOwnWindow;
        _lastOwnWindowCheck = now;

        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == _lastForegroundWindow)
            return _isOwnWindow;

        _lastForegroundWindow = hwnd;
        if (hwnd == IntPtr.Zero)
        {
            _isOwnWindow = false;
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        _isOwnWindow = pid == (uint)_ownProcessId;
        return _isOwnWindow;
    }

    // Diagnostic: live Ctrl state (for the wheel-routing trace).
    private static bool CtrlDownNow() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
    // Live Shift state — lets a held Shift override the H→V remap so the horizontal wheel still
    // scrolls horizontally.
    private static bool ShiftDownNow() => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

    private bool IsExcludedApp()
    {
        var now = Environment.TickCount64;
        if (now - _lastExcludedCheck > EXCLUSION_CHECK_MS)
        {
            _lastExcludedProcess = CachedProcessHelper.GetProcessUnderCursor();
            _lastExcludedCheck = now;
        }
        return _settings.IsExcluded(_lastExcludedProcess);
    }

    private void ShowScrollIndicator(int speed)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _scrollIndicator ??= new ScrollIndicator();
            var pos = GetCursorPosition();
            _scrollIndicator.UpdateSpeed(speed);
            if (!_scrollIndicator.IsVisible)
            {
                _scrollIndicator.ShowAt(pos.X, pos.Y, speed);
            }
        });
    }

    private System.Drawing.Point GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var point);
        return new System.Drawing.Point(point.x, point.y);
    }

    private void UpdateHookState()
    {
        if (_hook is null || _vm is null || _engine is null) return;
        if (_vm.Enabled)
        {
            var snapshot = _vm.Snapshot();
            _engine.ApplySettings(snapshot);
            _hook.ShiftKeyHorizontal = snapshot.ShiftKeyHorizontal;
            _engine.Start();
            _zoomEngine?.Start();
            _middleClickEngine?.Start();
            _hook.Install();
        }
        else
        {
            _hook.Uninstall();
            _engine.Stop();
            _zoomEngine?.Stop();
            _middleClickEngine?.Stop();
            _middleClickEngine?.OnMiddleUp();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Backstop: persist any live-applied settings not yet written to disk (e.g. the window was
        // closed via Exit, or settings changed without clicking Save).
        _vm?.Snapshot().Save();
        base.OnExit(e);
        _hook?.Dispose();
        _hotkey?.Dispose();
        _engine?.Dispose();
        _zoomEngine?.Dispose();
        _middleClickEngine?.Dispose();
        _tray?.Dispose();
        _inputDetector?.Dispose();
        _rawInputListener?.Dispose();
        LoggingConfig.Shutdown();
        NativeMethods.timeEndPeriod(1);
    }

    private void ShowSettingsWindow(bool show = true, bool startMinimized = false)
    {
        if (_vm is null) return;

        // Don't show the window at all when minimized
        if (!show) return;

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_vm);
            _settingsWindow.Closed += (_, _) =>
            {
                // Persist on close. Edits apply live (SettingsChanged → engine), but were only
                // written to disk by the explicit "Save" button — so closing the window without
                // clicking Save reverted changes (e.g. horizontal step) on the next launch.
                _vm?.Snapshot().Save();
                _settingsWindow = null;
            };
            _settingsWindow.Owner = null;

            // Apply minimized state before showing if needed
            if (startMinimized)
            {
                _settingsWindow.WindowState = WindowState.Minimized;
                _settingsWindow.Show();
                // Ensure it doesn't flash on taskbar
                _settingsWindow.ShowInTaskbar = false;
            }
            else
            {
                _settingsWindow.Show();
            }
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.ShowInTaskbar = true;
            _settingsWindow.Activate();
        }
    }

    /// <summary>
    /// Called to update the UI when device state changes
    /// </summary>
    internal void UpdateDeviceStateUI(bool isTouchpad, int touchpadCount, int mouseCount)
    {
        DeviceStateChanged?.Invoke(this, new DeviceStateEventArgs(isTouchpad, touchpadCount, mouseCount));
    }
}

public class DeviceStateEventArgs : EventArgs
{
    public bool IsTouchpadActive { get; }
    public int TouchpadCount { get; }
    public int MouseCount { get; }

    public DeviceStateEventArgs(bool isTouchpadActive, int touchpadCount, int mouseCount)
    {
        IsTouchpadActive = isTouchpadActive;
        TouchpadCount = touchpadCount;
        MouseCount = mouseCount;
    }
}

