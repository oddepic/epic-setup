using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EpicSetup.ViewModels;

public partial class TabVm : ObservableObject
{
    public string Name { get; }
    public ObservableCollection<CategoryVm> Categories { get; }

    [ObservableProperty] private bool _isSelected;

    public TabVm(string name, IEnumerable<CategoryVm> categories)
    {
        Name = name;
        Categories = new ObservableCollection<CategoryVm>(categories);
    }
}