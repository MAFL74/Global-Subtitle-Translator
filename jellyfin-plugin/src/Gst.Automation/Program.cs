using System.Text.Json;
using Gst.Core;

if (args.Length < 1) { Console.WriteLine("Bruk: ... <config.json> [--once]"); return 1; }
var configPath = Path.GetFullPath(args[0]);
var runOnce = args.Skip(1).Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase));
var config = JsonSerializer.Deserialize<GstConfig>(await File.ReadAllTextAsync(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Ugyldig GST-konfigurasjon.");
var credentialPath = Path.GetFullPath(config.GoogleCredentialsFile, Path.GetDirectoryName(configPath)!);
var provider = new GoogleNmtTranslationProvider(credentialPath);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.WriteLine($"GST automation: {config.MediaRoot}");
do
{
    await ScanAsync(config, provider, cancellation.Token);
    if (runOnce) break;
    try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.ScanIntervalMinutes)), cancellation.Token); } catch (OperationCanceledException) { }
} while (!cancellation.IsCancellationRequested);
return 0;

static async Task ScanAsync(GstConfig config, ITranslationProvider provider, CancellationToken token)
{
    var mkvs = Directory.EnumerateFiles(Path.GetFullPath(config.MediaRoot), "*.mkv", SearchOption.AllDirectories).ToList();
    foreach (var profile in config.Profiles.Where(p => p.Enabled))
    foreach (var mediaPath in mkvs)
    {
        token.ThrowIfCancellationRequested();
        var output = SubtitleFileNaming.GetTranslatedPath(mediaPath, profile.TargetLanguage);
        if (File.Exists(output)) { Console.WriteLine($"[SKIP] {output}"); continue; }
        var temp = Path.Combine(Path.GetTempPath(), "gst", Guid.NewGuid().ToString("N") + ".srt");
        try
        {
            var extractor = new MkvSubtitleExtractor();
            var track = MkvSubtitleExtractor.ChooseTrack(await extractor.GetTracksAsync(mediaPath, token), profile.SourceLanguage);
            if (track is null) { Console.WriteLine($"[NO SOURCE] {mediaPath}"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await extractor.ExtractAsync(mediaPath, track, temp, token);
            var source = SrtCodec.Read(temp);
            Console.WriteLine($"[START] {mediaPath}: {profile.SourceLanguage} -> {profile.TargetLanguage} ({source.Cues.Count} cues)");
            var translated = await provider.TranslateAsync(new TranslationRequest(source.Cues, new LanguagePair(profile.SourceLanguage, profile.TargetLanguage)), token);
            var temporary = output + ".gst-tmp";
            SrtCodec.Write(temporary, translated);
            File.Move(temporary, output);
            Console.WriteLine($"[DONE] {output}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        { Console.WriteLine($"[ERROR] {mediaPath}: {ex.Message}"); }
        finally { try { File.Delete(temp); } catch { } }
    }
}

public sealed class GstConfig
{
    public string MediaRoot { get; set; } = "/media";
    public string GoogleCredentialsFile { get; set; } = "../secrets/google-translate.json";
    public int ScanIntervalMinutes { get; set; } = 30;
    public List<TranslationProfile> Profiles { get; set; } = new();
}
public sealed class TranslationProfile
{
    public string Name { get; set; } = "Norwegian";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "nb";
    public bool Enabled { get; set; } = true;
}
