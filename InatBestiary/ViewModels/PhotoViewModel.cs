using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InatBestiary.Models;
using InatBestiary.Services;

namespace InatBestiary.ViewModels;

public partial class PhotoViewModel : ObservableObject, IDisposable
{
    public string FilePath { get; }

    // These are observable so lazy-loaded metadata can update the UI
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _dateTaken = "…";
    [ObservableProperty] private string _ratingStars = "☆☆☆☆☆";
    [ObservableProperty] private string _coordinates = "";
    [ObservableProperty] private Bitmap? _thumbnail;

    public double? Latitude  { get; private set; }
    public double? Longitude { get; private set; }

    public DateTime? DateTakenSortKey { get; private set; }
    public int RatingValue { get; private set; }

    // Full constructor — metadata available immediately
    public PhotoViewModel(PhotoMetadata m)
    {
        FilePath = m.FilePath;
        ApplyMetadata(m);
    }

    // Lazy constructor for recursive search results — shows filename instantly,
    // loads EXIF in background
    public PhotoViewModel(string filePath, PhotoScannerService scanner)
    {
        FilePath  = filePath;
        _fileName = Path.GetFileName(filePath);
        Task.Run(() =>
        {
            try
            {
                var m = scanner.ReadMetadata(filePath);
                Dispatcher.UIThread.Post(() => ApplyMetadata(m));
            }
            catch { }
        });
    }

    private void ApplyMetadata(PhotoMetadata m)
    {
        FileName         = m.FileName;
        DateTakenSortKey = m.DateTaken;
        RatingValue      = m.Rating;
        DateTaken        = m.DateTaken?.ToString("yyyy-MM-dd  HH:mm") ?? "No date";
        RatingStars      = m.Rating > 0
            ? new string('★', m.Rating) + new string('☆', 5 - m.Rating)
            : "☆☆☆☆☆";
        Latitude  = m.Latitude;
        Longitude = m.Longitude;
        if (m.Latitude.HasValue && m.Longitude.HasValue)
            Coordinates = m.Country is not null
                ? m.Country
                : $"{m.Latitude.Value.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°,  {m.Longitude.Value.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}°";
        else
            Coordinates = "";
    }

    // Called from MainWindowViewModel once the country is resolved from DB / Nominatim.
    public void SetCountry(string country)
    {
        if (Latitude.HasValue && Longitude.HasValue)
            Coordinates = country;
    }

    // Dispose old bitmap when thumbnail is replaced on the same VM
    partial void OnThumbnailChanging(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    public void Dispose() => Thumbnail = null; // OnThumbnailChanging disposes the old bitmap

    public void LoadThumbnailAsync() => Task.Run(() =>
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var bmp = Bitmap.DecodeToWidth(stream, 180);
            Dispatcher.UIThread.Post(() => Thumbnail = bmp);
        }
        catch { }
    });

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{FilePath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
