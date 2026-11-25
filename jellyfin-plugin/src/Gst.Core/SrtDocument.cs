namespace Gst.Core;

public sealed class SrtDocument
{
    public required IReadOnlyList<SubtitleCue> Cues { get; init; }

    public static SrtDocument Empty => new() { Cues = Array.Empty<SubtitleCue>() };
}
