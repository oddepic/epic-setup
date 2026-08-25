using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
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
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _detailsOpen;

    [RelayCommand]
    private void ToggleDetails() => DetailsOpen = !DetailsOpen;

    public bool SelectionEnabled => !IsInstalling && !IsReviewing;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _backendLogText = "";
    [ObservableProperty] private string _statusText = "Loading catalog…";
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private string _catalogSourceText = "";

    // Tooltip shown when hovering the "X installed" status text: lists the
    // names of all apps that installed successfully (one per line).
    public string InstalledAppsTooltip
    {
        get
        {
            var names = _allApps
                .Where(a => a.Status == AppStatus.Succeeded)
                .OrderBy(a => a.Name)
                .Select(a => a.Name)
                .ToList();
            return names.Count == 0 ? "" : string.Join("\n", names);
        }
    }

    private void NotifyInstalledAppsTooltip() => OnPropertyChanged(nameof(InstalledAppsTooltip));

    partial void OnDownloadProgressChanged(double value)
        => OnPropertyChanged(nameof(DownloadPercentText));

    public string DownloadPercentText => $"{(int)Math.Round(DownloadProgress * 100)}%";

    // Install stage header, Hermes-setup style.
    public string StageTitle => "Setting up your apps";

    public int DoneSteps => ReviewItems.Count(a => a.IsDone);
    public string StepsCounterLabel => $"{DoneSteps} of {ReviewItems.Count} steps";
    public string StepsPercentLabel => ReviewItems.Count == 0 ? "0%" : $"{(int)Math.Round(DoneSteps * 100.0 / ReviewItems.Count)}%";
    public double OverallStepsFraction => ReviewItems.Count == 0 ? 0 : (double)DoneSteps / ReviewItems.Count;

    private void NotifyStageHeader()
    {
        OnPropertyChanged(nameof(DoneSteps));
        OnPropertyChanged(nameof(StepsCounterLabel));
        OnPropertyChanged(nameof(StepsPercentLabel));
        OnPropertyChanged(nameof(OverallStepsFraction));
    }

    // Full log text for the details drawer.
    public string LogText => string.Join("\n", LogLines);

    public string ContinueLabel => $"Continue ({SelectedCount}/{TotalCount})";

    public MainViewModel() : this(new CatalogService(), new IconService(), new InstallEngine()) { }

    public MainViewModel(CatalogService catalogService, IconService iconService, InstallEngine engine)
    {
        _catalogService = catalogService;
        _iconService = iconService;
        _engine = engine;
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));
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
        BackendLogText = "";
        IsReviewing = false;
        try
        {
            var (catalog, source) = await Task.Run(() => _catalogService.LoadSync());
            ApplyCatalog(catalog);
            var srcText = source switch
            {
                CatalogService.SourceKind.Remote => "catalog · online",
                CatalogService.SourceKind.Cache => "catalog · cached",
                _ => "catalog · built-in"
            };
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var versionText = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "0";
            if (versionText.IndexOf('+') >= 0) versionText = versionText[..versionText.IndexOf('+')];
            CatalogSourceText = $"{srcText} · v{versionText}";
            TotalCount = _allApps.Count;
            StatusText = $"{Tabs.Count} tabs · {TotalCount} apps available.";
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var exePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "<unknown>";
            AppendBackendLog($"process: pid={process.Id} exe={exePath}");
            AppendBackendLog($"catalog: loaded {TotalCount} entries from {srcText}; updatedAt={catalog.UpdatedAt ?? "unknown"}");
        }
        catch (Exception ex)
        {
            StatusText = "Failed to load catalog: " + ex.Message;
            AppendBackendLog($"catalog: load failed: {ex.GetType().Name}: {ex.Message}");
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
        BackendLogText = "";
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
            // DetailsOpen is deliberately left untouched so the user can keep
            // reading the log after the run ends.
            IsInstalling = false;
            InstallCommand.NotifyCanExecuteChanged();
            NotifyInstalledAppsTooltip();
        }
    }

