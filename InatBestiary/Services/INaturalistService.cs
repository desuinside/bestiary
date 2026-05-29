using System.Text.Json;
using InatBestiary.Models;

namespace InatBestiary.Services;

public class INaturalistService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly HashSet<string> AllowedTaxa =
    [
        "Aves", "Mammalia", "Amphibia", "Reptilia",
        "Insecta", "Arachnida", "Mollusca", "Animalia",
    ];

    public async Task<IReadOnlyList<TaxonSuggestion>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var url = "https://api.inaturalist.org/v1/taxa/autocomplete" +
                  $"?q={Uri.EscapeDataString(query)}&per_page=20&locale=en";
        try
        {
            var json = await Http.GetStringAsync(url, ct);
            return Parse(json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    // Fetches the full taxonomy hierarchy for a known taxon ID.
    // Used by the tagging feature to build the ancestor chain for writing to XMP.
    public async Task<TaxonHierarchy?> GetTaxonHierarchyAsync(int taxonId, CancellationToken ct)
    {
        var url = $"https://api.inaturalist.org/v1/taxa/{taxonId}";
        try
        {
            var json = await Http.GetStringAsync(url, ct);
            return ParseHierarchy(json);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static readonly HashSet<string> HierarchyRanks = new(StringComparer.OrdinalIgnoreCase)
        { "kingdom", "phylum", "class", "order", "family", "genus" };

    private static TaxonHierarchy? ParseHierarchy(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

        var arr = results.EnumerateArray().ToList();
        if (arr.Count == 0) return null;
        var item = arr[0];

        var id     = item.GetProperty("id").GetInt32();
        var sci    = item.GetProperty("name").GetString() ?? "";
        var common = item.TryGetProperty("preferred_common_name", out var cn) ? cn.GetString() ?? sci : sci;

        var ancestors = new List<TaxonRank>();
        if (item.TryGetProperty("ancestors", out var anc))
        {
            foreach (var a in anc.EnumerateArray())
            {
                var rank = a.TryGetProperty("rank", out var rk) ? rk.GetString() ?? "" : "";
                if (!HierarchyRanks.Contains(rank)) continue;
                var aName   = a.GetProperty("name").GetString() ?? "";
                var aCommon = a.TryGetProperty("preferred_common_name", out var acn) ? acn.GetString() : null;
                ancestors.Add(new TaxonRank(rank, aName, aCommon));
            }
        }

        return new TaxonHierarchy(id, sci, common, ancestors);
    }

    // Used by auto-sync: looks for an exact common-name match for a folder name.
    public async Task<TaxonSuggestion?> FindExactMatchAsync(string name, CancellationToken ct)
    {
        var results = await SearchAsync(name, ct);

        // 1. Exact common-name match ("Brown Hare" == "Brown Hare")
        var match = results.FirstOrDefault(r =>
            string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        // 2. Common name ends with folder name
        //    e.g. folder "Brown Hare" → iNat "European Brown Hare"
        return results.FirstOrDefault(r =>
            r.DisplayName.EndsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    // Fetches all observations by a user for a given taxon (follows pages up to 10000 results).
    public async Task<IReadOnlyList<InatObservation>> GetUserObservationsAsync(
        string username, int taxonId, CancellationToken ct)
    {
        var list = new List<InatObservation>();
        int page = 1;
        int total;
        do
        {
            var url = $"https://api.inaturalist.org/v1/observations" +
                      $"?user_login={Uri.EscapeDataString(username)}" +
                      $"&taxon_id={taxonId}&per_page=200&page={page}" +
                      $"&order=desc&order_by=observed_on";
            try
            {
                var json = await Http.GetStringAsync(url, ct);
                (var batch, total) = ParseObservations(json);
                list.AddRange(batch);
                page++;
            }
            catch (OperationCanceledException) { throw; }
            catch { break; }
        }
        while (list.Count < total && list.Count < 10000);

        return list;
    }

    private static (List<InatObservation> results, int total) ParseObservations(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        int total = root.TryGetProperty("total_results", out var tr) ? tr.GetInt32() : 0;
        var list  = new List<InatObservation>();

        if (!root.TryGetProperty("results", out var results)) return (list, total);

        foreach (var item in results.EnumerateArray())
        {
            // Location: "lat,lon" string
            double? lat = null, lon = null;
            if (item.TryGetProperty("location", out var loc) &&
                loc.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var parts = loc.GetString()!.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var la) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var lo))
                { lat = la; lon = lo; }
            }

            // Date
            DateOnly date = default;
            if (item.TryGetProperty("observed_on", out var od) &&
                od.ValueKind == System.Text.Json.JsonValueKind.String &&
                DateOnly.TryParse(od.GetString(), out var d))
                date = d;

            // Time (ISO 8601 with offset)
            DateTimeOffset? observedAt = null;
            if (item.TryGetProperty("time_observed_at", out var ta) &&
                ta.ValueKind == System.Text.Json.JsonValueKind.String &&
                DateTimeOffset.TryParse(ta.GetString(), out var dto))
                observedAt = dto;

            int id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
            string? placeGuess = item.TryGetProperty("place_guess", out var pg) &&
                                 pg.ValueKind == JsonValueKind.String ? pg.GetString() : null;
            list.Add(new InatObservation(id, observedAt, date, lat, lon, placeGuess));
        }

        return (list, total);
    }

    private static List<TaxonSuggestion> Parse(string json)
    {
        var list = new List<TaxonSuggestion>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results))
            return list;

        foreach (var item in results.EnumerateArray())
        {
            var iconic    = item.TryGetProperty("iconic_taxon_name", out var it) ? it.GetString() : null;
            if (iconic is null || !AllowedTaxa.Contains(iconic)) continue;

            var id       = item.GetProperty("id").GetInt32();
            var sci      = item.GetProperty("name").GetString() ?? "";
            var common   = item.TryGetProperty("preferred_common_name", out var cn) ? cn.GetString() : null;
            var rank     = item.TryGetProperty("rank",     out var rk) ? rk.GetString() : null;
            var ancestry = item.TryGetProperty("ancestry", out var an) ? an.GetString() : null;

            list.Add(new TaxonSuggestion(id, common ?? sci, sci, iconic, rank, ancestry));
        }

        return list;
    }
}
