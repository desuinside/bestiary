using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InatBestiary.Models;
using InatBestiary.Services;
using InatBestiary.ViewModels;

namespace InatBestiary.Views;

public partial class MainWindow : Window
{
    private readonly INaturalistService _inat = new();

    public MainWindow()
    {
        InitializeComponent();

        var searchBox = this.FindControl<AutoCompleteBox>("SearchBox")!;
        searchBox.AsyncPopulator = PopulateAsync;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.RequestInatUsername = ShowUsernameDialogAsync;
        };

        Loaded += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.TryOpenLastCatalogAsync();
        };

        // Track which iNat taxon was explicitly selected so the ViewModel
        // can use it for ancestry-based DB search (must be set before
        // SearchText changes, which happens when AutoCompleteBox updates Text).
        searchBox.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.SelectTaxon(searchBox.SelectedItem as TaxonSuggestion);
        };
    }

    private async Task<IEnumerable<object>> PopulateAsync(string? text, CancellationToken ct)
    {
        if (text is null || DataContext is not MainWindowViewModel vm || !vm.UseINaturalist)
            return [];

        var results = await _inat.SearchAsync(text, ct);
        return results.Cast<object>();
    }

    private void OnPhotoDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is PhotoViewModel vm)
            vm.OpenCommand.Execute(null);
    }

    private async Task<string?> ShowUsernameDialogAsync()
    {
        var dialog = new UsernameDialog();
        return await dialog.ShowDialog<string?>(this);
    }

    private async void OpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select photo catalog folder",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
            await vm.LoadCatalogAsync(folders[0].Path.LocalPath);
    }
}
