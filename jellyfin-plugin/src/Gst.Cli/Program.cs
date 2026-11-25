using Gst.Core;

if (args.Length < 3)
{
    Console.WriteLine("Bruk: dotnet run --project src/Gst.Cli -- <input.srt> <source> <target> [output.srt]");
    Console.WriteLine("Eksempel: dotnet run --project src/Gst.Cli -- film.en.srt en nb film.no.srt");
    return 1;
}

var input = Path.GetFullPath(args[0]);
var source = args[1];
var target = args[2];
var output = args.Length >= 4
    ? Path.GetFullPath(args[3])
    : SubtitleFileNaming.GetTranslatedPath(input, target);

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Finner ikke undertekstfilen: {input}");
    return 2;
}

var document = SrtCodec.Read(input);
var provider = new PreviewTranslationProvider();
var request = new TranslationRequest(document.Cues, new LanguagePair(source, target));
var translated = await provider.TranslateAsync(request);
SrtCodec.Write(output, translated);

Console.WriteLine($"Lest {document.Cues.Count} tekstblokker fra {input}");
Console.WriteLine($"Provider: {provider.Name}");
Console.WriteLine($"Språkpar: {source} -> {target}");
Console.WriteLine($"Skrev resultatet til {output}");
return 0;
