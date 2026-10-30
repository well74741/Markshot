namespace Markshot.Services;

public interface ITranslationProvider
{
    Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken);
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

        var target = string.IsNullOrWhiteSpace(targetLanguage) ? _config.TargetLanguage : targetLanguage;
        var systemPrompt = string.IsNullOrWhiteSpace(_config.SystemPrompt)
            ? $"You are a professional translator. Translate the user's text into {target}. " +
              "Preserve line breaks, punctuation style and formatting. " +
              "Output only the translation, without quotes or explanations."
            : _config.SystemPrompt.Replace("{target}", target, StringComparison.OrdinalIgnoreCase);

        var payload = new
        {
            model = _config.Model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = text }
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
