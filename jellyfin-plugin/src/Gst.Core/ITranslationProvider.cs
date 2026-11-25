namespace Gst.Core;

public interface ITranslationProvider
{
    string Name { get; }

    Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
