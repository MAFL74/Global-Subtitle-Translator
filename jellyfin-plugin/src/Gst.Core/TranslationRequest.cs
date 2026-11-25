namespace Gst.Core;

public sealed record TranslationRequest(
    IReadOnlyList<SubtitleCue> Cues,
    LanguagePair LanguagePair,
    string? Context = null);
