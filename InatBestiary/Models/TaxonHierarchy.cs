namespace InatBestiary.Models;

public sealed record TaxonRank(string Rank, string ScientificName, string? CommonName);

public sealed record TaxonHierarchy(
    int TaxonId,
    string ScientificName,
    string CommonName,
    IReadOnlyList<TaxonRank> Ancestors)
{
    // Flat keywords: specific → broad, both common and scientific names included
    public IReadOnlyList<string> Keywords
    {
        get
        {
            var list = new List<string> { CommonName, ScientificName };
            foreach (var a in Ancestors.Reverse())
            {
                if (!string.IsNullOrEmpty(a.CommonName) && a.CommonName != a.ScientificName)
                    list.Add(a.CommonName);
                if (!string.IsNullOrEmpty(a.ScientificName))
                    list.Add(a.ScientificName);
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // "Animalia|Chordata|Aves|Coraciiformes|Alcedinidae|Alcedo|Alcedo atthis|Common Kingfisher"
    public string HierarchicalKeyword
    {
        get
        {
            var parts = Ancestors
                .Select(a => a.ScientificName)
                .Append(ScientificName)
                .Append(CommonName)
                .Where(s => !string.IsNullOrEmpty(s));
            return string.Join("|", parts);
        }
    }
}
