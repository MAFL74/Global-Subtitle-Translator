using Gst.Core;

if (args.Length < 3)
{
    Console.WriteLine("Bruk: dotnet run --project src/Gst.TranslateTool -- <film.mkv> <kilde> <mål> [output-mappe]");
    Console.WriteLine("Eksempel: ... -- film.mkv en nb");
    return 1;
}

var mediaPath = Path.GetFullPath(args[0]);
var source = args[1].ToLowerInvariant();
var target = args[2].ToLowerInvariant();
var outputDirectory = args.Length >= 4 ? Path.GetFullPath(args[3]) : Path.GetDirectoryName(mediaPath)!;
var keyFile = Path.Combine(Environment.CurrentDirectory, "secrets", "google-translate.json");
var temporaryDirectory = Path.Combine(Path.GetTempPath(), "gst", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
using var overallTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

try
{
    var extractor = new MkvSubtitleExtractor();
    var tracks = await extractor.GetTracksAsync(mediaPath, overallTimeout.Token);
    var selected = MkvSubtitleExtractor.ChooseTrack(tracks, source);
    if (selected is null) { Console.Error.WriteLine($"Fant ikke tekstspor for kilde '{source}'."); return 2; }

    var extractedPath = Path.Combine(temporaryDirectory, "source.srt");
    await extractor.ExtractAsync(mediaPath, selected, extractedPath, overallTimeout.Token);
    var document = SrtCodec.Read(extractedPath);
    var provider = new GoogleNmtTranslationProvider(keyFile);
    Console.WriteLine($"Oversetter {document.Cues.Count} cues med {provider.Name}: {source} -> {target}");
    var translated = await provider.TranslateAsync(new TranslationRequest(document.Cues, new LanguagePair(source, target)), overallTimeout.Token);

    Directory.CreateDirectory(outputDirectory);
    var output = SubtitleFileNaming.GetTranslatedPath(Path.Combine(outputDirectory, Path.GetFileName(mediaPath)), target);
    var temporaryOutput = output + ".gst-tmp";
    SrtCodec.Write(temporaryOutput, translated);
    File.Move(temporaryOutput, output, true);
    Console.WriteLine($"Ferdig: {output}");
    return 0;
}
finally
{
    try { Directory.Delete(temporaryDirectory, true); } catch { }
}
