using System.Text.Json.Serialization;

namespace ImageTagger.Models;

/// <summary>
/// Cache entry for a single image file.
/// Keys match the Python script's cache format so files are cross-compatible.
/// </summary>
public class CacheEntry
{
    [JsonPropertyName("original_rel_path")]
    public string OriginalRelPath { get; set; } = "";

    [JsonPropertyName("current_rel_path")]
    public string CurrentRelPath { get; set; } = "";

    /// <summary>
    /// Snapshot of the three tracked EXIF fields before any modification.
    /// Keys: ImageDescription, XPComment, UserComment.
    /// Value is the decoded string, or null if the field was absent.
    /// </summary>
    [JsonPropertyName("original_exif")]
    public Dictionary<string, string?> OriginalExif { get; set; } = new();

    [JsonPropertyName("current_exif")]
    public Dictionary<string, string?> CurrentExif { get; set; } = new();

    [JsonPropertyName("was_renamed")]
    public bool WasRenamed { get; set; }

    [JsonPropertyName("first_seen_at")]
    public string FirstSeenAt { get; set; } = "";

    [JsonPropertyName("last_processed_at")]
    public string? LastProcessedAt { get; set; }

    /// <summary>scanned | processed | skipped | failed | undone</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "scanned";
}

/// <summary>Top-level cache file written to {cacheFolder}/{md5}.json</summary>
public class CacheFile
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("source_root")]
    public string SourceRoot { get; set; } = "";

    /// <summary>Convenience alias used internally; not serialised.</summary>
    [JsonIgnore]
    public string SourceFolder
    {
        get => SourceRoot;
        set => SourceRoot = value;
    }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("files")]
    public Dictionary<string, CacheEntry> Files { get; set; } = new();
}
