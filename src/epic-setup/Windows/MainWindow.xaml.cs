using System;
using System.Collections.Specialized;
using System.Windows;
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel vm && vm.DetailsOpen)
            {
                vm.DetailsOpen = false;
                e.Handled = true;
            }
        };

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
            if (vm.DetailsOpen) DetailsScroller.ScrollToEnd();
        };
        vm.PropertyChanged += (_, e) => OnVmPropertyChanged(vm, e.PropertyName);

        UpdateQueueMeta(vm);
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
            if (vm.DetailsOpen) DetailsScroller.ScrollToEnd();
        }
        else if (prop == nameof(MainViewModel.IsInstalling))
        {
            if (vm.IsInstalling)
                vm.DetailsOpen = true;   // live output panel open by default
        }
    }
}
