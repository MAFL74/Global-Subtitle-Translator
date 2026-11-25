using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Gst.Core;

public sealed class GoogleNmtTranslationProvider : ITranslationProvider
{
    private readonly string? _apiKey;
    private readonly ServiceAccountCredential? _credential;
    private readonly HttpClient _httpClient;

    public GoogleNmtTranslationProvider(string apiKeyFile, HttpClient? httpClient = null)
    {
        var contents = File.ReadAllText(apiKeyFile).Trim();
        if (contents.StartsWith("{"))
        {
            var credential = GoogleCredential.FromJson(contents).CreateScoped("https://www.googleapis.com/auth/cloud-translation");
            _credential = credential.UnderlyingCredential as ServiceAccountCredential
                ?? throw new InvalidOperationException("Credential file is not a service account credential.");
        }
        else
        {
            _apiKey = contents;
            if (string.IsNullOrWhiteSpace(_apiKey)) throw new InvalidOperationException("Google credential file is empty.");
        }
        _httpClient = httpClient ?? new HttpClient();
    }

    public string Name => "Google Cloud Translation NMT";

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var result = new List<SubtitleCue>(request.Cues.Count);
        foreach (var batch in request.Cues.Chunk(100))
        {
            using var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            batchTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            var body = new Dictionary<string, object>
            {
                ["q"] = batch.Select(cue => cue.Text).ToArray(),
                ["target"] = ToGoogleLanguage(request.LanguagePair.TargetLanguage),
                ["format"] = "text"
            };
            if (!request.LanguagePair.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
                body["source"] = ToGoogleLanguage(request.LanguagePair.SourceLanguage);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2");
            if (_credential is not null)
                requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken));
            else
                requestMessage.RequestUri = new Uri($"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(_apiKey!)}");
            requestMessage.Content = JsonContent.Create(body);
            using var response = await _httpClient.SendAsync(requestMessage, batchTimeout.Token);
            var json = await response.Content.ReadAsStringAsync(batchTimeout.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Google Translation API failed ({(int)response.StatusCode}): {json}");

            using var document = JsonDocument.Parse(json);
            var translations = document.RootElement.GetProperty("data").GetProperty("translations");
            if (translations.GetArrayLength() != batch.Length) throw new InvalidOperationException("Google returned a different number of translations than requested.");
            for (var i = 0; i < batch.Length; i++)
                result.Add(batch[i] with { Text = translations[i].GetProperty("translatedText").GetString() ?? string.Empty });
        }
        return result;
    }

    private static string ToGoogleLanguage(string language) => language.ToLowerInvariant() switch
    {
        "nb" or "nn" => "no",
        "zh-cn" => "zh-CN",
        "zh-tw" => "zh-TW",
        _ => language
    };
}
