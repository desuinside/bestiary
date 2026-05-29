using System.Text;
using System.Xml.Linq;
using InatBestiary.Models;

namespace InatBestiary.Services;

// Reads and writes XMP metadata inside JPEG APP1 segments.
// Both Write (taxonomy) and WriteGps preserve all other existing XMP fields.
public static class JpegXmpWriter
{
    private static readonly byte[] XmpPrefix =
        Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    // ── Public API ────────────────────────────────────────────────────────────

    public static void Write(string filePath, TaxonHierarchy hierarchy) =>
        UpdateXmp(filePath, doc => ApplyTaxonomy(doc, hierarchy), ".xmp_tmp");

    public static void WriteGps(string filePath, double latitude, double longitude) =>
        UpdateXmp(filePath, doc => ApplyGps(doc, latitude, longitude), ".gps_tmp");

    // ── Core: read → mutate → write ──────────────────────────────────────────

    private static void UpdateXmp(string filePath, Action<XDocument> mutate, string tmpSuffix)
    {
        var jpeg = File.ReadAllBytes(filePath);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Not a valid JPEG file.");

        var (segments, bodyStart) = ParseHeaderSegments(jpeg);
        var existingXmp = segments.FirstOrDefault(IsXmpApp1);

        XDocument doc;
        if (existingXmp is not null)
        {
            var xml = Encoding.UTF8.GetString(existingXmp.Payload, XmpPrefix.Length,
                                              existingXmp.Payload.Length - XmpPrefix.Length);
            try   { doc = XDocument.Parse(xml); }
            catch { doc = CreateEmptyDoc(); }
        }
        else doc = CreateEmptyDoc();

        mutate(doc);
        segments.RemoveAll(IsXmpApp1);

        var xmpPayload = ToXmpPayload(doc);
        if (xmpPayload.Length + 2 > 65535)
            throw new InvalidOperationException("XMP data too large for a single JPEG APP1 segment.");

        int insertAt = segments.FindIndex(s => s.Marker == 0xE1 && !IsXmpApp1(s));
        segments.Insert(insertAt < 0 ? 0 : insertAt + 1, new Segment(0xE1, xmpPayload));

        var tempPath = filePath + tmpSuffix;
        try
        {
            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            fs.WriteByte(0xFF); fs.WriteByte(0xD8);
            foreach (var seg in segments)
            {
                int len = seg.Payload.Length + 2;
                fs.WriteByte(0xFF); fs.WriteByte(seg.Marker);
                fs.WriteByte((byte)(len >> 8)); fs.WriteByte((byte)(len & 0xFF));
                fs.Write(seg.Payload);
            }
            fs.Write(jpeg, bodyStart, jpeg.Length - bodyStart);
        }
        catch { try { File.Delete(tempPath); } catch { } throw; }

        File.Move(tempPath, filePath, overwrite: true);
    }

    // ── XMP field updaters ────────────────────────────────────────────────────

    private static void ApplyTaxonomy(XDocument doc, TaxonHierarchy h)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace dc  = "http://purl.org/dc/elements/1.1/";
        XNamespace lr  = "http://ns.adobe.com/lightroom/1.0/";
        XNamespace pc  = "http://photocatalog.app/ns/1.0/";

        var desc = GetOrCreateDescription(doc);
        EnsureNs(desc, "dc",           dc.NamespaceName);
        EnsureNs(desc, "lr",           lr.NamespaceName);
        EnsureNs(desc, "photocatalog", pc.NamespaceName);

        // Preserve any existing flat keywords; union with new taxonomy keywords.
        var existingKw = desc.Element(dc + "subject")
            ?.Descendants(rdf + "li")
            .Select(e => e.Value) ?? [];
        var mergedKw = new HashSet<string>(existingKw, StringComparer.OrdinalIgnoreCase);
        foreach (var kw in h.Keywords) mergedKw.Add(kw);

        desc.Element(dc + "subject")?.Remove();
        desc.Add(new XElement(dc + "subject",
            new XElement(rdf + "Bag",
                mergedKw.Select(kw => new XElement(rdf + "li", kw)))));

        // Preserve any existing hierarchical subjects; add ours if not already present.
        var existingHier = desc.Element(lr + "hierarchicalSubject")
            ?.Descendants(rdf + "li")
            .Select(e => e.Value) ?? [];
        var mergedHier = new HashSet<string>(existingHier, StringComparer.OrdinalIgnoreCase)
            { h.HierarchicalKeyword };

