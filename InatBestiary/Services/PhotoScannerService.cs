using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using InatBestiary.Models;

namespace InatBestiary.Services;

public class PhotoScannerService
{
    private static readonly HashSet<string> ImageExtensions =
    [
        ".jpg", ".jpeg", ".tiff", ".tif", ".heic", ".heif",
        ".arw", ".cr2", ".cr3", ".nef", ".dng", ".raf", ".orf"
    ];

    public bool IsImageFile(string filePath) =>
        ImageExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    public IEnumerable<string> GetImageFiles(string folderPath) =>
        System.IO.Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

    public PhotoMetadata ReadMetadata(string filePath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(filePath);
            var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0   = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var gps    = dirs.OfType<GpsDirectory>().FirstOrDefault();
            var xmp    = dirs.OfType<XmpDirectory>().FirstOrDefault();

            DateTime? dateTaken = null;
            if (subIfd?.TryGetDateTime(ExifSubIfdDirectory.TagDateTimeOriginal, out var dt) == true)
                dateTaken = dt;

            int rating = ReadRating(ifd0, xmp);

            double? lat = null, lon = null;
            if (gps is not null)
            {
                var loc = gps.GetGeoLocation();
                if (loc != null && Math.Abs(loc.Latitude) <= 90 && Math.Abs(loc.Longitude) <= 180)
                {
                    lat = loc.Latitude;
                    lon = loc.Longitude;
                }
                else
                {
                    // GetGeoLocation() returns null when LatitudeRef/LongitudeRef tags are
                    // absent (some cameras omit them). Read the rational triplets directly.
                    try
                    {
                        var latR = gps.GetRationalArray(GpsDirectory.TagLatitude);
                        var lonR = gps.GetRationalArray(GpsDirectory.TagLongitude);
                        if (latR?.Length >= 3 && lonR?.Length >= 3)
                        {
                            lat = DmsRationalsToDecimal(latR);
                            lon = DmsRationalsToDecimal(lonR);
                            if (gps.GetString(GpsDirectory.TagLatitudeRef)
                                    ?.StartsWith("S", StringComparison.OrdinalIgnoreCase) == true)
                                lat = -lat;
                            if (gps.GetString(GpsDirectory.TagLongitudeRef)
                                    ?.StartsWith("W", StringComparison.OrdinalIgnoreCase) == true)
                                lon = -lon;
                        }
                    }
                    catch { }
                }
            }

            // MetadataExtractor XMP properties (handles exif:GPSLatitude/Longitude in XMP segment)
            if ((lat is null || lon is null) && xmp?.GetXmpProperties() is { } xmpProps)
            {
                if (xmpProps.TryGetValue("exif:GPSLatitude", out var xmpLatStr))
                    lat ??= JpegXmpWriter.ParseXmpCoord(xmpLatStr);
                if (xmpProps.TryGetValue("exif:GPSLongitude", out var xmpLonStr))
                    lon ??= JpegXmpWriter.ParseXmpCoord(xmpLonStr);
            }

            // Direct XMP segment parse as final fallback
            if (lat is null || lon is null)
            {
                var (xmpLat, xmpLon) = JpegXmpWriter.ReadGpsFromXmp(filePath);
                lat ??= xmpLat;
                lon ??= xmpLon;
            }

            // Discard physically impossible values (malformed EXIF/XMP from some cameras)
            if (lat.HasValue && Math.Abs(lat.Value) > 90)  lat = null;
            if (lon.HasValue && Math.Abs(lon.Value) > 180) lon = null;

            return new PhotoMetadata(filePath, Path.GetFileName(filePath), dateTaken, rating, lat, lon);
        }
        catch
        {
            return new PhotoMetadata(filePath, Path.GetFileName(filePath), null, 0, null, null);
        }
    }

    // Diagnostic: returns a compact string describing what GPS data MetadataExtractor finds.
    public string DiagnoseGps(string filePath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(filePath);
            var gps  = dirs.OfType<GpsDirectory>().FirstOrDefault();
            var xmp  = dirs.OfType<XmpDirectory>().FirstOrDefault();

            var gpsInfo = "gps=null";
            if (gps is not null)
            {
                var loc  = gps.GetGeoLocation();
                var latR = gps.GetRationalArray(GpsDirectory.TagLatitude);
                var latRef = gps.GetString(GpsDirectory.TagLatitudeRef) ?? "?";
                gpsInfo = $"gps: loc={(loc is null ? "null" : $"{loc.Latitude:F3}")} " +
                          $"latR={(latR is null ? "null" : latR.Length.ToString())} " +
                          $"ref={latRef}";
            }

            var xmpInfo = "xmp=null";
            if (xmp is not null)
            {
                var props = xmp.GetXmpProperties();
                string? rawGps = null;
                props?.TryGetValue("exif:GPSLatitude", out rawGps);
                xmpInfo = $"xmp: keys={props?.Count ?? 0} " +
                          $"hasGps={props?.ContainsKey("exif:GPSLatitude") == true} " +
                          $"val={rawGps ?? "null"}";
            }

            return $"{gpsInfo} | {xmpInfo}";
        }
        catch (Exception ex) { return $"ex:{ex.GetType().Name}:{ex.Message}"; }
    }

    // Returns true if the file already has photocatalog taxonomy XMP tags.
    // Uses MetadataExtractor (same parser as ReadMetadata) with JpegXmpWriter as fallback.
    public bool HasTaxonomyTags(string filePath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(filePath);
            var xmp = dirs.OfType<XmpDirectory>().FirstOrDefault();
            if (xmp?.GetXmpProperties() is { } props && props.ContainsKey("photocatalog:TaxonId"))
                return true;
        }
        catch { }
        return JpegXmpWriter.HasTaxonomyTags(filePath);
    }

    // Some cameras store GPS seconds as a large integer without a proper fractional denominator
    // (e.g., {163349, 1} meaning 16.3349 seconds, not 163349 seconds).
    // Detect implausible seconds (> 60) and scale down by the smallest power of 10 that fits.
    private static double DmsRationalsToDecimal(MetadataExtractor.Rational[] r)
    {
        var deg = r[0].ToDouble();
        var min = r[1].ToDouble();
        var sec = r[2].ToDouble();

        if (sec > 60)
            for (var f = 10.0; f <= 1e8; f *= 10)
                if (sec / f < 60) { sec /= f; break; }

        return deg + min / 60.0 + sec / 3600.0;
    }

    private static int ReadRating(ExifIfd0Directory? ifd0, XmpDirectory? xmp)
    {
        // Microsoft EXIF rating extension tag (0x4746): values 0-5
        if (ifd0?.TryGetInt32(0x4746, out var exifRating) == true && exifRating > 0)
            return Math.Clamp(exifRating, 0, 5);

        // XMP xmp:Rating field
        if (xmp?.GetXmpProperties() is { } props &&
            props.TryGetValue("xmp:Rating", out var ratingStr) &&
            int.TryParse(ratingStr, out var xmpRating) && xmpRating > 0)
            return Math.Clamp(xmpRating, 0, 5);

        return 0;
    }
}
