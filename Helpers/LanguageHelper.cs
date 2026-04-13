namespace ImageTagger.Helpers;

public static class LanguageHelper
{
    private static readonly Dictionary<string, string> _codes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AF"] = "Afrikaans",   ["SQ"] = "Albanian",    ["AR"] = "Arabic",
            ["HY"] = "Armenian",    ["AZ"] = "Azerbaijani", ["EU"] = "Basque",
            ["BE"] = "Belarusian",  ["BN"] = "Bengali",     ["BS"] = "Bosnian",
            ["BG"] = "Bulgarian",   ["CA"] = "Catalan",     ["ZH"] = "Chinese",
            ["HR"] = "Croatian",    ["CS"] = "Czech",       ["DA"] = "Danish",
            ["NL"] = "Dutch",       ["EN"] = "English",     ["ET"] = "Estonian",
            ["FI"] = "Finnish",     ["FR"] = "French",      ["GL"] = "Galician",
            ["KA"] = "Georgian",    ["DE"] = "German",      ["EL"] = "Greek",
            ["HE"] = "Hebrew",      ["HI"] = "Hindi",       ["HU"] = "Hungarian",
            ["IS"] = "Icelandic",   ["ID"] = "Indonesian",  ["GA"] = "Irish",
            ["IT"] = "Italian",     ["JA"] = "Japanese",    ["KK"] = "Kazakh",
            ["KO"] = "Korean",      ["LV"] = "Latvian",     ["LT"] = "Lithuanian",
            ["MK"] = "Macedonian",  ["MS"] = "Malay",       ["MT"] = "Maltese",
            ["NB"] = "Norwegian",   ["FA"] = "Persian",     ["PL"] = "Polish",
            ["PT"] = "Portuguese",  ["RO"] = "Romanian",    ["RU"] = "Russian",
            ["SR"] = "Serbian",     ["SK"] = "Slovak",      ["SL"] = "Slovenian",
            ["ES"] = "Spanish",     ["SW"] = "Swahili",     ["SV"] = "Swedish",
            ["TH"] = "Thai",        ["TR"] = "Turkish",     ["UK"] = "Ukrainian",
            ["UR"] = "Urdu",        ["VI"] = "Vietnamese",  ["CY"] = "Welsh",
        };

    /// <summary>
    /// All language names, English first, the rest alphabetically sorted.
    /// Bound directly to the Language ComboBox.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { "English" }
            .Concat(_codes.Values.Where(v => v != "English").OrderBy(v => v))
            .ToArray();

    /// <summary>
    /// Resolve an ISO 639-1 code or full name to a display name.
    /// Unknown strings are returned title-cased (e.g. "klingon" → "Klingon").
    /// </summary>
    public static string Resolve(string codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName)) return "English";
        var s = codeOrName.Trim();
        if (_codes.TryGetValue(s, out var byCode)) return byCode;
        foreach (var v in _codes.Values)
            if (string.Equals(v, s, StringComparison.OrdinalIgnoreCase)) return v;
        return char.ToUpper(s[0]) + s[1..];
    }
}
