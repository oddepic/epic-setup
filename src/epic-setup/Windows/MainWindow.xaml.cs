using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EpicSetup.ViewModels;

namespace EpicSetup.Windows;

public partial class MainWindow : Window
{
    private bool _vmWired;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
        Loaded += (_, _) => WireViewModel();
        // Note: the details panel is never force-closed - not on install end,
        // not by Escape. Reading the log is the user's call.

#if DEBUG
        // Rehearse the install stage without touching the real engine.
        InputBindings.Add(new KeyBinding(
            ((MainViewModel)DataContext).FakeInstallCommand, Key.F12, ModifierKeys.None));
#endif
    }

    // View-model subscriptions: queue meta chips + live log behaviour.
    private void WireViewModel()
    {
        if (_vmWired || DataContext is not MainViewModel vm) return;
        _vmWired = true;

        vm.ReviewItems.CollectionChanged += (_, _) => UpdateQueueMeta(vm);
        vm.LogLines.CollectionChanged += (_, _) =>
        {
            if (vm.DetailsOpen && _logFollowTail) DetailsBox.ScrollToEnd();
        };
        vm.PropertyChanged += (_, e) => OnVmPropertyChanged(vm, e.PropertyName);

        // Follow the tail while the user is at the bottom; pause when they
        // scroll up to read, resume when they return to the bottom.
        DetailsBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogScrolled));
        UpdateQueueMeta(vm);
    }

    private bool _logFollowTail = true;

    private void LogScrolled(object sender, ScrollChangedEventArgs e)
    {
        var sv = DetailsBox;
        _logFollowTail = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 24;
    }

    // Queue header meta chip (estimated size) stays in sync with ReviewItems
    // without extra view-model surface; the count chip binds directly.
    private void UpdateQueueMeta(MainViewModel vm)
    {
        if (SizeChip is null || SizeValue is null) return; // XAML not parsed yet
        var totalMb = vm.ReviewItems.Sum(r => r.Model.Size ?? 0);
        SizeValue.Text = totalMb >= 1024 ? $"{totalMb / 1024.0:0.#} GB" : $"{totalMb} MB";
        SizeChip.Visibility = totalMb > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SizeChip_Loaded(object sender, RoutedEventArgs e) => WireViewModel();

    private void OnVmPropertyChanged(MainViewModel vm, string? prop)
    {
        if (prop == nameof(MainViewModel.DetailsOpen))
        {
            DetailsChevron.Text = vm.DetailsOpen ? "▾" : "▸";
            DetailsToggleLabel.Text = vm.DetailsOpen ? "hide details" : "show details";
            if (vm.DetailsOpen)
            {
                _logFollowTail = true;
                DetailsBox.ScrollToEnd();
            }
        }
        else if (prop == nameof(MainViewModel.IsInstalling))
        {
            if (vm.IsInstalling)
                vm.DetailsOpen = true;   // live output panel open by default
        }
    }
}