        desc.Element(lr + "hierarchicalSubject")?.Remove();
        desc.Add(new XElement(lr + "hierarchicalSubject",
            new XElement(rdf + "Bag",
                mergedHier.Select(kw => new XElement(rdf + "li", kw)))));

        // photocatalog-specific fields are ours exclusively — always replace.
        desc.Element(pc + "TaxonId")?.Remove();
        desc.Element(pc + "ScientificName")?.Remove();
        desc.Add(new XElement(pc + "TaxonId",       h.TaxonId));
        desc.Add(new XElement(pc + "ScientificName", h.ScientificName));
    }

    private static void ApplyGps(XDocument doc, double lat, double lon)
    {
        XNamespace exif = "http://ns.adobe.com/exif/1.0/";
        var desc = GetOrCreateDescription(doc);
        EnsureNs(desc, "exif", exif.NamespaceName);

        desc.Attribute(exif + "GPSLatitude")?.Remove();
        desc.Attribute(exif + "GPSLongitude")?.Remove();
        desc.Element(exif + "GPSLatitude")?.Remove();
        desc.Element(exif + "GPSLongitude")?.Remove();

        desc.Add(new XElement(exif + "GPSLatitude",  ToXmpCoord(lat, isLatitude: true)));
        desc.Add(new XElement(exif + "GPSLongitude", ToXmpCoord(lon, isLatitude: false)));
    }

    // ── XDocument helpers ─────────────────────────────────────────────────────

    private static XDocument CreateEmptyDoc()
    {
        XNamespace x   = "adobe:ns:meta/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        return new XDocument(
            new XElement(x + "xmpmeta",
                new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
                new XElement(rdf + "RDF",
                    new XAttribute(XNamespace.Xmlns + "rdf", rdf.NamespaceName),
                    new XElement(rdf + "Description",
                        new XAttribute(rdf + "about", "")))));
    }

    private static XElement GetOrCreateDescription(XDocument doc)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var rdfEl = doc.Descendants(rdf + "RDF").FirstOrDefault();
        if (rdfEl is null)
        {
            rdfEl = new XElement(rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", rdf.NamespaceName));
            doc.Root?.Add(rdfEl);
        }
        var desc = rdfEl.Elements(rdf + "Description").FirstOrDefault();
        if (desc is null)
        {
            desc = new XElement(rdf + "Description", new XAttribute(rdf + "about", ""));
            rdfEl.Add(desc);
        }
        return desc;
    }

    private static void EnsureNs(XElement el, string prefix, string ns)
    {
        if (el.Attribute(XNamespace.Xmlns + prefix) is null)
            el.Add(new XAttribute(XNamespace.Xmlns + prefix, ns));
    }

    private static byte[] ToXmpPayload(XDocument doc)
    {
        // Serialize the element tree only (skip old xpacket PIs; we add our own)
        var content = string.Concat(
            doc.Nodes()
               .Where(n => n is not XProcessingInstruction)
               .Select(n => n.ToString()));

        var sb = new StringBuilder();
        sb.AppendLine("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.AppendLine(content);
        sb.Append("<?xpacket end=\"w\"?>");

        var xmlBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var payload  = new byte[XmpPrefix.Length + xmlBytes.Length];
        XmpPrefix.CopyTo(payload, 0);
        xmlBytes.CopyTo(payload, XmpPrefix.Length);
        return payload;
    }

    // ── Public read helpers ───────────────────────────────────────────────────

    // Reads GPS from the XMP segment of a JPEG (bypasses MetadataExtractor).
    // Returns (null, null) if no XMP GPS is present or the file can't be parsed.
    public static (double? Latitude, double? Longitude) ReadGpsFromXmp(string filePath)
    {
        try
        {
            var jpeg = File.ReadAllBytes(filePath);
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return (null, null);

            var (segments, _) = ParseHeaderSegments(jpeg);
            var xmpSeg = segments.FirstOrDefault(IsXmpApp1);
            if (xmpSeg is null) return (null, null);

            var xml = Encoding.UTF8.GetString(xmpSeg.Payload, XmpPrefix.Length,
                                              xmpSeg.Payload.Length - XmpPrefix.Length);
            var doc = XDocument.Parse(xml);
            XNamespace exif = "http://ns.adobe.com/exif/1.0/";
            XNamespace rdf  = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

            double? lat = null, lon = null;
            foreach (var desc in doc.Descendants(rdf + "Description"))
            {
                lat ??= ParseXmpCoord(desc.Attribute(exif + "GPSLatitude")?.Value
                                   ?? desc.Element(exif + "GPSLatitude")?.Value);
                lon ??= ParseXmpCoord(desc.Attribute(exif + "GPSLongitude")?.Value
                                   ?? desc.Element(exif + "GPSLongitude")?.Value);
                if (lat.HasValue && lon.HasValue) break;
            }
            return (lat, lon);
        }
        catch { return (null, null); }
    }

    // Returns true if the file already has photocatalog taxonomy XMP tags.
    public static bool HasTaxonomyTags(string filePath)
    {
        try
        {
            var jpeg = File.ReadAllBytes(filePath);
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;

            var (segments, _) = ParseHeaderSegments(jpeg);
            var xmpSeg = segments.FirstOrDefault(IsXmpApp1);
            if (xmpSeg is null) return false;

            var xml = Encoding.UTF8.GetString(xmpSeg.Payload, XmpPrefix.Length,
                                              xmpSeg.Payload.Length - XmpPrefix.Length);
            var doc = XDocument.Parse(xml);
            XNamespace pc = "http://photocatalog.app/ns/1.0/";
            return doc.Descendants(pc + "TaxonId").Any();
        }
        catch { return false; }
    }

    // ── Coordinate formatting ─────────────────────────────────────────────────

    // XMP GPSCoordinate format: "DD,MM.mmmmmH"
    private static string ToXmpCoord(double degrees, bool isLatitude)
    {
        string hem = isLatitude ? (degrees >= 0 ? "N" : "S") : (degrees >= 0 ? "E" : "W");
        double abs = Math.Abs(degrees);
        int    deg = (int)abs;
        double min = (abs - deg) * 60.0;
        return $"{deg},{min:F6}{hem}";
    }

    // Parses XMP GPS coordinate strings back to decimal degrees.
    // Handles both "DD,MM.mmmmmmH" (decimal-minutes) and "DD,MM,SS.sssH" (DMS) formats.
    public static double? ParseXmpCoord(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        char hem = value[^1];
        if (!"NSEWnsew".Contains(hem)) return null;
        var parts = value[..^1].Split(',');
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var fl = System.Globalization.NumberStyles.Float;

        double result;
        if (parts.Length == 2)
        {
            // DD,MM.mmmmmmH
            if (!double.TryParse(parts[0], fl, ic, out var deg)) return null;
            if (!double.TryParse(parts[1], fl, ic, out var min)) return null;
            result = deg + min / 60.0;
        }
        else if (parts.Length == 3)
        {
            // DD,MM,SS.sssH — some cameras write seconds as a large integer
            // without a proper fractional denominator (e.g. 163349 instead of 16.3349).
            if (!double.TryParse(parts[0], fl, ic, out var deg)) return null;
            if (!double.TryParse(parts[1], fl, ic, out var min)) return null;
            if (!double.TryParse(parts[2], fl, ic, out var sec)) return null;
            if (sec > 60)
                for (var f = 10.0; f <= 1e8; f *= 10)
                    if (sec / f < 60) { sec /= f; break; }
            result = deg + min / 60.0 + sec / 3600.0;
        }
        else return null;

        return (hem == 'S' || hem == 'W') ? -result : result;
    }

    // ── JPEG segment parsing ──────────────────────────────────────────────────

    private static (List<Segment> segments, int bodyStart) ParseHeaderSegments(byte[] jpeg)
    {
        var segments = new List<Segment>();
        int pos = 2;

        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw new InvalidDataException($"Expected 0xFF marker at byte {pos}.");

            // Skip 0xFF fill bytes (valid JPEG padding before a marker byte)
            while (pos + 1 < jpeg.Length && jpeg[pos + 1] == 0xFF)
                pos++;
            if (pos + 1 >= jpeg.Length) break;

            byte marker = jpeg[pos + 1];
            pos += 2;

            if (marker == 0xDA) return (segments, pos - 2);
            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            if (pos + 1 >= jpeg.Length)
                throw new InvalidDataException("Unexpected end of file in JPEG segment.");

            int len = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (len < 2) throw new InvalidDataException($"Invalid segment length {len}.");

            var payload = new byte[len - 2];
            Array.Copy(jpeg, pos + 2, payload, 0, Math.Min(len - 2, jpeg.Length - pos - 2));
            segments.Add(new Segment(marker, payload));
            pos += len;
        }

        throw new InvalidDataException("No SOS marker found; file may be truncated.");
    }

    private static bool IsXmpApp1(Segment s) =>
        s.Marker == 0xE1 &&
        s.Payload.Length >= XmpPrefix.Length &&
        s.Payload.AsSpan(0, XmpPrefix.Length).SequenceEqual(XmpPrefix);

    private record Segment(byte Marker, byte[] Payload);
}
