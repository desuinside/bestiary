using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InatBestiary.ViewModels;

public partial class FolderNodeViewModel : ObservableObject
{
    public string FullPath { get; }
    public string Name { get; }
    public int PhotoCount { get; init; }
    public string DisplayLabel => PhotoCount > 0 ? $"{Name}  ({PhotoCount})" : Name;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSearchMatch;
    [ObservableProperty] private bool _isSynced;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    // Injected by MainWindowViewModel — shared command instances, not per-node.
    public System.Windows.Input.ICommand? ContextTagCommand          { get; init; }
    public System.Windows.Input.ICommand? ContextTagLocationsCommand { get; init; }
    public System.Windows.Input.ICommand? ContextRefreshCommand      { get; init; }

    // null  → show all Children (no filter active)
    // other → show only these children (search filter active)
    private ObservableCollection<FolderNodeViewModel>? _filteredChildren;
    public ObservableCollection<FolderNodeViewModel> FilteredChildren =>
        _filteredChildren ?? Children;

    public FolderNodeViewModel(string fullPath)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath) is { Length: > 0 } n ? n : fullPath;
    }

    // Walk the subtree; make visible only nodes on the path to any matched directory.
    // Returns true if this node should be visible (it or a descendant matched).
    internal bool ApplySearchFilter(HashSet<string> matchedDirs)
    {
        var visibleChildren = new List<FolderNodeViewModel>();
        foreach (var child in Children)
            if (child.ApplySearchFilter(matchedDirs))
                visibleChildren.Add(child);

        bool selfMatch = matchedDirs.Contains(FullPath);
        bool visible   = selfMatch || visibleChildren.Count > 0;

        // Replace FilteredChildren only when the set differs from Children
        _filteredChildren = visibleChildren.Count == Children.Count
            ? null
            : new ObservableCollection<FolderNodeViewModel>(visibleChildren);

        OnPropertyChanged(nameof(FilteredChildren));

        IsSearchMatch = selfMatch;
        if (visible) IsExpanded = true;

        return visible;
    }

    // Restore tree to show everything (called when search is cleared).
    internal void ResetSearchFilter()
    {
        _filteredChildren = null;
        OnPropertyChanged(nameof(FilteredChildren));
        IsSearchMatch = false;
        foreach (var child in Children)
            child.ResetSearchFilter();
    }
}
