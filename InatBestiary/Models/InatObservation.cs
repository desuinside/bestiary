namespace InatBestiary.Models;

// One iNaturalist observation with enough data to match a local photo and get GPS.
public sealed record InatObservation(
    int    Id,
    DateTimeOffset? ObservedAt,   // null if only a date was recorded (no time)
    DateOnly        ObservedOn,
    double?         Latitude,
    double?         Longitude,
    string?         PlaceGuess);  // human-readable location from iNat ("Horsens, Denmark")
