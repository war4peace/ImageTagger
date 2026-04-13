using ImageTagger.Helpers;
using ImageTagger.Models;

namespace ImageTagger.Services;

/// <summary>
/// Orchestrates the full processing pipeline: pre-scan → analyse → rename,
/// and the undo pass.  All heavy work runs on background threads via Task;
/// progress is reported through <see cref="IProgress{T}"/> so the ViewModel
/// can safely update the UI from the calling (UI) thread.
/// </summary>
public sealed class ImageProcessorService
{
    private readonly ExifService       _exif;
    private readonly CacheService      _cache;
    private readonly ImageScanService  _scanner;
    private readonly OllamaService     _ollama;

    public ImageProcessorService(
        ExifService      exif,
        CacheService     cache,
        ImageScanService scanner,
        OllamaService    ollama)
    {
        _exif    = exif;
        _cache   = cache;
        _scanner = scanner;
        _ollama  = ollama;
    }

    // ── Processing pipeline ───────────────────────────────────────────────────

    /// <summary>
    /// Runs the full tagging / renaming pipeline.
    /// </summary>
    public async Task ProcessAsync(
        ProcessingOptions           options,
        IProgress<ProcessingProgress> progress,
        CancellationToken           ct)
    {
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var source = options.SourceFolder;

        // ── 1. Scan ──────────────────────────────────────────────────────────
        Report(progress, ProcessingPhase.Scanning, ProcessingStatus.Scanning,
               0, 0, 0, 0, 0, null, null, null, sw.Elapsed);

        var scanResult = await Task.Run(
            () => _scanner.Scan(source, options.MinWidth, options.MinHeight), ct);

        var files = scanResult.Files;

        // Report how many were filtered by the minimum-resolution setting
        if (scanResult.SkippedByResolution > 0)
        {
            Report(progress, ProcessingPhase.Scanning, ProcessingStatus.Info,
                   scanResult.TotalBeforeFilter, 0, 0, 0, 0, null, null,
                   $"{scanResult.SkippedByResolution} image(s) skipped — below minimum resolution ({options.MinWidth}×{options.MinHeight} px)",
                   sw.Elapsed);
        }

        if (files.Count == 0)
        {
            ReportComplete(progress, 0, 0, 0, 0, sw.Elapsed);
            return;
        }

        ct.ThrowIfCancellationRequested();

        // ── 1b. Warm up the model so the first image doesn't pay cold-start ───
        _ollama.SetBaseUrl(options.OllamaUrl);

        Report(progress, ProcessingPhase.Scanning, ProcessingStatus.Info,
               files.Count, 0, 0, 0, 0,
               $"Loading {options.OllamaModel}…",
               null,
               $"Loading {options.OllamaModel}…  (up to 60 s, please wait)",
               sw.Elapsed);

        var modelReady = await _ollama.WarmUpAsync(options.OllamaModel, 60, ct);

        Report(progress, ProcessingPhase.Scanning, ProcessingStatus.Info,
               files.Count, 0, 0, 0, 0, null, null,
               modelReady
                   ? $"{options.OllamaModel} loaded — starting image analysis"
                   : "Warning: model warm-up timed out — first image may still fail",
               sw.Elapsed);

        ct.ThrowIfCancellationRequested();

        // ── 2. Pre-scan cache (snapshot original EXIF for all files) ─────────
        _cache.SetCacheFolder(options.CacheFolder);
        var cacheFile = _cache.LoadCache(source);

        Report(progress, ProcessingPhase.Caching, ProcessingStatus.Caching,
               files.Count, 0, 0, 0, 0, null, null, null, sw.Elapsed);

        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel      = ImageScanService.MakeRelative(source, file);
                var snapshot = _exif.ReadTrackedFields(file);
                _cache.EnsureEntry(cacheFile, rel, snapshot);
            }
            cacheFile.LastUpdated = DateTime.UtcNow.ToString("o");
            _cache.SaveCache(source, cacheFile);
        }, ct);

        ct.ThrowIfCancellationRequested();

        // ── 3. Process each file ─────────────────────────────────────────────
        int total          = files.Count;
        int current        = 0;
        int countProcessed = 0;
        int countSkipped   = 0;
        int countFailed    = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            current++;

            var rel   = ImageScanService.MakeRelative(source, file);
            var entry = _cache.FindEntry(cacheFile, rel)!;
            var fname = Path.GetFileName(file);

            Report(progress, ProcessingPhase.Processing, ProcessingStatus.Analysing,
                   total, current, countProcessed, countSkipped, countFailed,
                   fname, null, null, sw.Elapsed);

            // --- Skip check ------------------------------------------------
            bool alreadyTagged = !options.ForceTag && _exif.IsAlreadyProcessed(file);
            bool alreadyRenamed =
                !options.ForceRename &&
                !FilenameHelper.HasCameraDefaultName(Path.GetFileName(file));

            if (alreadyTagged && alreadyRenamed)
            {
                countSkipped++;
                _cache.MarkSkipped(cacheFile, entry);
                _cache.SaveCache(source, cacheFile);
                Report(progress, ProcessingPhase.Processing, ProcessingStatus.Skipped,
                       total, current, countProcessed, countSkipped, countFailed,
                       fname, null, null, sw.Elapsed);
                continue;
            }

            // --- Analyse image with Ollama ----------------------------------
            try
            {
                var (description, condensed) = await _ollama.AnalyseImageAsync(
                    file,
                    options.OllamaModel,
                    options.Language,
                    options.OllamaTimeoutSeconds,
                    options.CondensedMaxWords,
                    ct);

                // --- Write EXIF if needed -----------------------------------
                if (!alreadyTagged || options.ForceTag)
                    await Task.Run(() => _exif.WriteDescription(file, description), ct);

                // --- Rename if needed ---------------------------------------
                string? newName = null;
                if (FilenameHelper.HasCameraDefaultName(Path.GetFileName(file)) ||
                    options.ForceRename)
                {
                    // Always derive the new name from the ORIGINAL (camera-default) filename
                    // stored in the cache, not from 'file'.  This prevents descriptions from
                    // accumulating on force-reprocess:
                    //   IMG_0134_Water_Lilies_On_Lake.jpg  →  IMG_0134_New_Description.jpg
                    // instead of:
                    //   IMG_0134_Water_Lilies_On_Lake_New_Description.jpg
                    var originalAbs = Path.Combine(
                        source,
                        entry.OriginalRelPath.Replace('/', Path.DirectorySeparatorChar));

                    var newPath = FilenameHelper.BuildNewPath(originalAbs, condensed);
                    await Task.Run(() => File.Move(file, newPath), ct);
                    newName = Path.GetFileName(newPath);

                    // Update cache to reflect the new filename
                    var newRel = ImageScanService.MakeRelative(source, newPath);
                    var currentExif = _exif.ReadTrackedFields(newPath);
                    _cache.MarkProcessed(cacheFile, entry, newRel, currentExif, wasRenamed: true);
                }
                else
                {
                    var currentExif = _exif.ReadTrackedFields(file);
                    _cache.MarkProcessed(cacheFile, entry, rel, currentExif, wasRenamed: false);
                }

                countProcessed++;
                _cache.SaveCache(source, cacheFile);

                Report(progress, ProcessingPhase.Processing, ProcessingStatus.Processed,
                       total, current, countProcessed, countSkipped, countFailed,
                       fname, newName, description, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user pressed Stop — unwind the whole loop
            }
            catch (OperationCanceledException)
            {
                // Ollama's internal timeout fired (not the user) — log as per-file failure
                countFailed++;
                _cache.MarkFailed(cacheFile, entry);
                _cache.SaveCache(source, cacheFile);
                Report(progress, ProcessingPhase.Processing, ProcessingStatus.Failed,
                       total, current, countProcessed, countSkipped, countFailed,
                       fname, null,
                       "Ollama did not respond in time — increase timeout in Advanced options",
                       sw.Elapsed);
            }
            catch (Exception ex)
            {
                countFailed++;
                _cache.MarkFailed(cacheFile, entry);
                _cache.SaveCache(source, cacheFile);
                Report(progress, ProcessingPhase.Processing, ProcessingStatus.Failed,
                       total, current, countProcessed, countSkipped, countFailed,
                       fname, null, ex.Message, sw.Elapsed);
            }
        }

        ReportComplete(progress, total, countProcessed, countSkipped, countFailed, sw.Elapsed);
    }

    // ── Undo pipeline ─────────────────────────────────────────────────────────

    /// <summary>
    /// Undoes the changes recorded in <paramref name="entries"/>:
    /// restores EXIF fields and reverses renames.
    /// </summary>
    public async Task UndoAsync(
        string                      sourceFolder,
        IEnumerable<UndoItem>       entries,
        IProgress<ProcessingProgress> progress,
        CancellationToken           ct)
    {
        var sw       = System.Diagnostics.Stopwatch.StartNew();
        var items    = entries.ToList();
        int total    = items.Count;
        int current  = 0;

        // CacheService cache folder has already been set by the ViewModel
        var cacheFile = _cache.LoadCache(sourceFolder);

        Report(progress, ProcessingPhase.Undoing, ProcessingStatus.Starting,
               total, 0, 0, 0, 0, null, null, null, sw.Elapsed);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            current++;

            var entry = _cache.FindEntry(cacheFile, item.CacheKey);
            if (entry == null)
            {
                Report(progress, ProcessingPhase.Undoing, ProcessingStatus.Failed,
                       total, current, 0, 0, 0, item.CurrentName,
                       null, "Cache entry not found", sw.Elapsed);
                continue;
            }

            // Resolve the on-disk path from the current relative path
            var currentAbs = Path.Combine(sourceFolder,
                entry.CurrentRelPath.Replace('/', Path.DirectorySeparatorChar));

            Report(progress, ProcessingPhase.Undoing, ProcessingStatus.Analysing,
                   total, current, 0, 0, 0,
                   Path.GetFileName(currentAbs), null, null, sw.Elapsed);

            try
            {
                // --- Restore EXIF -------------------------------------------
                await Task.Run(() =>
                    _exif.RestoreFields(currentAbs, entry.OriginalExif), ct);

                // --- Reverse rename -----------------------------------------
                string? originalName = null;
                if (entry.WasRenamed && entry.OriginalRelPath != entry.CurrentRelPath)
                {
                    var originalAbs = Path.Combine(sourceFolder,
                        entry.OriginalRelPath.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(currentAbs) && !File.Exists(originalAbs))
                    {
                        await Task.Run(() => File.Move(currentAbs, originalAbs), ct);
                        originalName = Path.GetFileName(originalAbs);
                    }
                }

                _cache.MarkUndone(entry);
                _cache.SaveCache(sourceFolder, cacheFile);

                Report(progress, ProcessingPhase.Undoing, ProcessingStatus.Processed,
                       total, current, 0, 0, 0,
                       Path.GetFileName(currentAbs), originalName, null, sw.Elapsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(progress, ProcessingPhase.Undoing, ProcessingStatus.Failed,
                       total, current, 0, 0, 0,
                       Path.GetFileName(currentAbs), null, ex.Message, sw.Elapsed);
            }
        }

        ReportComplete(progress, total, current, 0, 0, sw.Elapsed);
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all processable cache entries for display in the Undo panel.
    /// </summary>
    public List<UndoItem> GetUndoItems(string sourceFolder)
    {
        var cache = _cache.LoadCache(sourceFolder);
        return cache.Files.Values
            .Where(e => e.Status is "processed" or "skipped" or "failed")
            .OrderBy(e => e.OriginalRelPath, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UndoItem
            {
                CacheKey     = e.OriginalRelPath,
                OriginalName = Path.GetFileName(e.OriginalRelPath),
                CurrentName  = Path.GetFileName(e.CurrentRelPath),
                Status       = e.Status,
            })
            .ToList();
    }

    // ── Progress helpers ──────────────────────────────────────────────────────

    private static void Report(
        IProgress<ProcessingProgress> progress,
        ProcessingPhase  phase,
        ProcessingStatus status,
        int total, int current,
        int countProcessed, int countSkipped, int countFailed,
        string? currentFile,
        string? newName,
        string? description,
        TimeSpan elapsed)
    {
        progress.Report(new ProcessingProgress
        {
            Phase          = phase,
            Status         = status,
            Total          = total,
            Current        = current,
            CountProcessed = countProcessed,
            CountSkipped   = countSkipped,
            CountFailed    = countFailed,
            CurrentFile    = currentFile,
            NewName        = newName,
            Description    = description,
            Elapsed        = elapsed,
        });
    }

    private static void ReportComplete(
        IProgress<ProcessingProgress> progress,
        int total, int countProcessed, int countSkipped, int countFailed,
        TimeSpan elapsed)
    {
        progress.Report(new ProcessingProgress
        {
            Phase          = ProcessingPhase.Complete,
            Status         = ProcessingStatus.Complete,
            Total          = total,
            Current        = countProcessed + countSkipped + countFailed,
            CountProcessed = countProcessed,
            CountSkipped   = countSkipped,
            CountFailed    = countFailed,
            Elapsed        = elapsed,
        });
    }
}
