using System.Text.Json.Serialization;

namespace Markshot.Core;

/// <summary>Persisted application settings. Stored in %APPDATA%\Markshot\config.json.</summary>
public sealed class AppConfig
{
    public string CaptureHotkey { get; set; } = "F1";
    public string PinHotkey { get; set; } = "F3";

    /// <summary>Empty means Pictures\Markshot.</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>Empty means "use the system/user-profile OCR language".</summary>
    public string OcrLanguage { get; set; } = "";

    public TranslationConfig Translation { get; set; } = new();

    public List<string> PinGroups { get; set; } = new() { "默认" };

    public string CurrentPinGroup { get; set; } = "默认";

    public List<CaptureRegion> RecentRegions { get; set; } = new();

    public void EnsureDefaults()
    {
        PinGroups ??= new List<string>();
        if (PinGroups.Count == 0)
        {
            PinGroups.Add("默认");
        }

        if (string.IsNullOrWhiteSpace(CurrentPinGroup) || !PinGroups.Contains(CurrentPinGroup))
        {
            CurrentPinGroup = PinGroups[0];
        }

        RecentRegions ??= new List<CaptureRegion>();
    }

    public void RememberRegion(Rect region)
    {
        RecentRegions ??= new List<CaptureRegion>();

        var entry = new CaptureRegion
        {
            X = (int)Math.Round(region.X),
            Y = (int)Math.Round(region.Y),
            Width = (int)Math.Round(region.Width),
            Height = (int)Math.Round(region.Height)
        };

        RecentRegions.RemoveAll(existing =>
            Math.Abs(existing.X - entry.X) < 3 &&
            Math.Abs(existing.Y - entry.Y) < 3 &&
            Math.Abs(existing.Width - entry.Width) < 3 &&
            Math.Abs(existing.Height - entry.Height) < 3);

        RecentRegions.Insert(0, entry);
        while (RecentRegions.Count > 10)
        {
            RecentRegions.RemoveAt(RecentRegions.Count - 1);
        }
    }

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Markshot");

    public static string ConfigPath =>
        File.Exists(PortableConfigPath) ? PortableConfigPath : RoamingConfigPath;

    public static string PortableConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string RoamingConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public string ResolveSaveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(SaveDirectory))
        {
            return Environment.ExpandEnvironmentVariables(SaveDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Markshot");
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.Translation ??= new TranslationConfig();
                    config.EnsureDefaults();
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("加载配置失败", ex);
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            WriteTo(ConfigPath);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败", ex);
            try
            {
                WriteTo(PortableConfigPath);
            }
            catch (Exception fallbackError)
            {
                Log.Error("保存便携配置失败", fallbackError);
            }
        }
    }

    private void WriteTo(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public sealed class TranslationConfig{
    /// <summary>OpenAI-compatible base URL, e.g. https://api.deepseek.com/v1</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "deepseek-chat";

    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>Optional prompt override. {target} is replaced with the target language.</summary>
    public string SystemPrompt { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 60;

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        (!string.IsNullOrWhiteSpace(ApiKey) ||
         BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
         BaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase));
}

public sealed class CaptureRegion
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public Rect ToRect() => new(X, Y, Width, Height);
}

public static class Log
{
    private static readonly object Gate = new();

    public static string? LastError { get; private set; }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.LogDirectory);
                var path = Path.Combine(AppConfig.LogDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    File.Delete(path);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null)
                {
                    line += Environment.NewLine + ex;
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception writeError)
        {
            LastError = writeError.ToString();
            TryFallback(writeError, level, message, ex);
        }
    }

    private static void TryFallback(Exception writeError, string level, string message, Exception? ex)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Markshot", "logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex != null)
            {
                line += Environment.NewLine + ex;
            }

            line += Environment.NewLine + "LOG FAILURE: " + writeError;
            File.AppendAllText(Path.Combine(directory, "fallback.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Never throw from logging.
        }
    }
}