#if DEBUG
    // Debug-only rehearsal of the install stage: drives the same status
    // pipeline as InstallAsync without touching the real engine, so the
    // step-list UI can be exercised safely (bound to F12 in the window).
    [RelayCommand]
    private async Task FakeInstallAsync()
    {
        var queue = ReviewItems.ToList();
        if (queue.Count == 0) return;

        IsInstalling = true;
        OverallProgress = 0;
        StatusText = "[fake] installing";
        var rand = new Random();

        foreach (var vm in queue)
        {
            AppendLog($"[fake] enter {vm.Id}");
            foreach (var f in new[] { 0.15, 0.4, 0.65, 0.9 })
            {
                vm.SetStatus(AppStatus.Downloading, $"Downloading {f:P0} [fake]");
                DownloadProgress = f;
                await Task.Delay(rand.Next(450, 750));
            }
            vm.SetStatus(AppStatus.Installing, "Installing [fake]");
            await Task.Delay(rand.Next(600, 900));

            if (rand.Next(10) == 0)
                vm.SetStatus(AppStatus.Failed, "Installer exited 1622: [fake failure]");
            else
                vm.SetStatus(AppStatus.Succeeded, "Installed [fake]");

            AppendLog($"[fake] exit {vm.Id} -> {vm.Status}");
            DownloadProgress = 0;
            NotifyStageHeader();

            // Hold mid-stage so the UI can be inspected/screenshotted safely.
            if (ReferenceEquals(vm, queue[0]) && queue.Count > 1)
            {
                StatusText = "[fake] hold-v3";
                AppendLog("[fake] hold enter");
                await Task.Delay(90000);
                AppendLog("[fake] hold exit");
                StatusText = "[fake] installing";
            }
        }

        StatusText = "[fake] done.";
        IsInstalling = false;
        NotifyInstalledAppsTooltip();
    }
#endif

    private void OnProgress(InstallUpdate u)
    {
        // Per-chunk download ticks carry no log-worthy text; only status
        // transitions and diagnostics go to the log feed.
        if (!string.IsNullOrWhiteSpace(u.Diagnostic))
            AppendBackendLog(u.Diagnostic);

        if (!string.IsNullOrEmpty(u.AppId))
        {
            var appVm = _allApps.FirstOrDefault(a => a.Id == u.AppId);
            if (appVm != null && u.Status != AppStatus.Downloading)
            {
                appVm.SetStatus(u.Status, Label(u.Status, u.Message));
                NotifyStageHeader();
                NotifyInstalledAppsTooltip();
            }
        }
        else if (!string.IsNullOrEmpty(u.Message))
        {
            AppendBackendLog($"{u.Status} | {u.Message}");
        }
        if (u.Status == AppStatus.Downloading)
            DownloadProgress = u.DownloadFraction;
        OverallProgress = u.Fraction;
        if (u.Message is not null)
            ProgressDetail = u.Message;
        if (u.AppId is null && u.Status == AppStatus.Succeeded)
            StatusText = u.Message ?? "Done.";
    }

    // Live install log at %LOCALAPPDATA%\epic-setup\install.log - contains the
    // FULL raw messages (not the shortened UI text), so failures are checkable.
    private string? _lastLoggedLine;

    private void AppendLog(string line)
    {
        try
        {
            // Live UI feed first: disk errors below must never break the drawer.
            LogLines.Add(line);
            if (LogLines.Count > 4000) LogLines.RemoveAt(0);

            // Dedupe consecutive identical lines to keep the log small.
            if (line == _lastLoggedLine) return;
            _lastLoggedLine = line;

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "epic-setup");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "install.log");

            // Bounded: start fresh if the log ever outgrows 1 MB.
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.WriteAllText(path, "");

            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }
        catch { }
    }

    private void AppendBackendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        BackendLogText = line;
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    private static string Label(AppStatus s, string? msg) => s switch
    {
        AppStatus.Pending => "Pending",
        AppStatus.Downloading => "Downloading…",
        AppStatus.Verifying => "Verifying signature…",
        AppStatus.Installing => "Installing…",
        AppStatus.Succeeded => msg ?? "Installed",
        AppStatus.Failed => "Failed: " + ShortenReason(msg),
        AppStatus.Skipped => msg ?? "Skipped",
        _ => s.ToString()
    };

    // Keep only the first reason clause of a failure message (cut at the first
    // Keep only the first reason clause of a failure message (cut at the first
    // ": "), so we show e.g. "Installer exited" instead of the full detail.
    // Messages without a sub-clause stay whole.
    private static string ShortenReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown error.";
        int idx = message.IndexOf(": ", StringComparison.Ordinal);
        if (idx >= 0) message = message[..idx];
        return message.TrimEnd(' ', '.') + ".";
    }

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
        NotifyStageHeader();

        // Live per-step elapsed seconds tick while the stage runs.
        if (value)
        {
            foreach (var a in ReviewItems) a.ResetStepClock();   // measure this run afresh
            _elapsedTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _elapsedTimer.Tick += (_, _) => { foreach (var a in ReviewItems) a.TickElapsed(); };
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer?.Stop();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _elapsedTimer;

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
