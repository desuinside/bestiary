using System.Globalization;
using System.Text.Json;

namespace InatBestiary.Services;

public class GeocodingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    static GeocodingService()
    {
        // Nominatim usage policy requires a descriptive User-Agent
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("InatBestiary/1.0");
    }

    // Returns the location name for the given coordinates, or null when Nominatim has no result.
    // Throws on network/HTTP errors or invalid coordinates; caller handles those.
    // zoom=3 asks for country-level precision (fast, small payload).
    // Format: "Denmark (Danmark)" when English and local names differ, otherwise just "Denmark".
    public async Task<string?> GetCountryAsync(double lat, double lon, CancellationToken ct)
    {
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            throw new ArgumentOutOfRangeException("coordinates",
                $"GPS coordinates out of range: lat={lat:F5}, lon={lon:F5}");

        var url = "https://nominatim.openstreetmap.org/reverse" +
                  $"?format=json&zoom=3&accept-language=en&namedetails=1" +
                  $"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&lon={lon.ToString(CultureInfo.InvariantCulture)}";

        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        string? englishName = null;
        if (doc.RootElement.TryGetProperty("address", out var addr) &&
            addr.TryGetProperty("country", out var country))
            englishName = country.GetString();

        if (englishName is null) return null;

        string? localName = null;
        if (doc.RootElement.TryGetProperty("namedetails", out var nd) &&
            nd.TryGetProperty("name", out var name))
            localName = name.GetString();

        if (localName is null || string.Equals(englishName, localName, StringComparison.OrdinalIgnoreCase))
            return englishName;

        return $"{englishName} ({localName})";
    }
}
