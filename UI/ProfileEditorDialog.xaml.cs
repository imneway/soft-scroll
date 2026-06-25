using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoftScroll.Infrastructure;
using SoftScroll.Settings;

namespace SoftScroll.UI;

public partial class ProfileEditorDialog : Window
{
    // The edited profile, valid only when DialogResult == true.
    public AppProfile Result { get; private set; }

    // Snapshot of the profile when the dialog opened — preserves identity (app/process name)
    // and provides parse fallbacks for blank/invalid fields.
    private readonly AppProfile _original;

    // Current global settings — "Reset" re-bases the form on these.
    private readonly AppSettings _globalDefaults;

    public ProfileEditorDialog(AppProfile profile, AppSettings globalDefaults)
    {
        InitializeComponent();
        _original = Clone(profile);
        _globalDefaults = globalDefaults;
        Result = Clone(profile);

        CmbEasing.ItemsSource = Enum.GetValues(typeof(EasingMode));

        ApplyTheme();
        ApplyLocalization();
        Populate(profile);
    }

    private void ApplyLocalization()
    {
        var L = new Func<string, string>(LocalizationManager.Get);
        Title = L("EditProfile");
        TxtHeader.Text = string.Format("{0} — {1}", L("EditProfile"),
            string.IsNullOrWhiteSpace(_original.AppName) ? _original.ProcessName : _original.AppName);
        TxtLblName.Text = L("ProfileName");
        ChkEnabled.Content = L("EnableThisProfile");
        TxtLblStep.Text = L("StepSize");
        TxtLblAnim.Text = L("AnimationTime");
        TxtLblAccelDelta.Text = L("AccelerationDelta");
        TxtLblAccelMax.Text = L("AccelerationMax");
        TxtLblTail.Text = L("TailToHeadRatio");
        TxtLblEasing.Text = L("EasingCurve");
        ChkEasing.Content = L("AnimationEasing");
        ChkCtrlHorizontalZoom.Content = L("CtrlHorizontalZoom");
        ChkHorizontalToVertical.Content = L("HorizontalToVertical");
        ChkReverseHoriz.Content = L("ReverseHorizontal");
        TxtLblHStep.Text = L("HorizontalStepSize");
        TxtLblHAccel.Text = L("HorizontalAccelMax");
        ChkMomentum.Content = L("EnableMomentum");
        TxtLblFriction.Text = L("Friction");
        TxtLblFlick.Text = L("FlickThreshold");
        BtnReset.Content = L("ResetTitle");
        BtnCancel.Content = L("Cancel");
        BtnSave.Content = L("Save");
    }

    private void Populate(AppProfile p)
    {
        TxtName.Text = p.AppName;
        ChkEnabled.IsChecked = p.Enabled;
        TxtStep.Text = p.StepSizePx.ToString();
        TxtAnim.Text = p.AnimationTimeMs.ToString();
        TxtAccelDelta.Text = p.AccelerationDeltaMs.ToString();
        TxtAccelMax.Text = p.AccelerationMax.ToString();
        TxtTail.Text = p.TailToHeadRatio.ToString();
        CmbEasing.SelectedItem = p.EasingMode;
        ChkEasing.IsChecked = p.AnimationEasing;
        ChkCtrlHorizontalZoom.IsChecked = p.CtrlHorizontalZoom;
        ChkHorizontalToVertical.IsChecked = p.HorizontalToVertical;
        ChkReverseHoriz.IsChecked = p.ReverseHorizontalDirection;
        TxtHStep.Text = p.HorizontalStepSizePx.ToString();
        TxtHAccel.Text = p.HorizontalAccelerationMax.ToString();
        ChkMomentum.IsChecked = p.MomentumEnabled;
        SldFriction.Value = p.MomentumFriction;
        TxtFrictionVal.Text = p.MomentumFriction.ToString();
        SldFlick.Value = p.MomentumFlickThreshold;
        TxtFlickVal.Text = p.MomentumFlickThreshold.ToString();
    }

    private AppProfile ReadInto()
    {
        return new AppProfile
        {
            // ProcessName (the match key) stays fixed; the display name is user-editable (rename).
            ProcessName = _original.ProcessName,
            AppName = string.IsNullOrWhiteSpace(TxtName.Text) ? _original.AppName : TxtName.Text.Trim(),
            Enabled = ChkEnabled.IsChecked == true,
            StepSizePx = ParseClamp(TxtStep, 10, 500, _original.StepSizePx),
            AnimationTimeMs = ParseClamp(TxtAnim, 10, 2000, _original.AnimationTimeMs),
            AccelerationDeltaMs = ParseClamp(TxtAccelDelta, 0, 500, _original.AccelerationDeltaMs),
            AccelerationMax = ParseClamp(TxtAccelMax, 1, 20, _original.AccelerationMax),
            TailToHeadRatio = ParseClamp(TxtTail, 1, 20, _original.TailToHeadRatio),
            EasingMode = CmbEasing.SelectedItem is EasingMode em ? em : _original.EasingMode,
            AnimationEasing = ChkEasing.IsChecked == true,
            HorizontalStepSizePx = ParseClamp(TxtHStep, 1, 500, _original.HorizontalStepSizePx),
            HorizontalAccelerationMax = ParseClamp(TxtHAccel, 1, 20, _original.HorizontalAccelerationMax),
            CtrlHorizontalZoom = ChkCtrlHorizontalZoom.IsChecked == true,
            HorizontalToVertical = ChkHorizontalToVertical.IsChecked == true,
            ReverseHorizontalDirection = ChkReverseHoriz.IsChecked == true,
            MomentumEnabled = ChkMomentum.IsChecked == true,
            MomentumFriction = (int)Math.Round(SldFriction.Value),
            MomentumFlickThreshold = (int)Math.Round(SldFlick.Value)
        };
    }

    private static int ParseClamp(System.Windows.Controls.TextBox tb, int min, int max, int fallback)
    {
        if (int.TryParse(tb.Text, out var v))
            return Math.Clamp(v, min, max);
        return fallback;
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
        Result = ReadInto();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Reset re-bases the form on the current global settings — a quick way to start a profile
    // from the global baseline and tweak from there. Identity (app/process name) is preserved.
    private void OnResetClick(object sender, RoutedEventArgs e)
        => Populate(AppProfile.FromAppSettings(_original.AppName, _original.ProcessName, _globalDefaults));

    private void OnFrictionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // TxtFrictionVal can be null while XAML is still loading (slider's default value fires
        // this before the label is created).
        if (TxtFrictionVal != null)
            TxtFrictionVal.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void OnFlickChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFlickVal != null)
            TxtFlickVal.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

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
}
