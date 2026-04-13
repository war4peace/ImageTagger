using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageTagger.Helpers;

public static class FilenameHelper
{
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".tiff", ".tif"];

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ── Camera default name detection ─────────────────────────────────────────

    private static readonly string[] _rawPatterns =
    [
        // Patterns are anchored at both ends ($) so that a renamed file whose
        // stem begins with a camera prefix — e.g. IMG_20260411_021652_Pond_View —
        // is NOT mistakenly identified as a camera default name.
        // [\d_]+ allows underscores between date/time segments (Android timestamps).
        @"^IMG_[\d_]+$",    @"^DSC[\d_]+$",    @"^DSCF[\d_]+$",   @"^DSCN[\d_]+$",
        @"^STA[\d_]+$",     @"^HPIM[\d_]+$",   @"^IMAG[\d_]+$",   @"^P\d{7}$",
        @"^MVI_[\d_]+$",    @"^MOV_[\d_]+$",   @"^GOPR[\d_]+$",   @"^PXL_[\d_]+$",
        @"^PANO_[\d_]+$",   @"^VID_[\d_]+$",   @"^WP_[\d_]+$",    @"^DCIM\d*$",
        @"^\d{8}_\d{6}$",   @"^\d+$",
    ];

    private static readonly Regex[] _patterns =
        _rawPatterns.Select(p => new Regex(p,
            RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    /// <summary>
    /// Returns true if the filename stem looks like a camera default name.
    /// Strips _upscaled and trailing (N) same-second duplicate suffixes
    /// before matching, so e.g. "20181018_163120(0).jpg" is handled correctly.
    /// </summary>
    public static bool HasCameraDefaultName(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        stem = Regex.Replace(stem, @"_upscaled$", "",    RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"\(\d+\)$",   "");   // strip (0), (1), (00) …
        return _patterns.Any(p => p.IsMatch(stem));
    }

    // ── Condensed title sanitisation ──────────────────────────────────────────

    /// <summary>
    /// Convert an Ollama-generated title to a safe ASCII filename component.
    /// Logic mirrors the Python script's _sanitize_condensed().
    /// </summary>
    public static string SanitizeCondensed(string text, int maxWords = 5)
    {
        // Decompose unicode and drop combining marks (strips diacritics)
        text = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        text = sb.ToString();

        // Keep only printable ASCII
        text = Regex.Replace(text, @"[^\x20-\x7E]", "");
        // Spaces and hyphens → underscore
        text = Regex.Replace(text, @"[\s\-]+", "_");
        // Strip illegal filename characters
        text = Regex.Replace(text, @"[<>:""/\\|?*]", "");
        // Collapse runs of underscores; trim edges
        text = Regex.Replace(text, @"_+", "_").Trim('_');

        var parts = text.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > maxWords) parts = parts[..maxWords];
        return parts.Length > 0 ? string.Join("_", parts) : "Unknown_Image";
    }

    // ── Rename path builder ───────────────────────────────────────────────────

    /// <summary>
    /// Build "STEM_Condensed.ext", appending _2, _3 … on collision.
    /// </summary>
    public static string BuildNewPath(string originalPath, string condensed)
    {
        var dir  = Path.GetDirectoryName(originalPath)!;
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var ext  = Path.GetExtension(originalPath);
        var candidate = Path.Combine(dir, $"{stem}_{condensed}{ext}");
        if (!File.Exists(candidate)) return candidate;
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{stem}_{condensed}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
