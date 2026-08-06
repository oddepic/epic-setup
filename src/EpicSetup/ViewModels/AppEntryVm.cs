using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EpicSetup.Models;
using EpicSetup.Services;

namespace EpicSetup.ViewModels;

public partial class AppEntryVm : ObservableObject
{
    public AppEntry Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string? Description => Model.Description;
    public bool NeedsReview => Model.NeedsReview;
    public bool HasWarning => Model.NeedsReview;
    public bool NeedsUserSetup => Model.NeedsUserSetup;
    public bool HasManualSetup => Model.NeedsUserSetup;

    // Estimated full app size for the hover tooltip. Returns null when no size
    // is set, which means NO tooltip pops up - so the blank-popup bug can't recur.
    public string? SizeLabel => Model.Size is int s ? $"~{s} MB" : null;

    public string IconInitial
    {
        get
        {
            foreach (var c in Name ?? "")
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }
    }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private AppStatus _status = AppStatus.Pending;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private ImageSource? _icon;

    public bool HasIcon => Icon != null;

    public bool IsProcessing => Status is AppStatus.Downloading or AppStatus.Verifying or AppStatus.Installing;
    public bool IsDone => Status is AppStatus.Succeeded or AppStatus.Failed or AppStatus.Skipped;
    public bool CanRemove => !IsProcessing && !IsDone;

    private readonly IconService _icons;
    public AppEntryVm(AppEntry model, IconService icons)
    {
        Model = model;
        _icons = icons;
        _icons.OnIconLoaded += OnIconLoaded;
        Icon = _icons.LoadIcon(Model);
    }

    private void OnIconLoaded(string id, ImageSource bmp)
    {
        if (id != Model.Id || HasIcon) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Icon = bmp;
            OnPropertyChanged(nameof(HasIcon));
        });
    }

    partial void OnStatusChanged(AppStatus value)
    {
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CanRemove));
    }

    public void SetStatus(AppStatus status, string? text)
    {
        Status = status;
        StatusText = text;
    }
}
