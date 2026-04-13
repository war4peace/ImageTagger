using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using TagLib.IFD;
using TagLib.IFD.Entries;

namespace ImageTagger.Services;

/// <summary>
/// Reads EXIF via MetadataExtractor (fast, robust).
/// Writes EXIF via TagLibSharp (reliable across all JPEG types, including
/// images that have no existing EXIF block).
/// </summary>
public sealed class ExifService
{
    private const string ProcessedMarker = "TaggedBy:ImageTagger";

    // EXIF integer tag numbers
    private const ushort TagImageDescription = 270;    // IFD0
    private const ushort TagXpComment        = 40092;  // IFD0 – Windows UTF-16LE
    private const int    TagUserComment      = 37510;  // Exif SubIFD

    public static readonly string[] TrackedFieldNames =
        ["ImageDescription", "XPComment", "UserComment"];

    // ── Dimensions ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (width, height) from the JPEG SOF marker or EXIF SubIFD.
    /// Returns (0,0) on failure.  Only reads headers — no pixel data loaded.
    /// </summary>
    public (int Width, int Height) GetImageDimensions(string path)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            var jpeg = dirs.OfType<JpegDirectory>().FirstOrDefault();
            if (jpeg != null &&
                jpeg.TryGetInt32(JpegDirectory.TagImageWidth,  out var jw) &&
                jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) &&
                jw > 0 && jh > 0)
                return (jw, jh);

            var exif = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exif != null &&
                exif.TryGetInt32(40962, out var ew) &&
                exif.TryGetInt32(40963, out var eh) &&
                ew > 0 && eh > 0)
                return (ew, eh);
        }
        catch { }
        return (0, 0);
    }

    // ── Reading (MetadataExtractor) ───────────────────────────────────────────

    /// <summary>
    /// Reads the three tracked EXIF fields.  Null means the field is absent.
    /// </summary>
    public Dictionary<string, string?> ReadTrackedFields(string path)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ImageDescription"] = null,
            ["XPComment"]        = null,
            ["UserComment"]      = null,
        };

        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);

            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null)
            {
                result["ImageDescription"] = ifd0.GetDescription(TagImageDescription);
                result["XPComment"]        = ifd0.GetDescription(TagXpComment);
            }

            var exif = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exif != null)
                result["UserComment"] = exif.GetDescription(TagUserComment);
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Returns true if any tracked field already contains our processed marker.
    /// </summary>
    public bool IsAlreadyProcessed(string path)
    {
        return ReadTrackedFields(path).Values.Any(v =>
            v?.Contains(ProcessedMarker, StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── Writing (TagLibSharp) ─────────────────────────────────────────────────

    /// <summary>
    /// Writes description + marker to EXIF.  Works on all JPEG types including
    /// images that have no existing EXIF block.
    /// </summary>
    public void WriteDescription(string path, string description)
    {
        var text = $"{description}\n{ProcessedMarker}";
        WriteFields(path, imageDesc: text, userComment: text, xpComment: text);
    }

    /// <summary>
    /// Restores EXIF fields from a cache snapshot (undo).
    /// Null values remove the field.
    /// </summary>
    public void RestoreFields(string path, Dictionary<string, string?> snapshot)
    {
        snapshot.TryGetValue("ImageDescription", out var imageDesc);
        snapshot.TryGetValue("XPComment",        out var xpComment);
        snapshot.TryGetValue("UserComment",      out var userComment);
        WriteFields(path, imageDesc, userComment, xpComment);
    }

    // ── TagLibSharp write implementation ──────────────────────────────────────

    private static void WriteFields(
        string  path,
        string? imageDesc,
        string? userComment,
        string? xpComment)
    {
        try
        {
            WriteViaTagLib(path, imageDesc, userComment, xpComment);
        }
        catch (Exception ex) when (IsTagLibWriteFailure(ex))
        {
            // Some phone images (modern Android/iOS) have non-standard JPEG
            // structures — dual APP1 blocks, oversized MakerNotes, or
            // manufacturer-specific IFD layouts — that TagLibSharp refuses to
            // write back.  Fall back to System.Drawing.Common (GDI+) which
            // sets EXIF PropertyItems directly on the bitmap and re-saves,
            // normalising the file structure in the same pass.
            // Quality 95 keeps visible quality loss imperceptible.
            WriteViaSystemDrawing(path, imageDesc, userComment, xpComment);
        }
    }

    private static bool IsTagLibWriteFailure(Exception ex)
        => ex is System.IO.IOException
        || ex is InvalidOperationException
        || ex is NotSupportedException
        || ex.Message.Contains("writeable",    StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("read only",    StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("read-only",    StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not writable", StringComparison.OrdinalIgnoreCase);

    private static void WriteViaTagLib(
        string  path,
        string? imageDesc,
        string? userComment,
        string? xpComment)
    {
        using var tfile = TagLib.File.Create(path);

        if (!tfile.Writeable)
            throw new System.IO.IOException(
                $"TagLibSharp marked file as not writable: {path}");

        // UserComment — TagLib's unified Comment maps to EXIF UserComment.
        tfile.Tag.Comment = userComment ?? "";

        // ImageDescription (tag 270) + XPComment (tag 40092) via raw IFD0.
        // create=true makes TagLib synthesise an EXIF block if none exists.
        if (tfile.GetTag(TagLib.TagTypes.TiffIFD, true) is IFDTag ifd)
        {
            if (imageDesc is null)
                ifd.Structure.RemoveTag(0, TagImageDescription);
            else
                ifd.Structure.SetEntry(0,
                    new StringIFDEntry(TagImageDescription, imageDesc));

            if (xpComment is null)
                ifd.Structure.RemoveTag(0, TagXpComment);
            else
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(xpComment + "\0");
                ifd.Structure.SetEntry(0,
                    new ByteVectorIFDEntry(TagXpComment,
                        new TagLib.ByteVector(bytes)));
            }
        }

        tfile.Save();
    }

    /// <summary>
    /// Fallback writer using System.Drawing.Common (GDI+).
    /// Sets EXIF PropertyItems directly on the bitmap and re-saves at quality 95,
    /// bypassing TagLibSharp entirely.  Handles JPEG variants that TagLibSharp
    /// refuses to write (non-standard structures common in phone cameras).
    /// </summary>
    private static void WriteViaSystemDrawing(
        string  path,
        string? imageDesc,
        string? userComment,
        string? xpComment)
    {
        var tmp = path + ".imgtmp";
        try
        {
            // Load into memory first — GDI+ holds a file lock on Bitmap(path)
            // even after Dispose(), which would block the final File.Move.
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var bmp = new System.Drawing.Bitmap(ms);

            // ImageDescription (0x010E / 270) — ASCII, EXIF type 2
            if (imageDesc != null)
                SetBitmapProperty(bmp, 0x010E, 2,
                    System.Text.Encoding.ASCII.GetBytes(imageDesc + "\0"));

            // UserComment (0x9286 / 37510) — EXIF type 7 (UNDEFINED)
            // Encoding: 8-byte ASCII charset prefix + ASCII text.
            if (userComment != null)
            {
                var prefix  = "ASCII\0\0\0"u8.ToArray();
                var text    = System.Text.Encoding.ASCII.GetBytes(userComment);
                var payload = new byte[prefix.Length + text.Length];
                prefix.CopyTo(payload, 0);
                text.CopyTo(payload, prefix.Length);
                SetBitmapProperty(bmp, 0x9286, 7, payload);
            }

            // XPComment (0x9C9C / 40092) — UTF-16LE byte array, EXIF type 1
            if (xpComment != null)
                SetBitmapProperty(bmp, 0x9C9C, 1,
                    System.Text.Encoding.Unicode.GetBytes(xpComment + "\0"));

            var codec = System.Drawing.Imaging.ImageCodecInfo
                .GetImageEncoders()
                .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ep = new System.Drawing.Imaging.EncoderParameters(1);
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, 95L);
            bmp.Save(tmp, codec, ep);

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Creates and sets an EXIF PropertyItem on a bitmap.
    /// PropertyItem has no public constructor in .NET; we use
    /// RuntimeHelpers.GetUninitializedObject to allocate an instance.
    /// </summary>
    private static void SetBitmapProperty(
        System.Drawing.Bitmap bmp, int id, short type, byte[] value)
    {
        var item = (System.Drawing.Imaging.PropertyItem)
            System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(System.Drawing.Imaging.PropertyItem));
        item.Id    = id;
        item.Type  = type;
        item.Value = value;
        item.Len   = value.Length;
        bmp.SetPropertyItem(item);
    }
}
