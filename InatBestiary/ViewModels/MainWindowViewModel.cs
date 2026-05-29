using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InatBestiary.Models;
using InatBestiary.Services;
using System.Collections.ObjectModel;

namespace InatBestiary.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly HashSet<string> JpgExtensions = [".jpg", ".jpeg"];

    // Matches a trailing separator + digit run: "Dunlin 1", "Dunlin-2", "Dunlin_3"
    private static readonly Regex TrailingNumber = new(@"[\s\-_]*\d+[\s\-_]*$", RegexOptions.Compiled);

    // Strips trailing numbers repeatedly so "Dunlin 1 2" → "Dunlin 1" → "Dunlin".
    // Returns empty string for pure-number names like "2024" (caller should skip those).
    private static string NormalizeFolderName(string name)
    {
        string prev;
        do { prev = name; name = TrailingNumber.Replace(name, "").Trim(); }
        while (name.Length > 0 && name != prev);
        return name;
    }

    private readonly PhotoScannerService _scanner  = new();
    private readonly INaturalistService  _inat     = new();
    private readonly GeocodingService    _geocoding = new();

    private CatalogDatabase?          _db;
    private string?                   _catalogRoot;
    private List<PhotoViewModel>      _allPhotos = [];
    private CancellationTokenSource?  _searchCts;
    private CancellationTokenSource?  _syncCts;
    private CancellationTokenSource?  _countryCts;
    // Non-null while a search is active; contains all matched file paths.
    private HashSet<string>?          _searchMatchedPaths;

    // Holds the iNat taxon that was explicitly selected from the autocomplete dropdown.
    public TaxonSuggestion? SelectedTaxon { get; set; }

    // Called from code-behind SelectionChanged (which fires AFTER Text changes).
    // Sets SelectedTaxon and immediately kicks off the local file search.
    public void SelectTaxon(TaxonSuggestion? taxon)
    {
        SelectedTaxon = taxon;
        if (taxon is not null && UseINaturalist && !string.IsNullOrWhiteSpace(SearchText))
            TriggerSearch(SearchText, taxon);
    }

    [ObservableProperty] private string _statusText = "Open a folder to explore your bestiary.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncDatabaseCommand))]
    [NotifyCanExecuteChangedFor(nameof(TagPhotosCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshLocationsCommand))]
    private bool _hasCatalog;

    [ObservableProperty]
    private FolderNodeViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TagPhotosCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isTagging;

    [ObservableProperty] private double _taggingProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncDatabaseCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isSyncing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshLocationsCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isResolvingLocations;

    // Toolbar
    [ObservableProperty] private string _searchText      = "";
    [ObservableProperty] private string _catalogRootName = "";
    [ObservableProperty] private string _windowTitle     = "iNat Bestiary";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortLabel))]
    [NotifyPropertyChangedFor(nameof(DateSortLabel))]
    [NotifyPropertyChangedFor(nameof(RatingSortLabel))]
    private int _sortModeIndex;   // 0=Name 1=Date 2=Rating
    [ObservableProperty] private bool   _isPreviewMode = false;
    [ObservableProperty] private bool   _jpgOnly       = true;
    [ObservableProperty] private bool   _useINaturalist = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionGlyph))]
    [NotifyPropertyChangedFor(nameof(NameSortLabel))]
    [NotifyPropertyChangedFor(nameof(DateSortLabel))]
    [NotifyPropertyChangedFor(nameof(RatingSortLabel))]
    private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhotos))]
    private ObservableCollection<PhotoViewModel> _photos = [];

    public bool HasPhotos       => Photos.Count > 0;
    public bool IsBusy          => IsSyncing || IsTagging || IsResolvingLocations;
    public string SortDirectionGlyph => SortDescending ? "↓" : "↑";
    public string NameSortLabel   => "Filename" + (SortModeIndex == 0 ? " " + SortDirectionGlyph : "");
    public string DateSortLabel   => "Date"     + (SortModeIndex == 1 ? " " + SortDirectionGlyph : "");
    public string RatingSortLabel => "Rating"   + (SortModeIndex == 2 ? " " + SortDirectionGlyph : "");

    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = [];

    // null → show all RootNodes (no search active); otherwise the filtered subset.
    private ObservableCollection<FolderNodeViewModel>? _filteredRootNodes;
    public ObservableCollection<FolderNodeViewModel> FilteredRootNodes => _filteredRootNodes ?? RootNodes;

    private void ResetRootFilter()
    {
        _filteredRootNodes = null;
        OnPropertyChanged(nameof(FilteredRootNodes));
        foreach (var root in RootNodes) root.ResetSearchFilter();
    }

    // ── property change hooks ──────────────────────────────────────────────

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value is null) return;
        if (_searchMatchedPaths is not null)
            LoadSearchResultsForFolder(value.FullPath);
        else
            LoadPhotos(value.FullPath);
    }

    partial void OnSearchTextChanged(string value)
    {
        TagPhotosCommand.NotifyCanExecuteChanged();
        SyncDatabaseCommand.NotifyCanExecuteChanged();

        // If text no longer matches the selected taxon, clear it
        if (SelectedTaxon is not null &&
            !string.Equals(SelectedTaxon.DisplayName, value, StringComparison.OrdinalIgnoreCase))
            SelectedTaxon = null;

        UpdateFolderMatches(RootNodes, value);

        if (string.IsNullOrWhiteSpace(value))
        {
            // Search cleared → restore full tree and return to folder view
            _searchCts?.Cancel();
            _searchCts = null;
            _searchMatchedPaths = null;
            ResetRootFilter();
            if (SelectedFolder is not null)
                LoadPhotos(SelectedFolder.FullPath);
            else
                Photos = [];
            return;
        }

        // iNat mode: typing populates the autocomplete dropdown only.
        // Local-file search fires only after the user selects a taxon from the list.
        if (UseINaturalist)
        {
            if (SelectedTaxon is null)
            {
                // User is still typing — clear stale results and wait for a selection.
                _searchCts?.Cancel();
                _searchCts = null;
                ResetRootFilter();
                Photos = [];
                return;
            }
            // A taxon was just selected from the dropdown → execute the search.
            TriggerSearch(value, SelectedTaxon);
            return;
        }

        // iNat OFF: search local files and folder names as the user types.
        TriggerSearch(value, null);
    }

    partial void OnSortModeIndexChanged(int value)   => OnFilterChanged();
    partial void OnSortDescendingChanged(bool value) => OnFilterChanged();
    partial void OnJpgOnlyChanged(bool value)        => OnFilterChanged();

    partial void OnUseINaturalistChanged(bool value)
    {
        if (!value)
        {
            _syncCts?.Cancel();
            // Switching to local mode: run a file-name search with whatever is typed.
            if (!string.IsNullOrWhiteSpace(SearchText))
                TriggerSearch(SearchText, null);
        }
        else
        {
            // Switching to iNat mode: drop any local results and wait for dropdown selection.
            _searchCts?.Cancel();
            _searchCts = null;
            _searchMatchedPaths = null;
            ResetRootFilter();
            if (!string.IsNullOrWhiteSpace(SearchText))
                Photos = [];
        }
    }

    private void OnFilterChanged()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            ApplyFolderFilter();
        else if (!UseINaturalist || SelectedTaxon is not null)
            TriggerSearch(SearchText, SelectedTaxon);
    }

    [RelayCommand] private void SetPreviewMode() => IsPreviewMode = true;
    [RelayCommand] private void SetListMode()   => IsPreviewMode = false;
    [RelayCommand] private void ClearSearch()   => SearchText = "";

    [RelayCommand]
    private void SortByName()
    {
        if (SortModeIndex == 0) SortDescending = !SortDescending;
        else { SortModeIndex = 0; SortDescending = false; }
    }

    [RelayCommand]
    private void SortByDate()
    {
        if (SortModeIndex == 1) SortDescending = !SortDescending;
        else { SortModeIndex = 1; SortDescending = true; }
    }

    [RelayCommand]
    private void SortByRating()
    {
        if (SortModeIndex == 2) SortDescending = !SortDescending;
        else { SortModeIndex = 2; SortDescending = true; }
    }

    // Injected by MainWindow code-behind so the VM can show the username dialog
    public Func<Task<string?>>? RequestInatUsername { get; set; }

    private bool CanRefreshLocations() => HasCatalog && !IsResolvingLocations;

    [RelayCommand(CanExecute = nameof(CanRefreshLocations))]
    private async Task RefreshLocationsAsync()
    {
        if (_catalogRoot is null) return;
        IsResolvingLocations = true;
        _countryCts?.Cancel();
        _countryCts = new CancellationTokenSource();
        try { await DoRefreshLocationsAsync(_catalogRoot, _countryCts.Token); }
        finally { IsResolvingLocations = false; }
    }

    [RelayCommand]
    private async Task RefreshFolderLocationsAsync(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        IsResolvingLocations = true;
        _countryCts?.Cancel();
        _countryCts = new CancellationTokenSource();
        try { await DoRefreshLocationsAsync(folderPath, _countryCts.Token); }
        finally { IsResolvingLocations = false; }
    }

    private async Task DoRefreshLocationsAsync(string rootPath, CancellationToken ct)
    {
        StatusText = "Scanning for photos with GPS…";
        var tempVms = new List<PhotoViewModel>();
        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                         .Where(_scanner.IsImageFile))
            {
                if (ct.IsCancellationRequested) break;
                var meta = _scanner.ReadMetadata(file);
                if (meta.Latitude.HasValue && meta.Longitude.HasValue)
                    tempVms.Add(new PhotoViewModel(meta));
            }
        }, ct);

        if (tempVms.Count == 0)
        {
            if (!ct.IsCancellationRequested) StatusText = "No photos with GPS coordinates found.";
            return;
        }

        await ResolveCountriesAsync(tempVms, ct, showProgress: true);

        // Update currently displayed photos using the freshly cached values
        if (_db is not null && !ct.IsCancellationRequested)
        {
            foreach (var vm in _allPhotos.Where(v => v.Latitude.HasValue && v.Longitude.HasValue))
            {
                var cached = await _db.GetCachedCountryAsync(vm.Latitude!.Value, vm.Longitude!.Value);
                if (cached is not null) vm.SetCountry(cached);
            }
        }
    }

    private bool CanTagPhotos() => HasCatalog && !IsTagging && _db is not null;

    [RelayCommand(CanExecute = nameof(CanTagPhotos))]
    private async Task TagPhotosAsync()
    {
        if (_db is null || _catalogRoot is null) return;
        IsTagging = true; TaggingProgress = 0;
        using var cts = new CancellationTokenSource();
        try { await DoTagPhotosAsync(_catalogRoot, cts.Token); }
        catch (Exception ex) { StatusText = $"Tagging failed: {ex.Message}"; }
        finally { IsTagging = false; TaggingProgress = 0; }
    }

    [RelayCommand]
    private async Task TagFolderAsync(string? folderPath)
    {
        if (_db is null || string.IsNullOrEmpty(folderPath)) return;
        IsTagging = true; TaggingProgress = 0;
        using var cts = new CancellationTokenSource();
        try { await DoTagPhotosAsync(folderPath, cts.Token); }
        catch (Exception ex) { StatusText = $"Tagging failed: {ex.Message}"; }
        finally { IsTagging = false; TaggingProgress = 0; }
    }

    private async Task DoTagPhotosAsync(string rootPath, CancellationToken ct)
    {
        if (_db is null) return;
        StatusText = "Scanning mapped folders…";

        var mapped = await _db.GetMappedSubfoldersAsync(rootPath);
        if (mapped.Count == 0)
        {
            StatusText = "No folders with iNaturalist mappings found here. Run 'Build DB' first.";
            return;
        }

        // One iNat API call per unique taxon, cached; one retry on failure
        var hierarchyCache = new Dictionary<int, TaxonHierarchy?>();
        foreach (var (_, taxonId) in mapped)
        {
            if (hierarchyCache.ContainsKey(taxonId)) continue;
            var h = await _inat.GetTaxonHierarchyAsync(taxonId, ct);
            if (h is null)
            {
                await Task.Delay(800, ct);
                h = await _inat.GetTaxonHierarchyAsync(taxonId, ct);
            }
            hierarchyCache[taxonId] = h;
        }

        // Sort longest-first so the deepest (most specific) mapped folder wins.
        var sortedMapped = mapped
            .Select(m => (
                Norm: m.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                m.TaxonId))
            .OrderByDescending(m => m.Norm.Length)
            .ToList();

        var selectedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        StatusText = "Building file list…";
        var (work, skippedHierarchy) = await Task.Run(() =>
        {
            var list    = new List<(string FilePath, TaxonHierarchy Hierarchy)>();
            int skipped = 0;
            foreach (var filePath in System.IO.Directory.EnumerateFiles(selectedRoot, "*", SearchOption.AllDirectories))
            {
                if (!JpgExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())) continue;
                var fileDir = Path.GetDirectoryName(filePath)!;
                foreach (var (norm, taxonId) in sortedMapped)
                {
                    if (fileDir.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                        fileDir.StartsWith(norm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hierarchyCache.TryGetValue(taxonId, out var h) && h is not null)
                        {
                            if (_scanner.HasTaxonomyTags(filePath)) skipped++;
                            else list.Add((filePath, h));
                        }
                        else skipped++;
                        break;
                    }
                }
            }
            return (list, skipped);
        });

        int total = work.Count, done = 0, failed = 0;
        StatusText = $"Tagging 0 / {total}…";

        await Task.Run(() =>
        {
            foreach (var (filePath, h) in work)
            {
                try { JpegXmpWriter.Write(filePath, h); done++; }
                catch { failed++; }

                var d = done + failed;
                if (d % 50 == 0)
                {
                    var progress = total > 0 ? (double)d / total : 0;
                    var status   = $"Tagging {d} / {total}…";
                    Dispatcher.UIThread.Post(() => { TaggingProgress = progress; StatusText = status; });
                }
            }
        });

        int finalDone = done, finalFailed = failed, finalSkipped = skippedHierarchy;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            TaggingProgress = 1;
            var parts = new List<string> { $"Tagged {finalDone} photo(s) across {mapped.Count} folder(s)." };
            if (finalFailed  > 0) parts.Add($"{finalFailed} write error(s).");
            if (finalSkipped > 0) parts.Add($"{finalSkipped} skipped (iNat hierarchy unavailable — retry).");
            StatusText = string.Join("  ", parts);
        });
    }

    [RelayCommand(CanExecute = nameof(CanTagPhotos))]
    private async Task TagWithLocationsAsync()
    {
        if (_db is null || _catalogRoot is null || RequestInatUsername is null) return;
        var username = await RequestInatUsername();
        if (string.IsNullOrWhiteSpace(username)) return;
        IsTagging = true; TaggingProgress = 0;
        using var cts = new CancellationTokenSource();
        try { await DoTagWithLocationsAsync(_catalogRoot, username, cts.Token); }
        catch (OperationCanceledException) { }
        finally { IsTagging = false; TaggingProgress = 0; }
    }

    [RelayCommand]
    private async Task TagFolderWithLocationsAsync(string? folderPath)
    {
        if (_db is null || string.IsNullOrEmpty(folderPath) || RequestInatUsername is null) return;
        var username = await RequestInatUsername();
        if (string.IsNullOrWhiteSpace(username)) return;
        IsTagging = true; TaggingProgress = 0;
        using var cts = new CancellationTokenSource();
        try { await DoTagWithLocationsAsync(folderPath, username, cts.Token); }
        catch (OperationCanceledException) { }
        finally { IsTagging = false; TaggingProgress = 0; }
    }

    private async Task DoTagWithLocationsAsync(string rootPath, string username, CancellationToken ct)
    {
        if (_db is null) return;
        StatusText = "Loading folder mappings…";

        var mapped = await _db.GetMappedSubfoldersAsync(rootPath);
        if (mapped.Count == 0) { StatusText = "No iNaturalist mappings found. Run 'Build DB' first."; return; }

        var taxonIds = mapped.Select(m => m.TaxonId).Distinct().ToList();
        StatusText = $"Fetching iNaturalist observations for {username}…";

        var observations = new List<InatObservation>();
        foreach (var taxonId in taxonIds)
        {
            var batch = await _inat.GetUserObservationsAsync(username, taxonId, ct);
            observations.AddRange(batch.Where(o => o.Latitude.HasValue && o.Longitude.HasValue));
        }
        if (observations.Count == 0) { StatusText = $"No geolocated observations found for '{username}'."; return; }

        var byDate = observations.GroupBy(o => o.ObservedOn).ToDictionary(g => g.Key, g => g.ToList());

        StatusText = "Scanning photos…";
        var jpgFiles = await Task.Run(() =>
            Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                .Where(f => JpgExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList(), ct);

        int matched = 0, failed = 0;
        StatusText = $"Matching {jpgFiles.Count} photos to {observations.Count} observations…";

        var placeCache = new List<(double Lat, double Lon, string Place)>();
        await Task.Run(() =>
        {
            int done = 0;
            foreach (var file in jpgFiles)
            {
                try
                {
                    var meta = _scanner.ReadMetadata(file);
                    if (meta.DateTaken is null) { done++; continue; }
                    if (meta.Latitude.HasValue) { done++; continue; }

                    var fileDt   = meta.DateTaken.Value;
                    var fileDate = DateOnly.FromDateTime(fileDt);

                    InatObservation? best       = null;
                    double           closestDiff = double.MaxValue;
                    foreach (var obs in observations)
                    {
                        if (obs.ObservedAt is null) continue;
                        var diff = Math.Abs((obs.ObservedAt.Value.DateTime - fileDt).TotalSeconds);
                        if (diff < closestDiff) { closestDiff = diff; best = obs; }
                    }
                    if (closestDiff > 14 * 3600) best = null;

                    if (best is null && byDate.TryGetValue(fileDate, out var sameDayObs))
                    {
                        best = sameDayObs
                            .OrderBy(o => o.ObservedAt.HasValue
                                ? Math.Abs((o.ObservedAt.Value.DateTime - fileDt).TotalSeconds)
                                : double.MaxValue)
                            .FirstOrDefault();
                    }
                    if (best is null) { done++; continue; }

                    JpegXmpWriter.WriteGps(file, best.Latitude!.Value, best.Longitude!.Value);
                    if (best.PlaceGuess is not null)
                        lock (placeCache)
                            placeCache.Add((best.Latitude!.Value, best.Longitude!.Value, best.PlaceGuess));
                    matched++;
                }
                catch { failed++; }

                done++;
                if (done % 20 == 0)
                {
                    var progress = jpgFiles.Count > 0 ? (double)done / jpgFiles.Count : 0;
                    var status   = $"Tagging GPS: {done}/{jpgFiles.Count}…";
                    Dispatcher.UIThread.Post(() => { TaggingProgress = progress; StatusText = status; });
                }
            }
        }, ct);

        foreach (var (lat, lon, place) in placeCache)
            try { await _db.CacheCountryAsync(lat, lon, place); } catch { }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusText      = $"GPS tagged: {matched} photo(s) matched" + (failed > 0 ? $", {failed} failed" : "") + ".";
            TaggingProgress = 0;
        });
    }

    private bool CanSyncDatabase() => HasCatalog && !IsSyncing && _db is not null;

    [RelayCommand(CanExecute = nameof(CanSyncDatabase))]
    private async Task SyncDatabaseAsync()
    {
        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        try
        {
            await RunSyncCoreAsync(_syncCts.Token, isManual: true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSyncing = false;
        }
    }

    // ── catalog open ──────────────────────────────────────────────────────

    public async Task LoadCatalogAsync(string rootPath)
    {
        _syncCts?.Cancel();
        _searchCts?.Cancel();
        _countryCts?.Cancel();

        RootNodes.Clear();
        _filteredRootNodes  = null;
        DisposePhotos();
        Photos = [];
        SelectedTaxon       = null;
        _searchMatchedPaths = null;
        _catalogRoot = rootPath;
        SaveLastCatalog(rootPath);
        StatusText   = "Scanning…";

        if (_db is not null) await _db.DisposeAsync();
        _db = new CatalogDatabase();
        await _db.OpenAsync(rootPath);

        var folderName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        CatalogRootName = folderName.Length > 0 ? folderName : rootPath;
        WindowTitle = $"iNat Bestiary — {CatalogRootName}";

        // Pre-initialize commands on UI thread — BuildTree runs on background thread.
        var ctxTag     = TagFolderCommand;
        var ctxTagLoc  = TagFolderWithLocationsCommand;
        var ctxRefresh = RefreshFolderLocationsCommand;

        var root = await Task.Run(() => BuildTree(rootPath, ctxTag, ctxTagLoc, ctxRefresh));
        foreach (var child in root.Children)
            RootNodes.Add(child);
        HasCatalog = true;
        StatusText = $"Opened: {rootPath}";
        SyncDatabaseCommand.NotifyCanExecuteChanged();

        // Mark already-synced folders from the DB
        var mapped = await _db.GetAllMappedFoldersAsync();
        MarkSynced(RootNodes, mapped);

        // Auto-sync unmapped folders in background (quiet, 300 ms rate-limit)
        if (UseINaturalist)
        {
            _syncCts = new CancellationTokenSource();
            _ = RunSyncCoreAsync(_syncCts.Token, isManual: false);
        }
    }

    public async Task TryOpenLastCatalogAsync()
    {
        var last = LoadLastCatalog();
        if (last is not null)
            await LoadCatalogAsync(last);
    }

    // ── folder mode (no search text) ──────────────────────────────────────

    private void DisposePhotos()
    {
        foreach (var vm in _allPhotos) vm.Dispose();
        _allPhotos.Clear();
    }

    private void LoadPhotos(string folderPath)
    {
        DisposePhotos();
        var files = _scanner.GetImageFiles(folderPath).ToList();
        StatusText = $"Loading {files.Count} file(s)…";

        foreach (var file in files)
        {
            var vm = new PhotoViewModel(_scanner.ReadMetadata(file));
            _allPhotos.Add(vm);
            vm.LoadThumbnailAsync();
        }

        ApplyFolderFilter();
        var folderName = Path.GetFileName(folderPath);
        var childCount = SelectedFolder?.Children.Count ?? 0;
        if (childCount > 0)
        {
            var total = SelectedFolder != null ? CountRecursivePhotos(SelectedFolder) : _allPhotos.Count;
            var sub   = childCount == 1 ? "1 subfolder" : $"{childCount} subfolders";
            StatusText = $"{_allPhotos.Count} file(s) here · {sub} · {total} total — {folderName}";
        }
        else
            StatusText = $"{_allPhotos.Count} file(s) in {folderName}";
        StartCountryResolution(_allPhotos.ToList());
    }

    private void LoadSearchResultsForFolder(string folderPath)
    {
        if (_searchMatchedPaths is null) return;
        DisposePhotos();
        _allPhotos = _searchMatchedPaths
            .Where(f => string.Equals(Path.GetDirectoryName(f), folderPath, StringComparison.OrdinalIgnoreCase))
            .Select(f => new PhotoViewModel(_scanner.ReadMetadata(f)))
            .ToList();
        foreach (var vm in _allPhotos) vm.LoadThumbnailAsync();
        ApplyFolderFilter();
        StartCountryResolution(_allPhotos.ToList());
    }

    private void StartCountryResolution(List<PhotoViewModel> vms)
    {
        _countryCts?.Cancel();
        _countryCts = new CancellationTokenSource();
        _ = ResolveCountriesAsync(vms, _countryCts.Token);
    }

    private async Task ResolveCountriesAsync(List<PhotoViewModel> vms, CancellationToken ct,
                                             bool showProgress = false)
    {
        var gpsVms = vms.Where(vm => vm.Latitude.HasValue && vm.Longitude.HasValue).ToList();
        bool needsRateLimit = false;
        int resolved = 0;

        for (int i = 0; i < gpsVms.Count; i++)
        {
            var vm = gpsVms[i];
            if (ct.IsCancellationRequested) break;

            try
            {
                string? country = null;

                if (_db is not null)
                    country = await _db.GetCachedCountryAsync(vm.Latitude!.Value, vm.Longitude!.Value);

                if (country is null)
                {
                    if (showProgress)
                        StatusText = $"Resolving locations… ({i + 1}/{gpsVms.Count})  " +
                                     $"lat={vm.Latitude!.Value:F5} lon={vm.Longitude!.Value:F5}";

                    // Nominatim policy: max 1 req/s. Rate-limit between calls, not before the first.
                    if (needsRateLimit)
                    {
                        await Task.Delay(1100, ct); // no ConfigureAwait — stay on UI thread
                        if (ct.IsCancellationRequested) break;
                    }
                    needsRateLimit = true;
                    country = await _geocoding.GetCountryAsync(vm.Latitude!.Value, vm.Longitude!.Value, ct);
                    if (country is not null && _db is not null)
                        await _db.CacheCountryAsync(vm.Latitude!.Value, vm.Longitude!.Value, country);
                }

                if (country is not null)
                {
                    vm.SetCountry(country);
                    resolved++;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ArgumentOutOfRangeException ex) when (showProgress)
            {
                StatusText = $"Invalid GPS: {ex.Message}";
                break;
            }
            catch (Exception ex) when (showProgress)
            {
                StatusText = $"Nominatim error: {ex.GetType().Name}: {ex.Message}";
                break;
            }
            catch { } // swallow when not in manual mode
        }

        if (showProgress && !ct.IsCancellationRequested)
            StatusText = resolved > 0
                ? $"Locations resolved: {resolved} of {gpsVms.Count} photo(s)."
                : StatusText.StartsWith("Invalid GPS") || StatusText.StartsWith("Nominatim error")
                    ? StatusText  // already set a specific error
                    : $"Nominatim returned no country for these coordinates.";
    }

    private void ApplyFolderFilter()
    {
        IEnumerable<PhotoViewModel> q = _allPhotos;
        if (JpgOnly)
            q = q.Where(p => JpgExtensions.Contains(Path.GetExtension(p.FileName).ToLowerInvariant()));
        Photos = new ObservableCollection<PhotoViewModel>(ApplySort(q));
    }

    // ── recursive search ──────────────────────────────────────────────────

    private void TriggerSearch(string text, TaxonSuggestion? taxon)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = ExecuteSearchAsync(text.Trim(), taxon, _searchCts.Token);
    }

    private async Task ExecuteSearchAsync(string text, TaxonSuggestion? taxon, CancellationToken ct)
    {
        if (_catalogRoot is null) return;
        // Clear any folder filter left by the previous search
        ResetRootFilter();
        Photos = [];
        StatusText = "Searching…";

        try
        {
            List<string> filePaths;

            if (taxon is not null && _db is not null)
            {
                // User selected a specific taxon from autocomplete → ancestry-based DB search
                await _db.StoreTaxonAsync(taxon);
                var folders = await _db.FindFoldersByAncestorAsync(taxon.Id);
                filePaths = folders.Count > 0
                    ? await GetFilesFromFolders(folders, ct)
                    : await TryDbOrFileScanAsync(text, ct);
            }
            else
            {
                // No autocomplete selection → try DB name lookup, then file-name scan
                filePaths = await TryDbOrFileScanAsync(text, ct);
            }

            if (ct.IsCancellationRequested) return;

            if (JpgOnly)
                filePaths = filePaths
                    .Where(f => JpgExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

            if (!ct.IsCancellationRequested)
            {
                var matchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fp in filePaths) matchedDirs.Add(Path.GetDirectoryName(fp)!);

                var visibleRoots = new List<FolderNodeViewModel>();
                foreach (var root in RootNodes)
                    if (root.ApplySearchFilter(matchedDirs))
                        visibleRoots.Add(root);
                _filteredRootNodes = new ObservableCollection<FolderNodeViewModel>(visibleRoots);
                OnPropertyChanged(nameof(FilteredRootNodes));

                _searchMatchedPaths = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
                Photos = [];
                StatusText = $"{filePaths.Count} photo(s) in {matchedDirs.Count} folder(s) — select a folder to view";
            }
        }
        catch (OperationCanceledException) { }
    }

    // Tries DB taxon-name lookup first; falls back to recursive filename scan.
    private async Task<List<string>> TryDbOrFileScanAsync(string text, CancellationToken ct)
    {
        if (_db is not null)
        {
            var taxonIds = (await _db.FindTaxonIdsByNameAsync(text)).ToList();

            // DB miss → try iNat API (handles orders/families not stored as leaf taxa)
            if (taxonIds.Count == 0 && UseINaturalist)
            {
                var suggestions = await _inat.SearchAsync(text, ct);
                if (suggestions.Count > 0)
                {
                    foreach (var s in suggestions)
                        await _db.StoreTaxonAsync(s);
                    taxonIds = suggestions.Select(s => s.Id).ToList();
                }
            }

            if (taxonIds.Count > 0)
            {
                var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in taxonIds)
                    foreach (var f in await _db.FindFoldersByAncestorAsync(id))
                        allFolders.Add(f);

                if (allFolders.Count > 0)
                    return await GetFilesFromFolders(allFolders, ct);
            }
        }
        return await ScanRecursiveAsync(text, ct);
    }

    private Task<List<string>> GetFilesFromFolders(IEnumerable<string> folders, CancellationToken ct) =>
        Task.Run(() =>
            folders
                .Where(System.IO.Directory.Exists)
                .SelectMany(f => _scanner.GetImageFiles(f))
                .ToList(),
            ct);

    // Searches file names AND folder names anywhere in the path
    private Task<List<string>> ScanRecursiveAsync(string filter, CancellationToken ct) =>
        Task.Run(() =>
            System.IO.Directory.EnumerateFiles(_catalogRoot!, "*", SearchOption.AllDirectories)
                .Where(_scanner.IsImageFile)
                .Where(f =>
                    Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (Path.GetDirectoryName(f) ?? "")
                        .Split(Path.DirectorySeparatorChar)
                        .Any(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList(),
            ct);

    // ── iNaturalist sync core ─────────────────────────────────────────────

    // Shared by auto-sync (startup) and manual "Build DB" command.
    // isManual = true  → update status every folder, 100 ms delay.
    // isManual = false → update status every 25 folders + on each new match, 300 ms delay.
    private async Task RunSyncCoreAsync(CancellationToken ct, bool isManual)
    {
        if (_db is null) return;

        var folders = GetAllFolderPaths(RootNodes)
            .Where(p => Path.GetFileName(p).Length > 0)
            .ToList();

        int synced = 0, done = 0, skipped = 0, total = folders.Count;

        if (!isManual && total > 0)
            Dispatcher.UIThread.Post(() => StatusText = $"Syncing species names… 0/{total}");

        foreach (var folderPath in folders)
        {
            if (ct.IsCancellationRequested) break;

            if (await _db.IsFolderMappedAsync(folderPath))
            {
                done++;
                skipped++;
                if (isManual)
                {
                    var d = done; var t = total;
                    Dispatcher.UIThread.Post(() =>
                        StatusText = $"Building DB: {d}/{t}  (skipping already-mapped)…");
                }
                else if (done % 25 == 0)
                {
                    var d = done; var t = total;
                    Dispatcher.UIThread.Post(() => StatusText = $"Syncing… {d}/{t}");
                }
                continue;
            }

            var folderName = NormalizeFolderName(Path.GetFileName(folderPath));
            if (string.IsNullOrEmpty(folderName)) { done++; continue; }

            var match = await _inat.FindExactMatchAsync(folderName, ct);
            if (match is not null)
            {
                await _db.StoreTaxonAsync(match);
                await _db.StoreFolderTaxonAsync(folderPath, match.Id, auto: true);
                synced++;

                var path = folderPath;
                Dispatcher.UIThread.Post(() =>
                {
                    var node = FindNode(RootNodes, path);
                    if (node is not null) node.IsSynced = true;
                });
            }

            done++;
            if (isManual)
            {
                var d = done; var t = total;
                Dispatcher.UIThread.Post(() => StatusText = $"Building DB: {d}/{t}…");
            }
            else
            {
                var d = done; var t = total; var s = synced;
                Dispatcher.UIThread.Post(() =>
                    StatusText = $"Syncing… {d}/{t}" + (s > 0 ? $"  ({s} matched)" : ""));
            }

            try { await Task.Delay(isManual ? 100 : 300, ct); }
            catch (OperationCanceledException) { break; }
        }

        if (!ct.IsCancellationRequested)
            Dispatcher.UIThread.Post(() =>
                StatusText = synced > 0
                    ? $"Done. Mapped {synced} new folder(s) ({skipped} already had entries)."
                    : skipped > 0
                        ? $"Done. No new matches found ({skipped} folder(s) already mapped)."
                        : "Done. No folders matched any iNaturalist taxon.");
    }

    // ── tree helpers ──────────────────────────────────────────────────────

    private static int CountRecursivePhotos(FolderNodeViewModel node) =>
        node.PhotoCount + node.Children.Sum(CountRecursivePhotos);

    private FolderNodeViewModel BuildTree(string path,
        System.Windows.Input.ICommand ctxTag,
        System.Windows.Input.ICommand ctxTagLoc,
        System.Windows.Input.ICommand ctxRefresh)
    {
        var photoCount = 0;
        try { photoCount = _scanner.GetImageFiles(path).Count(); } catch { }

        var node = new FolderNodeViewModel(path)
        {
            PhotoCount = photoCount,
            ContextTagCommand          = ctxTag,
            ContextTagLocationsCommand = ctxTagLoc,
            ContextRefreshCommand      = ctxRefresh
        };
        try
        {
            foreach (var sub in System.IO.Directory.GetDirectories(path).Order())
                node.Children.Add(BuildTree(sub, ctxTag, ctxTagLoc, ctxRefresh));
        }
        catch { }

        return node;
    }

    private static void UpdateFolderMatches(IEnumerable<FolderNodeViewModel> nodes, string text)
    {
        foreach (var node in nodes)
        {
            node.IsSearchMatch = !string.IsNullOrWhiteSpace(text) &&
                                 node.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
            UpdateFolderMatches(node.Children, text);
        }
    }

    private static void MarkSynced(IEnumerable<FolderNodeViewModel> nodes, HashSet<string> mapped)
    {
        foreach (var node in nodes)
        {
            if (mapped.Contains(node.FullPath)) node.IsSynced = true;
            MarkSynced(node.Children, mapped);
        }
    }

    private static IEnumerable<string> GetAllFolderPaths(IEnumerable<FolderNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.FullPath;
            foreach (var child in GetAllFolderPaths(node.Children))
                yield return child;
        }
    }

    private static FolderNodeViewModel? FindNode(IEnumerable<FolderNodeViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.FullPath == path) return node;
            var found = FindNode(node.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    private IEnumerable<PhotoViewModel> ApplySort(IEnumerable<PhotoViewModel> q) =>
        (SortModeIndex, SortDescending) switch
        {
            (1, false) => q.OrderBy(p => p.DateTakenSortKey ?? DateTime.MinValue),
            (1, true)  => q.OrderByDescending(p => p.DateTakenSortKey ?? DateTime.MinValue),
            (2, false) => q.OrderBy(p => p.RatingValue),
            (2, true)  => q.OrderByDescending(p => p.RatingValue),
            (_, false) => q.OrderBy(p => p.FileName, NaturalSortComparer.Instance),
            _          => q.OrderByDescending(p => p.FileName, NaturalSortComparer.Instance),
        };

    // ── settings persistence ──────────────────────────────────────────────

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InatBestiary", "last_catalog.txt");

    private static string? LoadLastCatalog()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var path = File.ReadAllText(SettingsPath).Trim();
            return Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static void SaveLastCatalog(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, path);
        }
        catch { }
    }
}
