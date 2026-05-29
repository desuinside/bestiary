using Microsoft.Data.Sqlite;
using InatBestiary.Models;

namespace InatBestiary.Services;

public sealed class CatalogDatabase : IAsyncDisposable
{
    private SqliteConnection? _conn;

    public async Task OpenAsync(string catalogRoot)
    {
        var path = Path.Combine(catalogRoot, ".bestiary.db");
        _conn = new SqliteConnection($"Data Source={path}");
        await _conn.OpenAsync();
        await InitSchemaAsync();
    }

    private async Task InitSchemaAsync()
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS taxa (
                id          INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                common_name TEXT,
                rank        TEXT,
                iconic      TEXT,
                ancestry    TEXT
            );
            CREATE TABLE IF NOT EXISTS folder_taxa (
                folder_path  TEXT PRIMARY KEY,
                taxon_id     INTEGER NOT NULL REFERENCES taxa(id),
                auto_matched INTEGER DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS location_cache (
                lat_key  INTEGER NOT NULL,
                lon_key  INTEGER NOT NULL,
                country  TEXT,
                PRIMARY KEY (lat_key, lon_key)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task StoreTaxonAsync(TaxonSuggestion t)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO taxa(id, name, common_name, rank, iconic, ancestry)
            VALUES (@id, @name, @common, @rank, @iconic, @ancestry)
            """;
        cmd.Parameters.AddWithValue("@id",       t.Id);
        cmd.Parameters.AddWithValue("@name",     t.ScientificName);
        cmd.Parameters.AddWithValue("@common",   (object?)t.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rank",     (object?)t.Rank        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@iconic",   (object?)t.IconicTaxon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ancestry", (object?)t.Ancestry    ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task StoreFolderTaxonAsync(string folderPath, int taxonId, bool auto)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO folder_taxa(folder_path, taxon_id, auto_matched)
            VALUES (@path, @taxon, @auto)
            """;
        cmd.Parameters.AddWithValue("@path",  folderPath);
        cmd.Parameters.AddWithValue("@taxon", taxonId);
        cmd.Parameters.AddWithValue("@auto",  auto ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    // Returns folder paths whose mapped taxon IS the given id OR is a descendant of it.
    // iNaturalist ancestry format: "48460/1/2/355675/3/26727/..."
    public async Task<IReadOnlyList<string>> FindFoldersByAncestorAsync(int ancestorId)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT ft.folder_path
            FROM folder_taxa ft
            JOIN taxa t ON ft.taxon_id = t.id
            WHERE t.id = @id
               OR ('/' || COALESCE(t.ancestry, '') || '/') LIKE ('%/' || @id || '/%')
            """;
        cmd.Parameters.AddWithValue("@id", ancestorId);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<HashSet<string>> GetAllMappedFoldersAsync()
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT folder_path FROM folder_taxa";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            set.Add(reader.GetString(0));
        return set;
    }

    // Returns all mapped folders that ARE folderPath or are nested inside it.
    // Uses SUBSTR prefix matching to avoid LIKE wildcard issues with special chars in paths.
    public async Task<IReadOnlyList<(string FolderPath, int TaxonId)>> GetMappedSubfoldersAsync(string folderPath)
    {
        var normalized = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix     = normalized + Path.DirectorySeparatorChar;

        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT folder_path, taxon_id FROM folder_taxa
            WHERE folder_path = @path
               OR (LENGTH(folder_path) > @prefixLen
                   AND SUBSTR(folder_path, 1, @prefixLen) = @prefix)
            """;
        cmd.Parameters.AddWithValue("@path",      normalized);
        cmd.Parameters.AddWithValue("@prefix",    prefix);
        cmd.Parameters.AddWithValue("@prefixLen", prefix.Length);

        var list = new List<(string, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add((reader.GetString(0), (int)reader.GetInt64(1)));
        return list;
    }

    // Returns distinct taxon IDs whose common_name or scientific name contains the query.
    public async Task<IReadOnlyList<int>> FindTaxonIdsByNameAsync(string query)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT id FROM taxa
            WHERE common_name LIKE @q OR name LIKE @q
            """;
        cmd.Parameters.AddWithValue("@q", "%" + query + "%");

        var list = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add((int)reader.GetInt64(0));
        return list;
    }

    public async Task<int?> GetFolderTaxonIdAsync(string folderPath)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT taxon_id FROM folder_taxa WHERE folder_path = @path";
        cmd.Parameters.AddWithValue("@path", folderPath);
        var result = await cmd.ExecuteScalarAsync();
        return result is long id ? (int)id : null;
    }

    public async Task<bool> IsFolderMappedAsync(string folderPath)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM folder_taxa WHERE folder_path = @path";
        cmd.Parameters.AddWithValue("@path", folderPath);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    // Keys use 1 decimal place (~11 km grid) — enough for country-level deduplication.
    private static (long lat, long lon) LocationKey(double lat, double lon) =>
        ((long)(lat * 10), (long)(lon * 10));

    public async Task<string?> GetCachedCountryAsync(double lat, double lon)
    {
        var (lk, lnk) = LocationKey(lat, lon);
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT country FROM location_cache WHERE lat_key=@lat AND lon_key=@lon";
        cmd.Parameters.AddWithValue("@lat", lk);
        cmd.Parameters.AddWithValue("@lon", lnk);
        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? s : null;
    }

    public async Task CacheCountryAsync(double lat, double lon, string country)
    {
        var (lk, lnk) = LocationKey(lat, lon);
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO location_cache(lat_key, lon_key, country)
            VALUES (@lat, @lon, @country)
            """;
        cmd.Parameters.AddWithValue("@lat",     lk);
        cmd.Parameters.AddWithValue("@lon",     lnk);
        cmd.Parameters.AddWithValue("@country", country);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is null) return;
        await _conn.DisposeAsync();
        _conn = null;
    }
}
