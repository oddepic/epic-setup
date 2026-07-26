using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using EpicSetup.Models;
using EpicSetup.Services;

namespace EpicSetup.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly CatalogService _catalogService;
    private readonly IconService _iconService;
    private readonly InstallEngine _engine;
    private readonly List<AppEntryVm> _allApps = new();

    public ObservableCollection<TabVm> Tabs { get; } = new();
    public ObservableCollection<CategoryVm> Categories { get; } = new();
    public ObservableCollection<AppEntryVm> ReviewItems { get; } = new();

    public bool SelectionEnabled => !IsInstalling && !IsReviewing;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _statusText = "Loading catalog…";
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private string _catalogSourceText = "";

    public string ContinueLabel => $"Continue ({SelectedCount}/{TotalCount})";

    public MainViewModel() : this(new CatalogService(), new IconService(), new InstallEngine()) { }

    public MainViewModel(CatalogService catalogService, IconService iconService, InstallEngine engine)
    {
        _catalogService = catalogService;
        _iconService = iconService;
        _engine = engine;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "Loading catalog…";
        Tabs.Clear();
        Categories.Clear();
        _allApps.Clear();
        ReviewItems.Clear();
        IsReviewing = false;
        try
        {
            var (catalog, source) = await Task.Run(() => _catalogService.LoadSync());
            ApplyCatalog(catalog);
            CatalogSourceText = source switch
            {
                CatalogService.SourceKind.Remote => "catalog · online",
                CatalogService.SourceKind.Cache => "catalog · cached",
                _ => "catalog · built-in"
            };
            TotalCount = _allApps.Count;
            StatusText = $"{Tabs.Count} tabs · {TotalCount} apps available.";
        }
        catch (Exception ex)
        {
            StatusText = "Failed to load catalog: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync() => await LoadAsync();

    private void ApplyCatalog(Catalog catalog)
    {
        foreach (var tabDef in catalog.Tabs)
        {
            var categories = tabDef.Categories.Select(c =>
                new CategoryVm(c.Name, c.Hint, c.Apps.Select(CreateAppVm))).ToList();
            var tab = new TabVm(tabDef.Name, categories);
            tab.PropertyChanged += TabOnPropertyChanged;
            Tabs.Add(tab);
        }
        if (Tabs.Count > 0) Tabs[0].IsSelected = true;
    }

    private AppEntryVm CreateAppVm(AppEntry entry)
    {
        var vm = new AppEntryVm(entry, _iconService);
        vm.PropertyChanged += AppVmOnPropertyChanged;
        _allApps.Add(vm);
        return vm;
    }

    private void TabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabVm.IsSelected) && sender is TabVm t && t.IsSelected && !IsReviewing)
            ShowTab(t);
    }

    private void ShowTab(TabVm tab)
    {
        Categories.Clear();
        foreach (var c in tab.Categories) Categories.Add(c);
    }

    private void AppVmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntryVm.IsChecked))
        {
            SelectedCount = _allApps.Count(a => a.IsChecked);
            ContinueCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        ReviewItems.Clear();
        foreach (var a in _allApps.Where(a => a.IsChecked)) ReviewItems.Add(a);
        IsReviewing = true;
        OnPropertyChanged(nameof(SelectionEnabled));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private bool CanContinue() => SelectedCount > 0 && !IsReviewing && !IsInstalling;

    [RelayCommand]
    private void Back()
    {
        IsReviewing = false;
        OnPropertyChanged(nameof(SelectionEnabled));
        ContinueCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveApp(AppEntryVm? app)
    {
        if (app == null) return;
        ReviewItems.Remove(app);
        app.IsChecked = false;
        InstallCommand.NotifyCanExecuteChanged();
        if (ReviewItems.Count == 0)
        {
            IsReviewing = false;
            OnPropertyChanged(nameof(SelectionEnabled));
            ContinueCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var selected = ReviewItems.Select(r => r.Model).ToList();
        if (selected.Count == 0) return;

        IsInstalling = true;
        OverallProgress = 0;
        ProgressDetail = "Starting…";
        StatusText = $"Installing {selected.Count} apps…";

        var progress = new Progress<InstallUpdate>(OnProgress);
        try
        {
            await Task.Run(() => _engine.RunAsync(selected, progress, default));
            StatusText = $"Done: {_allApps.Count(a => a.Status == AppStatus.Succeeded)} installed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsInstalling = false;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnProgress(InstallUpdate u)
    {
        if (!string.IsNullOrEmpty(u.AppId))
        {
            var appVm = _allApps.FirstOrDefault(a => a.Id == u.AppId);
            if (appVm != null) appVm.SetStatus(u.Status, Label(u.Status, u.Message));
        }
        OverallProgress = u.Fraction;
        ProgressDetail = u.Message ?? "";
        if (u.AppId is null && u.Status == AppStatus.Succeeded)
            StatusText = u.Message ?? "Done.";
    }

    private static string Label(AppStatus s, string? msg) => s switch
    {
        AppStatus.Pending => "Pending",
        AppStatus.Downloading => "Downloading…",
        AppStatus.Verifying => "Verifying signature…",
        AppStatus.Installing => "Installing…",
        AppStatus.Succeeded => msg ?? "Installed",
        AppStatus.Failed => "Failed: " + msg,
        AppStatus.Skipped => msg ?? "Skipped",
        _ => s.ToString()
    };

    private bool CanInstall() => IsReviewing && ReviewItems.Count > 0 && !IsInstalling;

    partial void OnSelectedCountChanged(int value)
    {
        ContinueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ContinueLabel));
    }
    partial void OnTotalCountChanged(int value)
        => OnPropertyChanged(nameof(ContinueLabel));
    partial void OnIsReviewingChanged(bool value)
    {
        ContinueCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionEnabled));
    }
    partial void OnIsInstallingChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionEnabled));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var a in Categories.SelectMany(c => c.Apps)) a.IsChecked = true;
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var a in Categories.SelectMany(c => c.Apps)) a.IsChecked = false;
    }
}