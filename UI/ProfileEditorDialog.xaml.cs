using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoftScroll.Infrastructure;
using SoftScroll.Settings;
// Disambiguate from the WinForms types pulled in by implicit usings (the app uses WinForms for the tray icon).
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace SoftScroll.UI;

public partial class ProfileEditorDialog : Window
{
    // The edited profile, valid only when DialogResult == true.
    public AppProfile Result { get; private set; }

    // Snapshot of the profile when the dialog opened — preserves identity (app/process name)
    // and provides parse fallbacks for blank/invalid fields.
    private readonly AppProfile _original;

    // Current global settings — every "follow" (null) field resolves to these, and the bracket /
    // "默认" indicators are computed against them.
    private readonly AppSettings _globalDefaults;

    // One wrapper per numeric input; tracks its int? (null = follow) and the focus-swap rendering.
    private FollowNum _numStep = null!, _numAnim = null!, _numAccelDelta = null!, _numAccelMax = null!,
                      _numTail = null!, _numHStep = null!, _numHAccel = null!;
    private int _gFriction, _gFlick;          // global values the two sliders compare against
    private Brush _normal = Brushes.White, _muted = Brushes.Gray;
    private object _easingFollow = null!;   // "默认(<global curve>)" sentinel in the easing dropdown, built in SetupControls

    public ProfileEditorDialog(AppProfile profile, AppSettings globalDefaults)
    {
        InitializeComponent();
        _original = Clone(profile);
        _globalDefaults = globalDefaults;
        Result = Clone(profile);

        ApplyTheme();
        ApplyLocalization();
        SetupControls();
        Populate(profile);
    }

    private void ApplyLocalization()
    {
        var L = new Func<string, string>(LocalizationManager.Get);
        Title = L("EditProfile");
        TxtHeader.Text = string.Format("{0} — {1}", L("EditProfile"),
            string.IsNullOrWhiteSpace(_original.AppName) ? _original.ProcessName : _original.AppName);
        TxtFollowHint.Text = L("FollowDefaultHint");
        TxtLblName.Text = L("ProfileName");
        ChkEnabled.Content = L("EnableThisProfile");
        TxtLblStep.Text = L("StepSize");
        TxtLblAnim.Text = L("AnimationTime");
        TxtLblAccelDelta.Text = L("AccelerationDelta");
        TxtLblAccelMax.Text = L("AccelerationMax");
        TxtLblTail.Text = L("TailToHeadRatio");
        TxtLblEasing.Text = L("EasingCurve");
        TxtLblHStep.Text = L("HorizontalStepSize");
        TxtLblHAccel.Text = L("HorizontalAccelMax");
        TxtLblAnimEasing.Text = L("AnimationEasing");
        TxtLblCtrlZoom.Text = L("CtrlHorizontalZoom");
        TxtLblHToV.Text = L("HorizontalToVertical");
        TxtLblReverseHoriz.Text = L("ReverseHorizontal");
        TxtLblMomentum.Text = L("EnableMomentum");
        TxtLblFriction.Text = L("Friction");
        TxtLblFlick.Text = L("FlickThreshold");
        BtnReset.Content = L("ResetTitle");
        BtnCancel.Content = L("Cancel");
        BtnSave.Content = L("Save");

        // The "默认" segment shows the value it currently resolves to from the global settings.
        string on = L("SegOn"), off = L("SegOff");
        string Def(bool g) => $"{L("SegDefault")}({(g ? on : off)})";
        SegEasingDefault.Content = Def(_globalDefaults.AnimationEasing); SegEasingOn.Content = on; SegEasingOff.Content = off;
        SegCtrlZoomDefault.Content = Def(_globalDefaults.CtrlHorizontalZoom); SegCtrlZoomOn.Content = on; SegCtrlZoomOff.Content = off;
        SegHToVDefault.Content = Def(_globalDefaults.HorizontalToVertical); SegHToVOn.Content = on; SegHToVOff.Content = off;
        SegRevDefault.Content = Def(_globalDefaults.ReverseHorizontalDirection); SegRevOn.Content = on; SegRevOff.Content = off;
        SegMomDefault.Content = Def(_globalDefaults.MomentumEnabled); SegMomOn.Content = on; SegMomOff.Content = off;
    }

