using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ImageTagger.Helpers;
using ImageTagger.Models;
using ImageTagger.Services;

namespace ImageTagger.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly ExifService            _exif;
    private readonly CacheService           _cache;
    private readonly ImageScanService       _scanner;
    private readonly OllamaService          _ollama;
    private readonly ImageProcessorService  _processor;

    // ── Ollama polling ────────────────────────────────────────────────────────
    private readonly DispatcherTimer _ollamaTimer;

    // ── Processing state ──────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;

    // ── Settings ──────────────────────────────────────────────────────────────
    private readonly AppSettings _settings;

    // =========================================================================
    // Constructor
    // =========================================================================

    public MainViewModel()
    {
        _settings  = AppSettings.Load();
        _exif      = new ExifService();
        _cache     = new CacheService(_settings.CacheFolder);
        _scanner   = new ImageScanService(_exif);
        _ollama    = new OllamaService(_settings.OllamaUrl);
        _processor = new ImageProcessorService(_exif, _cache, _scanner, _ollama);

        // ── Restore persisted settings ────────────────────────────────────
        _sourceFolder       = _settings.LastSourceFolder;
        _cacheFolder        = _settings.CacheFolder;
        _ollamaUrl          = _settings.OllamaUrl;
        _selectedLanguage   = _settings.Language;
        // Always start unchecked — prevents accidental reprocessing of already-tagged images.
        _forceTag    = false;
        _forceRename = false;
        _minWidth           = _settings.MinWidth;
        _minHeight          = _settings.MinHeight;
        _condensedMaxWords      = _settings.CondensedMaxWords;
        _ollamaTimeoutSec       = _settings.OllamaTimeoutSeconds;
        _discordWebhookEnabled  = _settings.DiscordWebhookEnabled;
        _discordWebhookUrl      = _settings.DiscordWebhookUrl;

        // ── Languages ─────────────────────────────────────────────────────
        Languages = LanguageHelper.All;

        // ── Commands ──────────────────────────────────────────────────────
        BrowseFolderCommand     = new RelayCommand(BrowseFolder,     () => !IsProcessing);
        BrowseCacheFolderCommand= new RelayCommand(BrowseCacheFolder,() => !IsProcessing);
        RefreshOllamaCommand    = new AsyncRelayCommand(RefreshOllamaAsync);
        StartCommand            = new AsyncRelayCommand(StartAsync,  () => CanStart);
        StopCommand             = new RelayCommand(Stop,             () => IsProcessing);
        UndoSelectedCommand     = new AsyncRelayCommand(UndoSelectedAsync, () => CanUndo);
        UndoAllCommand          = new AsyncRelayCommand(UndoAllAsync,      () => CanUndo);
        SelectAllUndoCommand    = new RelayCommand(() =>
        {
            foreach (var item in UndoItems) item.IsSelected = true;
            RefreshUndoCommands();
        });
        SelectNoneUndoCommand   = new RelayCommand(() =>
        {
            foreach (var item in UndoItems) item.IsSelected = false;
            RefreshUndoCommands();
        });
        ClearLogCommand               = new RelayCommand(() => LogEntries.Clear());
        OpenCacheFolderCommand        = new RelayCommand(OpenCacheFolder);
        ValidateDiscordWebhookCommand = new AsyncRelayCommand(ValidateDiscordWebhookAsync);

        // ── Ollama polling timer ──────────────────────────────────────────
        _ollamaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ollamaTimer.Tick += async (_, _) => await RefreshOllamaAsync();
        _ollamaTimer.Start();

        // Initial Ollama check (fire-and-forget — safe, result posted to UI thread)
        _ = RefreshOllamaAsync();
    }

    // =========================================================================
    // Bindable properties — configuration
    // =========================================================================

    private string _sourceFolder = "";
    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (!SetField(ref _sourceFolder, value)) return;
            _settings.LastSourceFolder = value;
            _settings.Save();
            RefreshUndoItems();
            RefreshStartCommand();
        }
    }

    private string _cacheFolder = "";
    public string CacheFolder
    {
        get => _cacheFolder;
        set
        {
            if (!SetField(ref _cacheFolder, value)) return;
            _settings.CacheFolder = value;
            _cache.SetCacheFolder(value);
            _settings.Save();
            RefreshUndoItems();
        }
    }

    private string _ollamaUrl = "http://127.0.0.1:11434";
    public string OllamaUrl
    {
        get => _ollamaUrl;
        set
        {
            if (!SetField(ref _ollamaUrl, value)) return;
            _settings.OllamaUrl = value;
            _ollama.SetBaseUrl(value);
            _settings.Save();
        }
    }

    // ── Language ──────────────────────────────────────────────────────────────

    public IReadOnlyList<string> Languages { get; }

    private string _selectedLanguage = "English";
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetField(ref _selectedLanguage, value)) return;
            _settings.Language = value;
            _settings.Save();
        }
    }

    // ── Ollama model ──────────────────────────────────────────────────────────

    public ObservableCollection<string> OllamaModels { get; } = [];

    private string _selectedModel = "";
    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetField(ref _selectedModel, value)) return;
            _settings.SelectedModel = value;
            _settings.Save();
            RefreshStartCommand();
        }
    }

    // ── Processing options ────────────────────────────────────────────────────

    private bool _forceTag;
    public bool ForceTag
    {
        get => _forceTag;
        set { SetField(ref _forceTag, value); _settings.ForceTag = value; _settings.Save(); }
    }

    private bool _forceRename;
    public bool ForceRename
    {
        get => _forceRename;
        set { SetField(ref _forceRename, value); _settings.ForceRename = value; _settings.Save(); }
    }

    private int _minWidth = 0;
    public int MinWidth
    {
        get => _minWidth;
        set { SetField(ref _minWidth, value); _settings.MinWidth = value; _settings.Save(); }
    }

    private int _minHeight = 0;
    public int MinHeight
    {
        get => _minHeight;
        set { SetField(ref _minHeight, value); _settings.MinHeight = value; _settings.Save(); }
    }

    private int _condensedMaxWords = 5;
    public int CondensedMaxWords
    {
        get => _condensedMaxWords;
        set { SetField(ref _condensedMaxWords, value); _settings.CondensedMaxWords = value; _settings.Save(); }
    }

    private int _ollamaTimeoutSec = 120;
    public int OllamaTimeoutSec
    {
        get => _ollamaTimeoutSec;
        set { SetField(ref _ollamaTimeoutSec, value); _settings.OllamaTimeoutSeconds = value; _settings.Save(); }
    }

    // ── Discord webhook ───────────────────────────────────────────────────────

    private bool _discordWebhookEnabled;
    public bool DiscordWebhookEnabled
    {
        get => _discordWebhookEnabled;
        set
        {
            if (!SetField(ref _discordWebhookEnabled, value)) return;
            _settings.DiscordWebhookEnabled = value;
            _settings.Save();
            if (!value) DiscordWebhookStatus = WebhookStatus.Unchecked;
        }
    }

    private string _discordWebhookUrl = "";
    public string DiscordWebhookUrl
    {
        get => _discordWebhookUrl;
        set
        {
            if (!SetField(ref _discordWebhookUrl, value)) return;
            _settings.DiscordWebhookUrl = value;
            _settings.Save();
            DiscordWebhookStatus = WebhookStatus.Unchecked; // reset on URL change
        }
    }

    private WebhookStatus _discordWebhookStatus = WebhookStatus.Unchecked;
    public WebhookStatus DiscordWebhookStatus
    {
        get => _discordWebhookStatus;
        set => SetField(ref _discordWebhookStatus, value);
    }

    // =========================================================================
    // Bindable properties — runtime state
    // =========================================================================

    private bool _ollamaAvailable;
    public bool OllamaAvailable
    {
        get => _ollamaAvailable;
        private set { SetField(ref _ollamaAvailable, value); RefreshStartCommand(); }
    }

    private string _ollamaStatus = "Checking…";
    public string OllamaStatus
    {
        get => _ollamaStatus;
        private set => SetField(ref _ollamaStatus, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            SetField(ref _isProcessing, value);
            OnPropertyChanged(nameof(IsNotProcessing));
            (BrowseFolderCommand      as RelayCommand)?.RaiseCanExecuteChanged();
            (BrowseCacheFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartCommand             as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand              as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotProcessing => !IsProcessing;

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    private bool _isComplete;
    public bool IsComplete
    {
        get => _isComplete;
        private set => SetField(ref _isComplete, value);
    }

    private string _progressText = "Idle";
    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    // ── Activity log ──────────────────────────────────────────────────────────

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    // ── Undo panel ────────────────────────────────────────────────────────────

    public ObservableCollection<UndoItem> UndoItems { get; } = [];

    private bool _undoPanelExpanded;
    public bool UndoPanelExpanded
    {
        get => _undoPanelExpanded;
        set => SetField(ref _undoPanelExpanded, value);
    }

    // =========================================================================
    // Commands
    // =========================================================================

    public ICommand BrowseFolderCommand      { get; }
    public ICommand BrowseCacheFolderCommand { get; }
    public ICommand RefreshOllamaCommand     { get; }
    public ICommand StartCommand             { get; }
    public ICommand StopCommand              { get; }
    public ICommand UndoSelectedCommand      { get; }
    public ICommand UndoAllCommand           { get; }
    public ICommand SelectAllUndoCommand     { get; }
    public ICommand SelectNoneUndoCommand    { get; }
    public ICommand ClearLogCommand               { get; }
    public ICommand OpenCacheFolderCommand        { get; }
    public ICommand ValidateDiscordWebhookCommand { get; }

    // =========================================================================
    // Command implementations
    // =========================================================================

    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Select image source folder",
            InitialDirectory = Directory.Exists(SourceFolder) ? SourceFolder : "",
        };
        if (dlg.ShowDialog() == true)
            SourceFolder = dlg.FolderName;
    }

    private void BrowseCacheFolder()
    {
        // Default to Documents\ImageTagger\cache — same path CacheService uses
        // when the field is left blank.  Fallback chain: saved path → default → Documents root.
        var defaultCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ImageTagger", "cache");

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Select cache folder",
            InitialDirectory = Directory.Exists(CacheFolder)  ? CacheFolder
                             : Directory.Exists(defaultCacheFolder) ? defaultCacheFolder
                             : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog() == true)
            CacheFolder = dlg.FolderName;
    }

    private async Task RefreshOllamaAsync()
    {
        var (available, message, models) = await _ollama.CheckStatusAsync();

        OllamaAvailable = available;
        OllamaStatus    = message;

        // Sync model list without clearing the selection if still valid
        var prev = SelectedModel;
        OllamaModels.Clear();
        foreach (var m in models) OllamaModels.Add(m);

        if (models.Contains(prev))
            SelectedModel = prev;
        else if (models.Contains(_settings.SelectedModel))
            SelectedModel = _settings.SelectedModel;
        else if (models.Count > 0)
            SelectedModel = models[0];
    }

    private bool CanStart =>
        !IsProcessing &&
        Directory.Exists(SourceFolder) &&
        !string.IsNullOrWhiteSpace(SelectedModel) &&
        OllamaAvailable;

    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        IsProcessing = true;
        IsComplete   = false;
        LogEntries.Clear();

        var options = BuildOptions();
        var progress = new Progress<ProcessingProgress>(OnProgress);

        try
        {
            await _processor.ProcessAsync(options, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddLog(new LogEntry
            {
                Kind         = LogEntryKind.Info,
                OriginalName = "Processing stopped by user.",
            });
        }
        catch (Exception ex)
        {
            AddLog(new LogEntry
            {
                Kind         = LogEntryKind.Failed,
                OriginalName = "Unexpected error",
                ErrorMessage = ex.Message,
            });
        }
        finally
        {
            _cts.Dispose();
            _cts         = null;
            IsProcessing = false;
            RefreshUndoItems();
        }
    }

    private void Stop() => _cts?.Cancel();

    private async Task ValidateDiscordWebhookAsync()
    {
        DiscordWebhookStatus = WebhookStatus.Checking;
        var ok = await Services.NotificationService.ValidateWebhookAsync(DiscordWebhookUrl);
        DiscordWebhookStatus = ok ? WebhookStatus.Valid : WebhookStatus.Invalid;
    }

    private bool CanUndo =>
        !IsProcessing &&
        Directory.Exists(SourceFolder) &&
        UndoItems.Any(i => i.IsSelected);

    private async Task UndoSelectedAsync()
    {
        var selected = UndoItems.Where(i => i.IsSelected).ToList();
        await RunUndoAsync(selected);
    }

    private async Task UndoAllAsync()
    {
        foreach (var item in UndoItems) item.IsSelected = true;
        await RunUndoAsync(UndoItems.ToList());
    }

    private async Task RunUndoAsync(IEnumerable<UndoItem> items)
    {
        if (!items.Any()) return;

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        _cache.SetCacheFolder(CacheFolder);  // ensure correct folder before undo

        var progress = new Progress<ProcessingProgress>(OnProgress);

        try
        {
            await _processor.UndoAsync(
                SourceFolder, items, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddLog(new LogEntry
            {
                Kind         = LogEntryKind.Info,
                OriginalName = "Undo stopped by user.",
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts         = null;
            IsProcessing = false;
            RefreshUndoItems();
        }
    }

    // =========================================================================
    // Progress handler
    // =========================================================================

    private void OnProgress(ProcessingProgress p)
    {
        ProgressValue = p.Total > 0 ? (p.Current * 100.0 / p.Total) : 0;

        switch (p.Status)
        {
            case ProcessingStatus.Scanning:
                ProgressText = "Scanning files…";
                StatusText   = "";
                break;

            case ProcessingStatus.Caching:
                ProgressText = "Preparing cache…";
                StatusText   = $"Snapshotting EXIF for {p.Total} files";
                break;

            case ProcessingStatus.Analysing:
                ProgressText = $"{p.Current} / {p.Total}";
                StatusText   = p.CurrentFile ?? "";
                break;

            case ProcessingStatus.Processed:
                ProgressText = $"{p.Current} / {p.Total}";
                AddLog(new LogEntry
                {
                    Kind         = LogEntryKind.Processed,
                    OriginalName = p.CurrentFile ?? "",
                    NewName      = p.NewName,
                    Description  = p.Description,
                });
                break;

            case ProcessingStatus.Skipped:
                AddLog(new LogEntry
                {
                    Kind         = LogEntryKind.Skipped,
                    OriginalName = p.CurrentFile ?? "",
                });
                break;

            case ProcessingStatus.Failed:
                AddLog(new LogEntry
                {
                    Kind         = LogEntryKind.Failed,
                    OriginalName = p.CurrentFile ?? "",
                    ErrorMessage = p.Description,
                });
                break;

            case ProcessingStatus.Info:
                if (p.CurrentFile is not null)
                    StatusText = p.CurrentFile;
                AddLog(new LogEntry
                {
                    Kind         = LogEntryKind.Info,
                    OriginalName = p.Description ?? "",
                });
                break;

            case ProcessingStatus.Complete:
                ProgressValue = 100;
                ProgressText  = "Complete";
                StatusText    = $"Finished — {p.CountProcessed} tagged, {p.CountSkipped} skipped" +
                                (p.CountFailed > 0 ? $", {p.CountFailed} failed" : "") +
                                $"  ({p.Elapsed:mm\\:ss})";
                IsComplete    = true;

                // Local notification — flash taskbar and play sound
                Services.NotificationService.NotifyComplete(Application.Current.MainWindow);

                // Discord notification — report real counts, not total iterated
                if (DiscordWebhookEnabled && !string.IsNullOrWhiteSpace(DiscordWebhookUrl))
                {
                    var summary = new System.Text.StringBuilder();
                    summary.Append($"✅ **ImageTagger** — processing complete in {p.Elapsed:mm\\:ss}\n");
                    summary.Append($"🏷️ Tagged: **{p.CountProcessed}**");
                    if (p.CountSkipped > 0) summary.Append($"  ⏭️ Skipped: **{p.CountSkipped}**");
                    if (p.CountFailed  > 0) summary.Append($"  ❌ Failed: **{p.CountFailed}**");
                    _ = Services.NotificationService.SendDiscordMessageAsync(
                        DiscordWebhookUrl, summary.ToString());
                }
                break;

            case ProcessingStatus.Starting:
                ProgressText = "Starting undo…";
                break;
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private ProcessingOptions BuildOptions() => new()
    {
        SourceFolder         = SourceFolder,
        CacheFolder          = CacheFolder,
        ForceTag             = ForceTag,
        ForceRename          = ForceRename,
        Language             = SelectedLanguage,
        OllamaModel          = SelectedModel,
        OllamaUrl            = OllamaUrl,
        MinWidth             = MinWidth,
        MinHeight            = MinHeight,
        OllamaTimeoutSeconds = OllamaTimeoutSec,
        CondensedMaxWords    = CondensedMaxWords,
    };

    private void AddLog(LogEntry entry)
    {
        // LogEntries is observed on the UI thread; Progress<T> already marshals here
        LogEntries.Insert(0, entry);          // newest at top
        while (LogEntries.Count > 1000)       // keep memory bounded
            LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    private void RefreshUndoItems()
    {
        // Unsubscribe from old items before clearing
        foreach (var item in UndoItems)
            item.PropertyChanged -= OnUndoItemSelectionChanged;

        UndoItems.Clear();
        if (!Directory.Exists(SourceFolder)) return;

        _cache.SetCacheFolder(CacheFolder);
        foreach (var item in _processor.GetUndoItems(SourceFolder))
        {
            item.PropertyChanged += OnUndoItemSelectionChanged;
            UndoItems.Add(item);
        }

        RefreshUndoCommands();
    }

    /// <summary>
    /// Re-evaluates undo commands whenever any item's IsSelected changes,
    /// so the Undo Selected button reacts immediately to checkbox clicks.
    /// </summary>
    private void OnUndoItemSelectionChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.UndoItem.IsSelected))
            RefreshUndoCommands();
    }

    private void OpenCacheFolder()
    {
        var folder = string.IsNullOrWhiteSpace(CacheFolder)
            ? _cache.CurrentCacheFolder
            : CacheFolder;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", folder);
        }
        catch { /* ignore if explorer fails */ }
    }

    private void RefreshStartCommand() =>
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    private void RefreshUndoCommands()
    {
        (UndoSelectedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UndoAllCommand      as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
