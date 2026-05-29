namespace InatBestiary.Models;

public sealed record TaxonSuggestion(
    int Id,
    string DisplayName,
    string ScientificName,
    string? IconicTaxon,
    string? Rank     = null,
    string? Ancestry = null)
{
    public string SubLine => $"{ScientificName}  ·  {RankLabel}  ·  {GroupLabel}";

    private string RankLabel => Rank switch
    {
        "species"    => "species",
        "genus"      => "genus",
        "family"     => "family",
        "order"      => "order",
        "class"      => "class",
        "phylum"     => "phylum",
        not null     => Rank,
        _            => "",
    };

    private string GroupLabel => IconicTaxon switch
    {
        "Aves"      => "Bird",
        "Mammalia"  => "Mammal",
        "Amphibia"  => "Amphibian",
        "Reptilia"  => "Reptile",
        "Insecta"   => "Insect",
        "Arachnida" => "Arachnid",
        "Mollusca"  => "Mollusc",
        _           => "Invertebrate",
    };

    // AutoCompleteBox sets Text = item.ToString() on selection
    public override string ToString() => DisplayName;
}
