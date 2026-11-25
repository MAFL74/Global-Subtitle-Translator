namespace Gst.Core;

/// <summary>Test provider used to verify the complete workflow without an API key.</summary>
public sealed class PreviewTranslationProvider : ITranslationProvider
{
    public string Name => "Preview (no online API)";

    public Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubtitleCue> result = request.Cues.Select(cue =>
            cue with { Text = $"[{request.LanguagePair.TargetLanguage}] {cue.Text}" }).ToList();
        return Task.FromResult(result);
    }
}
