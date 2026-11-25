namespace Gst.Core;

public static class SubtitleFileNaming
{
    public static string GetTranslatedPath(string sourcePath, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("A source path is required.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("A target language is required.", nameof(targetLanguage));

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var parts = stem.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // A media filename is its title, not a language-tagged subtitle. Searching
        // every dot-separated token mistakes titles such as "Speak.No.Evil" for
        // a language suffix and then looks for the sidecar under the wrong name.
        if (!Path.GetExtension(sourcePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            && parts.Length > 1
            && IsLanguageTag(parts[^1]))
        {
            stem = string.Join('.', parts[..^1]);
        }

        var outputLanguage = NormalizeOutputLanguage(targetLanguage);
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var provenanceTitle = outputLanguage == "no" ? ".AI" : string.Empty;
        return Path.Combine(directory, $"{stem}.{outputLanguage}{provenanceTitle}.srt");
    }

    private static string NormalizeOutputLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "nb" or "nob" or "nor" or "norwegian" or "norwegian bokmål" => "no",
        _ => language.Trim().ToLowerInvariant()
    };

    private static bool IsLanguageTag(string value) => value.Equals("en", StringComparison.OrdinalIgnoreCase)
        || value.Equals("eng", StringComparison.OrdinalIgnoreCase)
        || value.Equals("english", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nor", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nob", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nno", StringComparison.OrdinalIgnoreCase)
        || value.Equals("norwegian", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nb", StringComparison.OrdinalIgnoreCase)
        || value.Equals("nn", StringComparison.OrdinalIgnoreCase)
        || value.Equals("sv", StringComparison.OrdinalIgnoreCase)
        || value.Equals("swe", StringComparison.OrdinalIgnoreCase)
        || value.Equals("da", StringComparison.OrdinalIgnoreCase)
        || value.Equals("dan", StringComparison.OrdinalIgnoreCase)
        || value.Equals("de", StringComparison.OrdinalIgnoreCase)
        || value.Equals("deu", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ger", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fr", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fra", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fre", StringComparison.OrdinalIgnoreCase)
        || value.Equals("es", StringComparison.OrdinalIgnoreCase)
        || value.Equals("spa", StringComparison.OrdinalIgnoreCase)
        || value.Equals("it", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ita", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fi", StringComparison.OrdinalIgnoreCase)
        || value.Equals("fin", StringComparison.OrdinalIgnoreCase);
}
