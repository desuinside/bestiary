namespace InatBestiary.Models;

public record PhotoMetadata(
    string FilePath,
    string FileName,
    DateTime? DateTaken,
    int Rating,
    double? Latitude,
    double? Longitude,
    string? Country = null);
