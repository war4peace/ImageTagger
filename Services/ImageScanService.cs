using ImageTagger.Helpers;

namespace ImageTagger.Services;

public sealed class ImageScanService
{
    private readonly ExifService _exif;

    public ImageScanService(ExifService exif) => _exif = exif;

    // ── Result ────────────────────────────────────────────────────────────────

    public record ScanResult(
        List<string> Files,
        int          TotalBeforeFilter,
        int          SkippedByResolution);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="sourceFolder"/> recursively.
    /// Returns all image files that pass the resolution filter, together with
    /// counts so the caller can report how many were filtered out.
    /// </summary>
    public ScanResult Scan(string sourceFolder, int minWidth, int minHeight)
    {
        var results   = new List<string>();
        int total     = 0;
        int skipped   = 0;
        bool filtered = minWidth > 0 || minHeight > 0;

        if (!Directory.Exists(sourceFolder))
            return new ScanResult(results, 0, 0);

        foreach (var file in EnumerateImageFiles(sourceFolder))
        {
            if (IsInUpscaledSubfolder(sourceFolder, file)) continue;

            total++;

            if (filtered)
            {
                var (w, h) = _exif.GetImageDimensions(file);
                if (w < minWidth || h < minHeight)
                {
                    skipped++;
                    continue;
                }
            }

            results.Add(file);
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return new ScanResult(results, total, skipped);
    }

    /// <summary>
    /// Returns the path of an image file relative to <paramref name="sourceFolder"/>,
    /// normalised to forward slashes.
    /// </summary>
    public static string MakeRelative(string sourceFolder, string absolutePath)
    {
        var root = Path.GetFullPath(sourceFolder)
                       .TrimEnd(Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        if (absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return absolutePath[root.Length..].Replace('\\', '/');

        return Path.GetFileName(absolutePath);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateImageFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                 .Where(FilenameHelper.IsImageFile);

    private static bool IsInUpscaledSubfolder(string sourceFolder, string filePath)
    {
        var rel   = Path.GetRelativePath(sourceFolder, filePath);
        var parts = rel.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].EndsWith("_upscaled", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