    private void SetupControls()
    {
        // Resolve the theme brushes captured by ApplyTheme into concrete objects for code-driven rendering.
        _normal = (Brush)Resources["TextBrush"];
        _muted = (Brush)Resources["TextSecondaryBrush"];

        var g = _globalDefaults;
        _numStep = new FollowNum(TxtStep, 10, 500, g.StepSizePx, _normal, _muted);
        _numAnim = new FollowNum(TxtAnim, 10, 2000, g.AnimationTimeMs, _normal, _muted);
        _numAccelDelta = new FollowNum(TxtAccelDelta, 0, 500, g.AccelerationDeltaMs, _normal, _muted);
        _numAccelMax = new FollowNum(TxtAccelMax, 1, 20, g.AccelerationMax, _normal, _muted);
        _numTail = new FollowNum(TxtTail, 1, 20, g.TailToHeadRatio, _normal, _muted);
        _numHStep = new FollowNum(TxtHStep, 1, 500, g.HorizontalStepSizePx, _normal, _muted);
        _numHAccel = new FollowNum(TxtHAccel, 1, 20, g.HorizontalAccelerationMax, _normal, _muted);
        _gFriction = g.MomentumFriction;
        _gFlick = g.MomentumFlickThreshold;

        // The easing dropdown gets a "默认(<global curve>)" follow sentinel ahead of the real options.
        _easingFollow = new EasingFollowItem(_globalDefaults.EasingMode);
        CmbEasing.ItemsSource = new List<object>
        {
            _easingFollow, EasingMode.ExponentialOut, EasingMode.CubicOut, EasingMode.QuinticOut, EasingMode.Linear
        };
    }

    private void Populate(AppProfile p)
    {
        TxtName.Text = p.AppName;
        ChkEnabled.IsChecked = p.Enabled;

        _numStep.Set(p.StepSizePx);
        _numAnim.Set(p.AnimationTimeMs);
        _numAccelDelta.Set(p.AccelerationDeltaMs);
        _numAccelMax.Set(p.AccelerationMax);
        _numTail.Set(p.TailToHeadRatio);
        _numHStep.Set(p.HorizontalStepSizePx);
        _numHAccel.Set(p.HorizontalAccelerationMax);

        CmbEasing.SelectedItem = p.EasingMode.HasValue ? (object)p.EasingMode.Value : _easingFollow;

        SetSegment(SegEasingDefault, SegEasingOn, SegEasingOff, p.AnimationEasing);
        SetSegment(SegCtrlZoomDefault, SegCtrlZoomOn, SegCtrlZoomOff, p.CtrlHorizontalZoom);
        SetSegment(SegHToVDefault, SegHToVOn, SegHToVOff, p.HorizontalToVertical);
        SetSegment(SegRevDefault, SegRevOn, SegRevOff, p.ReverseHorizontalDirection);
        SetSegment(SegMomDefault, SegMomOn, SegMomOff, p.MomentumEnabled);

        SetSlider(SldFriction, TxtFrictionVal, _gFriction, p.MomentumFriction);
        SetSlider(SldFlick, TxtFlickVal, _gFlick, p.MomentumFlickThreshold);

        UpdateDependents();
    }

    private AppProfile ReadInto()
    {
        return new AppProfile
        {
            // ProcessName (the match key) stays fixed; the display name is user-editable (rename).
            ProcessName = _original.ProcessName,
            AppName = string.IsNullOrWhiteSpace(TxtName.Text) ? _original.AppName : TxtName.Text.Trim(),
            Enabled = ChkEnabled.IsChecked == true,
            StepSizePx = _numStep.Value,
            AnimationTimeMs = _numAnim.Value,
            AccelerationDeltaMs = _numAccelDelta.Value,
            AccelerationMax = _numAccelMax.Value,
            TailToHeadRatio = _numTail.Value,
            HorizontalStepSizePx = _numHStep.Value,
            HorizontalAccelerationMax = _numHAccel.Value,
            EasingMode = CmbEasing.SelectedItem is EasingMode em ? em : (EasingMode?)null,
            AnimationEasing = ReadSegment(SegEasingOn, SegEasingOff),
            CtrlHorizontalZoom = ReadSegment(SegCtrlZoomOn, SegCtrlZoomOff),
            HorizontalToVertical = ReadSegment(SegHToVOn, SegHToVOff),
            ReverseHorizontalDirection = ReadSegment(SegRevOn, SegRevOff),
            MomentumEnabled = ReadSegment(SegMomOn, SegMomOff),
            MomentumFriction = ReadSliderValue(SldFriction, _gFriction),
            MomentumFlickThreshold = ReadSliderValue(SldFlick, _gFlick)
        };
    }

