using Gst.Core;

if (args.Length != 3)
{
    Console.WriteLine("Bruk: dotnet run --project src/Gst.GoogleTool -- <input.srt> <source> <target>");
    return 1;
}

var input = Path.GetFullPath(args[0]);
var source = args[1];
var target = args[2];
var keyFile = Path.Combine(Environment.CurrentDirectory, "secrets", "google-translate.json");
var output = SubtitleFileNaming.GetTranslatedPath(input, target);
var document = SrtCodec.Read(input);
var provider = new GoogleNmtTranslationProvider(keyFile);
var translated = await provider.TranslateAsync(new TranslationRequest(document.Cues, new LanguagePair(source, target)));
SrtCodec.Write(output, translated);
Console.WriteLine($"Oversatte {translated.Count} cues med {provider.Name}");
Console.WriteLine($"Skrev: {output}");
return 0;
