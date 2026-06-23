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

    // Snapshot of the profile as it was when the dialog opened — used by "Reset".
    private readonly AppProfile _original;

    public ProfileEditorDialog(AppProfile profile)
    {
        InitializeComponent();
        _original = Clone(profile);
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
        ChkEnabled.Content = L("EnableThisProfile");
        TxtLblStep.Text = L("StepSize");
        TxtLblAnim.Text = L("AnimationTime");
        TxtLblAccelDelta.Text = L("AccelerationDelta");
        TxtLblAccelMax.Text = L("AccelerationMax");
        TxtLblTail.Text = L("TailToHeadRatio");
        TxtLblEasing.Text = L("EasingCurve");
        ChkEasing.Content = L("AnimationEasing");
        TxtLblHStep.Text = L("HorizontalStepSize");
        TxtLblHAccel.Text = L("HorizontalAccelMax");
        ChkMomentum.Content = L("EnableMomentum");
        TxtLblFriction.Text = L("Friction");
        BtnReset.Content = L("ResetTitle");
        BtnCancel.Content = L("Cancel");
        BtnSave.Content = L("Save");
    }

    private void Populate(AppProfile p)
    {
        ChkEnabled.IsChecked = p.Enabled;
        TxtStep.Text = p.StepSizePx.ToString();
        TxtAnim.Text = p.AnimationTimeMs.ToString();
        TxtAccelDelta.Text = p.AccelerationDeltaMs.ToString();
        TxtAccelMax.Text = p.AccelerationMax.ToString();
        TxtTail.Text = p.TailToHeadRatio.ToString();
        CmbEasing.SelectedItem = p.EasingMode;
        ChkEasing.IsChecked = p.AnimationEasing;
        TxtHStep.Text = p.HorizontalStepSizePx.ToString();
        TxtHAccel.Text = p.HorizontalAccelerationMax.ToString();
        ChkMomentum.IsChecked = p.MomentumEnabled;
        TxtFriction.Text = p.MomentumFriction.ToString();
    }

    private AppProfile ReadInto()
    {
        return new AppProfile
        {
            // Identity is not editable here — preserve it.
            AppName = _original.AppName,
            ProcessName = _original.ProcessName,
            Enabled = ChkEnabled.IsChecked == true,
            StepSizePx = ParseClamp(TxtStep, 10, 500, _original.StepSizePx),
            AnimationTimeMs = ParseClamp(TxtAnim, 10, 2000, _original.AnimationTimeMs),
            AccelerationDeltaMs = ParseClamp(TxtAccelDelta, 0, 500, _original.AccelerationDeltaMs),
            AccelerationMax = ParseClamp(TxtAccelMax, 1, 20, _original.AccelerationMax),
            TailToHeadRatio = ParseClamp(TxtTail, 1, 20, _original.TailToHeadRatio),
            EasingMode = CmbEasing.SelectedItem is EasingMode em ? em : _original.EasingMode,
            AnimationEasing = ChkEasing.IsChecked == true,
            HorizontalStepSizePx = ParseClamp(TxtHStep, 10, 500, _original.HorizontalStepSizePx),
            HorizontalAccelerationMax = ParseClamp(TxtHAccel, 1, 20, _original.HorizontalAccelerationMax),
            MomentumEnabled = ChkMomentum.IsChecked == true,
            MomentumFriction = ParseClamp(TxtFriction, 0, 100, _original.MomentumFriction)
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
        MomentumEnabled = p.MomentumEnabled,
        MomentumFriction = p.MomentumFriction
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

    // Reset reverts the form to the profile's state when the dialog was opened (without closing).
    private void OnResetClick(object sender, RoutedEventArgs e) => Populate(_original);

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