    // ── Segment (默认/开/关) helpers ────────────────────────────────────
    private static void SetSegment(RadioButton def, RadioButton on, RadioButton off, bool? v)
    {
        def.IsChecked = !v.HasValue;
        on.IsChecked = v == true;
        off.IsChecked = v == false;
    }

    private static bool? ReadSegment(RadioButton on, RadioButton off)
        => on.IsChecked == true ? true : off.IsChecked == true ? (bool?)false : null;

    // Resolved value of a tri-state segment: explicit on/off wins, otherwise the global value.
    private static bool EffectiveBool(RadioButton on, RadioButton off, bool global)
    {
        if (on.IsChecked == true) return true;
        if (off.IsChecked == true) return false;
        return global;
    }

    // Disable children whose parent toggle resolves to off, so an override there can't masquerade
    // as active (momentum gates friction/flick; animation-easing gates the easing curve).
    private void UpdateDependents()
    {
        CmbEasing.IsEnabled = EffectiveBool(SegEasingOn, SegEasingOff, _globalDefaults.AnimationEasing);
        bool mom = EffectiveBool(SegMomOn, SegMomOff, _globalDefaults.MomentumEnabled);
        SldFriction.IsEnabled = mom;
        SldFlick.IsEnabled = mom;
    }

    private void OnGatingChanged(object sender, RoutedEventArgs e) => UpdateDependents();

    // ── Slider helpers (bracket the label when it equals global = follow) ───
    private void SetSlider(Slider sld, TextBlock lbl, int global, int? v)
    {
        sld.Value = v ?? global;
        RenderSliderLabel(lbl, global, (int)Math.Round(sld.Value));
    }

    private void RenderSliderLabel(TextBlock lbl, int global, int v)
    {
        bool follow = v == global;
        lbl.Text = follow ? $"({v})" : v.ToString();
        lbl.Foreground = follow ? _muted : _normal;
    }

    private static int? ReadSliderValue(Slider sld, int global)
    {
        int v = (int)Math.Round(sld.Value);
        return v == global ? (int?)null : v;
    }

