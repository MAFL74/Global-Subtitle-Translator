namespace Gst.Core;

public sealed record LanguagePair(string SourceLanguage, string TargetLanguage)
{
    public override string ToString() => $"{SourceLanguage} -> {TargetLanguage}";
}
