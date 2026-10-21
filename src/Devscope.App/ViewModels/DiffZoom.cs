using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;

namespace Devscope.App.ViewModels;

/// <summary>
/// Zoom of the diff text, shared by the preview and every tab of the diff window and remembered across restarts.
/// Changed with Ctrl+mouse wheel, Ctrl+plus / Ctrl+minus, and reset with Ctrl+0.
/// </summary>
public sealed partial class DiffZoom : ObservableObject
{
    public const double Min = 0.5;

    public const double Max = 4;

    private const double Step = 0.1;

    private readonly IUserSettingsStore _settings;

    public DiffZoom(IUserSettingsStore settings)
    {
        _settings = settings;
        _scale = Clamp(settings.Current.DiffZoom);
    }

    /// <summary>1 is 100%.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(IsZoomed))]
    private double _scale;

    public string Percent => $"{Math.Round(Scale * 100):0}%";

    public bool IsZoomed => Math.Abs(Scale - 1) > 0.001;

    partial void OnScaleChanged(double value)
    {
        // Save, not Update: nothing else needs to react to a zoom change.
        _settings.Current.DiffZoom = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ZoomIn() => Scale = Clamp(Scale + Step);

    [RelayCommand]
    private void ZoomOut() => Scale = Clamp(Scale - Step);

    [RelayCommand]
    private void ResetZoom() => Scale = 1;

    // Rounded to whole steps, so repeated wheel turns land back on 100%.
    private static double Clamp(double value) =>
        Math.Clamp(Math.Round(double.IsFinite(value) ? value : 1, 1), Min, Max);
}
