using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EpicSetup.ViewModels;

public partial class CategoryVm : ObservableObject
{
    public string Name { get; }
    public string? Hint { get; }
    public ObservableCollection<AppEntryVm> Apps { get; }

    private bool _suppress;

    public CategoryVm(string name, string? hint, IEnumerable<AppEntryVm> apps)
    {
        Name = name;
        Hint = hint;
        Apps = new ObservableCollection<AppEntryVm>(apps);
        foreach (var a in Apps) a.PropertyChanged += AppOnPropertyChanged;
    }

    public int SelectedCount => Apps.Count(a => a.IsChecked);
    public int Count => Apps.Count;

    public bool? HeaderCheck
    {
        get
        {
            if (Apps.Count == 0) return false;
            if (Apps.All(a => a.IsChecked)) return true;
            if (Apps.All(a => !a.IsChecked)) return false;
            return null;
        }
        set
        {
            if (_suppress) return;
            _suppress = true;
            var v = value ?? false;
            foreach (var a in Apps) a.IsChecked = v;
            _suppress = false;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HeaderCheck));
        }
    }

    private void AppOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppEntryVm.IsChecked)) return;
        if (_suppress) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderCheck));
    }
}