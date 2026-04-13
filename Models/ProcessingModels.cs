namespace ImageTagger.Models;

// ── Ollama ────────────────────────────────────────────────────────────────────

public class OllamaModel
{
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

// ── Processing options (passed into the processing service) ───────────────────

public class ProcessingOptions
{
    public string SourceFolder        { get; set; } = "";
    public string CacheFolder         { get; set; } = "";
    public bool   ForceTag            { get; set; }
    public bool   ForceRename         { get; set; }
    public string Language            { get; set; } = "English";
    public string OllamaModel         { get; set; } = "";
    public string OllamaUrl           { get; set; } = "http://127.0.0.1:11434";
    public int    MinWidth            { get; set; } = 3840;
    public int    MinHeight           { get; set; } = 2160;
    public int    OllamaTimeoutSeconds{ get; set; } = 120;
    public int    CondensedMaxWords   { get; set; } = 5;
}

// ── Progress reporting ────────────────────────────────────────────────────────

public enum ProcessingPhase  { Idle, Scanning, Caching, Processing, Undoing, Complete }
public enum ProcessingStatus { Starting, Scanning, Caching, Analysing,
                               Processed, Skipped, Failed, Info, Complete }

public class ProcessingProgress
{
    public ProcessingPhase  Phase          { get; init; }
    public ProcessingStatus Status         { get; init; }
    public int              Total          { get; init; }
    public int              Current        { get; init; }
    public int              CountProcessed { get; init; }
    public int              CountSkipped   { get; init; }
    public int              CountFailed    { get; init; }
    public string?          CurrentFile    { get; init; }
    public string?          NewName        { get; init; }
    public string?          Description    { get; init; }
    public string?          ErrorMessage   { get; init; }
    public TimeSpan         Elapsed        { get; init; }
}

// ── Log entries (displayed in the activity panel) ─────────────────────────────

public enum LogEntryKind   { Info, Processed, Skipped, Failed }
public enum WebhookStatus  { Unchecked, Checking, Valid, Invalid }

public class LogEntry
{
    public DateTime      Timestamp    { get; init; } = DateTime.Now;
    public LogEntryKind  Kind         { get; init; }
    public string        OriginalName { get; init; } = "";
    public string?       NewName      { get; init; }
    public string?       Description  { get; init; }
    public string?       ErrorMessage { get; init; }

    public string TimeText    => Timestamp.ToString("HH:mm:ss");

    public string PrimaryText => Kind switch
    {
        LogEntryKind.Processed when NewName is not null => $"{OriginalName}  →  {NewName}",
        LogEntryKind.Processed                          => OriginalName,
        LogEntryKind.Skipped                            => $"{OriginalName}   (already tagged)",
        LogEntryKind.Failed                             => $"{OriginalName}   FAILED",
        _                                               => OriginalName,
    };

    public string? SecondaryText => Kind switch
    {
        LogEntryKind.Processed => Description,
        LogEntryKind.Failed    => ErrorMessage,
        _                      => null,
    };
}

// ── Undo list entries (displayed in the Undo panel) ───────────────────────────

public class UndoItem : System.ComponentModel.INotifyPropertyChanged
{
    public string CacheKey     { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string CurrentName  { get; init; } = "";
    public string Status       { get; init; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string DisplayText => CurrentName != OriginalName
        ? $"{OriginalName}  →  {CurrentName}"
        : OriginalName;

    public string StatusBadge => Status switch
    {
        "processed" => "✓",
        "skipped"   => "–",
        "failed"    => "✗",
        "undone"    => "↩",
        _           => "·",
    };
}
