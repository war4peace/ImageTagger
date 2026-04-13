using System.IO;
using System.Text.Json;

namespace ImageTagger.Models;

/// <summary>User preferences, persisted to %AppData%\ImageTagger\settings.json.</summary>
public class AppSettings
{
    public string OllamaUrl           { get; set; } = "http://127.0.0.1:11434";
    public string SelectedModel       { get; set; } = "";
    public string Language            { get; set; } = "English";
    public string LastSourceFolder    { get; set; } = "";
    public string CacheFolder         { get; set; } = "";   // empty = use default AppData location
    public bool   ForceTag            { get; set; } = false;
    public bool   ForceRename         { get; set; } = false;
    public int    MinWidth            { get; set; } = 0;   // 0 = no filter
    public int    MinHeight           { get; set; } = 0;
    public int    CondensedMaxWords   { get; set; } = 5;
    public int    OllamaTimeoutSeconds  { get; set; } = 120;
    public bool   DiscordWebhookEnabled { get; set; } = false;
    public string DiscordWebhookUrl     { get; set; } = "";

    // ── Persistence ──────────────────────────────────────────────────────────

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImageTagger", "settings.json");

    private static readonly JsonSerializerOptions _opts =
        new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(
                           File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* return defaults on any error */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, _opts));
        }
        catch { /* best-effort */ }
    }
}
