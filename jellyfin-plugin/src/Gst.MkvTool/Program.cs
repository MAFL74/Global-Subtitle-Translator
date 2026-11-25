using Gst.Core;

if (args.Length < 1)
{
    Console.WriteLine("Bruk: dotnet run --project src/Gst.MkvTool -- <film.mkv> [kilde] [arbeidsmappe]");
    return 1;
}

var mediaPath = Path.GetFullPath(args[0]);
var sourceLanguage = args.Length >= 2 ? args[1] : "en";
var workDirectory = args.Length >= 3 ? Path.GetFullPath(args[2]) : Path.Combine(Environment.CurrentDirectory, "test", "extracted");
var extractor = new MkvSubtitleExtractor();
var tracks = await extractor.GetTracksAsync(mediaPath);
Console.WriteLine($"Fant {tracks.Count} innebygde undertekstspor:");
foreach (var track in tracks) Console.WriteLine($"  stream {track.StreamIndex}: {track.Language} {track.Title}");

var selected = MkvSubtitleExtractor.ChooseTrack(tracks, sourceLanguage);
if (selected is null) { Console.WriteLine($"Fant ikke kilde: {sourceLanguage}"); return 2; }
var output = Path.Combine(workDirectory, Path.GetFileNameWithoutExtension(mediaPath) + $".{sourceLanguage}.extracted.srt");
await extractor.ExtractAsync(mediaPath, selected, output);
Console.WriteLine($"Valgte stream {selected.StreamIndex} ({selected.Language}, {selected.Title ?? "uten tittel"})");
Console.WriteLine($"Extracted tekst til: {output}");
return 0;
