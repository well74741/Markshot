namespace Markshot.Services;

public interface ITranslationProvider
{
    Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken);

    /// <summary>Translates several independent segments; the result has the same length and order.</summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> segments, string targetLanguage, CancellationToken cancellationToken);
}

public static class TranslationService
{
    public static ITranslationProvider Create(TranslationConfig config)
    {
        return new OpenAiCompatibleProvider(config);
    }
}

/// <summary>
/// Talks to any OpenAI-compatible /chat/completions endpoint:
/// DeepSeek, OpenAI, Moonshot, SiliconFlow, Ollama, LM Studio, one-api, ...
/// </summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly TranslationConfig _config;

    public OpenAiCompatibleProvider(TranslationConfig config)
    {
        _config = config;
    }

    public async Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var target = string.IsNullOrWhiteSpace(targetLanguage) ? _config.TargetLanguage : targetLanguage;
        var systemPrompt = string.IsNullOrWhiteSpace(_config.SystemPrompt)
            ? $"You are a professional translator. Translate the user's text into {target}. " +
              "Preserve line breaks, punctuation style and formatting. " +
              "Output only the translation, without quotes or explanations."
            : _config.SystemPrompt.Replace("{target}", target, StringComparison.OrdinalIgnoreCase);
        return await CompleteAsync(systemPrompt, text, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> segments, string targetLanguage, CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var target = string.IsNullOrWhiteSpace(targetLanguage) ? _config.TargetLanguage : targetLanguage;
        var systemPrompt =
            $"You translate text extracted from a screenshot into {target}. " +
            "The user sends a JSON array of strings; each string is an independent paragraph or UI label. " +
            "Reply with ONLY a JSON array of strings: the same length, the same order, one translation per item. " +
            "Never merge, split, drop or explain items. Keep numbers, URLs, code, file names and product names as they are. " +
            $"If an item is already in {target} or cannot be translated, return it unchanged.";
        var reply = await CompleteAsync(
            systemPrompt, JsonSerializer.Serialize(segments, BatchJson), cancellationToken);

        if (TryParseArray(reply, segments.Count, out var parsed))
        {
            return parsed;
        }

        // The model broke the format: fall back to one request per segment.
        Log.Info($"批量翻译返回格式不符，逐条重试（{segments.Count} 条）");
        var results = new string[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            results[i] = await TranslateAsync(segments[i], target, cancellationToken);
        }

        return results;
    }

    private static readonly JsonSerializerOptions BatchJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static bool TryParseArray(string reply, int expected, out IReadOnlyList<string> result)
    {
        result = Array.Empty<string>();
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            var items = JsonSerializer.Deserialize<string[]>(reply[start..(end + 1)]);
            if (items == null || items.Length != expected)
            {
                return false;
            }

            result = items;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.BaseUrl))
        {
            throw new InvalidOperationException("尚未配置翻译服务地址，请打开“设置”填写 Base URL。");
        }

        var isLocal = _config.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                      || _config.BaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_config.ApiKey) && !isLocal)
        {
            throw new InvalidOperationException("尚未配置翻译 API Key，请打开“设置”填写。");
        }

        var payload = new
        {
            model = _config.Model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        var url = _config.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 10, 300)));

        using var response = await Http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"翻译服务返回 {(int)response.StatusCode}：{Truncate(body, 320)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return (content ?? string.Empty).Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法解析翻译服务响应：{Truncate(body, 320)}", ex);
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}
