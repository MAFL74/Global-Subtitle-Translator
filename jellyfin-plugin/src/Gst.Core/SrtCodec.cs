using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gst.Core;

public static partial class SrtCodec
{
    public static SrtDocument Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var blocks = Regex.Split(text.Trim(), "\n{2,}");
        var cues = new List<SubtitleCue>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), out var index))
                continue;

            var timing = TimingRegex().Match(lines[1]);
            if (!timing.Success)
                continue;

            cues.Add(new SubtitleCue(
                index,
                TimeSpan.ParseExact(timing.Groups[1].Value, "hh\\:mm\\:ss\\,fff", CultureInfo.InvariantCulture),
                TimeSpan.ParseExact(timing.Groups[2].Value, "hh\\:mm\\:ss\\,fff", CultureInfo.InvariantCulture),
                string.Join(Environment.NewLine, lines.Skip(2))));
        }

        return new SrtDocument { Cues = cues };
    }

    public static void Write(string path, IEnumerable<SubtitleCue> cues)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var cue in cues)
        {
            writer.WriteLine(cue.Index);
            writer.WriteLine($"{Format(cue.Start)} --> {Format(cue.End)}");
            writer.WriteLine(cue.Text);
            writer.WriteLine();
        }
    }

    private static string Format(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";

    [GeneratedRegex("^(\\d{2}:\\d{2}:\\d{2},\\d{3})\\s+-->\\s+(\\d{2}:\\d{2}:\\d{2},\\d{3})$")]
    private static partial Regex TimingRegex();
}