    private void OnFrictionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFrictionVal != null) RenderSliderLabel(TxtFrictionVal, _gFriction, (int)Math.Round(e.NewValue));
    }

    private void OnFlickChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFlickVal != null) RenderSliderLabel(TxtFlickVal, _gFlick, (int)Math.Round(e.NewValue));
    }

    private static AppProfile Clone(AppProfile p) => new()
    {
        AppName = p.AppName,
        ProcessName = p.ProcessName,
        Enabled = p.Enabled,
        StepSizePx = p.StepSizePx,
        AnimationTimeMs = p.AnimationTimeMs,
        AccelerationDeltaMs = p.AccelerationDeltaMs,
        AccelerationMax = p.AccelerationMax,
        TailToHeadRatio = p.TailToHeadRatio,
        EasingMode = p.EasingMode,
        AnimationEasing = p.AnimationEasing,
        HorizontalStepSizePx = p.HorizontalStepSizePx,
        HorizontalAccelerationMax = p.HorizontalAccelerationMax,
        CtrlHorizontalZoom = p.CtrlHorizontalZoom,
        HorizontalToVertical = p.HorizontalToVertical,
        ReverseHorizontalDirection = p.ReverseHorizontalDirection,
        MomentumEnabled = p.MomentumEnabled,
        MomentumFriction = p.MomentumFriction,
        MomentumFlickThreshold = p.MomentumFlickThreshold
    };

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Force-commit every numeric input first: a field the user edited but didn't blur (e.g. they
        // clicked Save directly) otherwise keeps its stale value.
        _numStep.Commit(); _numAnim.Commit(); _numAccelDelta.Commit(); _numAccelMax.Commit();
        _numTail.Commit(); _numHStep.Commit(); _numHAccel.Commit();

        Result = ReadInto();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Reset = clear ALL overrides → follow global for everything (every field back to "默认").
    private void OnResetClick(object sender, RoutedEventArgs e)
        => Populate(AppProfile.CreateFollowing(_original.AppName, _original.ProcessName));

    private static readonly Regex _nonDigit = new("[^0-9]+", RegexOptions.Compiled);

    private void NumericOnly(object sender, TextCompositionEventArgs e)
    {
        e.Handled = _nonDigit.IsMatch(e.Text);
    }

    private void OnPasteNumeric(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (_nonDigit.IsMatch(text))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void ApplyTheme()
    {
        bool isDark = ThemeHelper.IsDarkMode();
        if (isDark)
        {
            SetBrush("BackgroundBrush", ThemeHelper.Dark.Background);
            SetBrush("SurfaceBrush", ThemeHelper.Dark.Surface);
            SetBrush("SurfaceBorderBrush", ThemeHelper.Dark.SurfaceBorder);
            SetBrush("TextBrush", ThemeHelper.Dark.Text);
            SetBrush("TextSecondaryBrush", ThemeHelper.Dark.TextSecondary);
            SetBrush("AccentBrush", ThemeHelper.Dark.Accent);
            SetBrush("InputBrush", ThemeHelper.Dark.Input);
            SetBrush("InputBorderBrush", ThemeHelper.Dark.InputBorder);
            SetBrush("HoverBrush", "#333333");
        }
        else
        {
            SetBrush("BackgroundBrush", ThemeHelper.Light.Background);
            SetBrush("SurfaceBrush", ThemeHelper.Light.Surface);
            SetBrush("SurfaceBorderBrush", ThemeHelper.Light.SurfaceBorder);
            SetBrush("TextBrush", ThemeHelper.Light.Text);
            SetBrush("TextSecondaryBrush", ThemeHelper.Light.TextSecondary);
            SetBrush("AccentBrush", ThemeHelper.Light.Accent);
            SetBrush("InputBrush", ThemeHelper.Light.Input);
            SetBrush("InputBorderBrush", ThemeHelper.Light.InputBorder);
            SetBrush("HoverBrush", "#E8E8E8");
        }
    }

    private void SetBrush(string resourceKey, string colorHex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
        Resources[resourceKey] = new System.Windows.Media.SolidColorBrush(color);
    }

    // Wraps a numeric TextBox with follow/override behaviour: when blurred and equal to global (or
    // empty) it shows a muted "(global)" and stores null (follow); editing to a different value
    // stores an override. Focused, it always shows the raw editable number.
    private sealed class FollowNum
    {
        private readonly TextBox _tb;
        private readonly int _min, _max, _global;
        private readonly Brush _normal, _muted;
        public int? Value { get; private set; }

        public FollowNum(TextBox tb, int min, int max, int global, Brush normal, Brush muted)
        {
            _tb = tb; _min = min; _max = max; _global = global; _normal = normal; _muted = muted;
            _tb.GotFocus += (_, __) => ShowEditable();
            _tb.LostFocus += (_, __) => Commit();
        }

        // A value equal to global is shown (and stored) as follow — keeps the "equals default ⇒
        // bracketed" rule consistent and collapses migrated overrides that happen to match global.
        public void Set(int? v) { Value = (v.HasValue && v.Value == _global) ? null : v; Render(); }

        public void Commit()
        {
            if (int.TryParse(_tb.Text, out var v))
            {
                v = Math.Clamp(v, _min, _max);
                Value = v == _global ? (int?)null : v;   // equal to global ⇒ follow
            }
            else
            {
                Value = null;   // empty / unparseable ⇒ follow
            }
            Render();
        }

        private void ShowEditable()
        {
            _tb.Foreground = _normal;
            _tb.Text = (Value ?? _global).ToString();
            _tb.SelectAll();
        }

        private void Render()
        {
            if (_tb.IsKeyboardFocused)
            {
                _tb.Foreground = _normal;
                _tb.Text = (Value ?? _global).ToString();
                return;
            }
            if (Value == null) { _tb.Foreground = _muted; _tb.Text = $"({_global})"; }
            else { _tb.Foreground = _normal; _tb.Text = Value.Value.ToString(); }
        }
    }

    // "默认(<global curve>)" sentinel row for the easing dropdown (selecting it = follow global).
    private sealed class EasingFollowItem
    {
        private readonly EasingMode _global;
        public EasingFollowItem(EasingMode global) => _global = global;
        public override string ToString() => $"{LocalizationManager.Get("SegDefault")}({_global})";
    }
}
