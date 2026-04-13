using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageTagger.Models;

namespace ImageTagger.Services;

public sealed class CacheService
{
    private string _cacheFolder;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    public CacheService(string cacheFolder = "")
    {
        _cacheFolder = ResolveFolder(cacheFolder);
    }

    public void SetCacheFolder(string folder) =>
        _cacheFolder = ResolveFolder(folder);

    public string CurrentCacheFolder => _cacheFolder;

    // ── Cache file location ───────────────────────────────────────────────────

    /// <summary>
    /// Derives the cache-file path for <paramref name="sourceFolder"/>:
    /// <c>{cacheFolder}/{md5(normalized_path)}.json</c>
    /// </summary>
    public string GetCacheFilePath(string sourceFolder)
    {
        var normalized = Path.GetFullPath(sourceFolder)
                             .TrimEnd(Path.DirectorySeparatorChar,
                                      Path.AltDirectorySeparatorChar)
                             .ToLowerInvariant();
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(_cacheFolder, $"{hash}.json");
    }

    // ── Load / Save ───────────────────────────────────────────────────────────

    public CacheFile LoadCache(string sourceFolder)
    {
        var path = GetCacheFilePath(sourceFolder);
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var obj  = JsonSerializer.Deserialize<CacheFile>(json, _jsonOpts);
                if (obj != null) return obj;
            }
        }
        catch { /* return fresh cache on any parse error */ }

        return new CacheFile
        {
            SourceRoot    = sourceFolder,
            SchemaVersion = CacheFile.CurrentSchemaVersion,
            CreatedAt     = DateTime.UtcNow.ToString("o"),
            LastUpdated   = DateTime.UtcNow.ToString("o"),
        };
    }

    public void SaveCache(string sourceFolder, CacheFile cache)
    {
        var path = GetCacheFilePath(sourceFolder);
        try
        {
            Directory.CreateDirectory(_cacheFolder);
            File.WriteAllText(path,
                JsonSerializer.Serialize(cache, _jsonOpts),
                Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    // ── Entry management ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds an entry by its current OR original relative path.
    /// Handles the case where a file was previously renamed and the
    /// cache key (original_rel_path) differs from the on-disk name.
    /// </summary>
    public CacheEntry? FindEntry(CacheFile cache, string relPath)
    {
        var norm = NormRel(relPath);

        // Direct match on original path (primary key)
        if (cache.Files.TryGetValue(norm, out var byOriginal))
            return byOriginal;

        // Scan for a match on current path (after a previous rename)
        foreach (var entry in cache.Files.Values)
            if (NormRel(entry.CurrentRelPath) == norm)
                return entry;

        return null;
    }

    /// <summary>
    /// Creates a new entry if one does not already exist for
    /// <paramref name="relPath"/>, persisting <paramref name="exifSnapshot"/>
    /// as the original EXIF state.  Returns the entry (new or existing).
    /// </summary>
    public CacheEntry EnsureEntry(
        CacheFile              cache,
        string                 relPath,
        Dictionary<string, string?> exifSnapshot)
    {
        var existing = FindEntry(cache, relPath);
        if (existing != null) return existing;

        var now   = DateTime.UtcNow.ToString("o");
        var entry = new CacheEntry
        {
            OriginalRelPath  = NormRel(relPath),
            CurrentRelPath   = NormRel(relPath),
            OriginalExif     = new Dictionary<string, string?>(exifSnapshot),
            CurrentExif      = new Dictionary<string, string?>(exifSnapshot),
            WasRenamed       = false,
            FirstSeenAt      = now,
            LastProcessedAt  = now,
            Status           = "pending",
        };
        cache.Files[entry.OriginalRelPath] = entry;
        return entry;
    }

    /// <summary>
    /// Marks a successfully processed entry with its new name and description.
    /// </summary>
    public void MarkProcessed(
        CacheFile cache,
        CacheEntry entry,
        string newRelPath,
        Dictionary<string, string?> currentExif,
        bool wasRenamed)
    {
        entry.CurrentRelPath  = NormRel(newRelPath);
        entry.CurrentExif     = new Dictionary<string, string?>(currentExif);
        entry.WasRenamed      = wasRenamed;
        entry.LastProcessedAt = DateTime.UtcNow.ToString("o");
        entry.Status          = "processed";
    }

    /// <summary>
    /// Marks an entry as skipped (already tagged, resolution too low, etc.).
    /// </summary>
    public void MarkSkipped(CacheFile cache, CacheEntry entry)
    {
        entry.LastProcessedAt = DateTime.UtcNow.ToString("o");
        entry.Status          = "skipped";
    }

    /// <summary>
    /// Marks an entry as failed.
    /// </summary>
    public void MarkFailed(CacheFile cache, CacheEntry entry)
    {
        entry.LastProcessedAt = DateTime.UtcNow.ToString("o");
        entry.Status          = "failed";
    }

    /// <summary>
    /// Restores an entry to its pre-processing state (undo).
    /// </summary>
    public void MarkUndone(CacheEntry entry)
    {
        entry.CurrentRelPath  = entry.OriginalRelPath;
        entry.CurrentExif     = new Dictionary<string, string?>(entry.OriginalExif);
        entry.WasRenamed      = false;
        entry.LastProcessedAt = DateTime.UtcNow.ToString("o");
        entry.Status          = "undone";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormRel(string rel) =>
        rel.Replace('\\', '/').TrimStart('/');

    private static string ResolveFolder(string folder) =>
        string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ImageTagger", "cache")
            : folder;
}
