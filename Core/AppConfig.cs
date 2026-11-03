using System.Text.Json.Serialization;

namespace Markshot.Core;

/// <summary>Persisted application settings. Stored in %APPDATA%\Markshot\config.json.</summary>
public sealed class AppConfig
{
    public string CaptureHotkey { get; set; } = "F1";
    public string PinHotkey { get; set; } = "F3";

    public string MouseThroughHotkey { get; set; } = "Ctrl+Alt+M";

    public string RestorePinHotkey { get; set; } = "Ctrl+Alt+Z";

    public string TextBypassModifier { get; set; } = "Alt";

    public string PinFlipHorizontalHotkey { get; set; } = "Q";

    public string PinFlipVerticalHotkey { get; set; } = "W";

    public string PinRotateHotkey { get; set; } = "E";

    public string PinResetHotkey { get; set; } = "F";

    public string CaptureToolOrder { get; set; } = "Shape,Pen,Arrow,Text,Number,Mosaic,Eraser";

    public string CaptureToolHidden { get; set; } = "";

    public string PinToolOrder { get; set; } = "Shape,Pen,Arrow,Text,Number,Mosaic,Eraser";

    public string PinToolHidden { get; set; } = "";

    /// <summary>Order / hidden of the action buttons on the right of the annotation bar.</summary>
    public string CaptureActionOrder { get; set; } = "Scroll,Record,Ocr,Close,Pin,Save,Copy,Translate,Edit";

    public string CaptureActionHidden { get; set; } = "";

    public string PinActionOrder { get; set; } = "Save,Apply,Edit";

    public string PinActionHidden { get; set; } = "";

    /// <summary>长截图 scrolls sideways (otherwise down).</summary>
    public bool ScrollHorizontal { get; set; }

    /// <summary>录屏: opens the recorder (start / stop while it is open). Empty = no hotkey.</summary>
    public string RecordHotkey { get; set; } = "";

    public RecorderConfig Recorder { get; set; } = new();

    public string ToolPenHotkey { get; set; } = "B";

    public string ToolSelectHotkey { get; set; } = "V";

    public string ToolEraserHotkey { get; set; } = "E";

    public string ToolTextHotkey { get; set; } = "T";

    public string ToolNumberHotkey { get; set; } = "S";

    public string ToolLineHotkey { get; set; } = "L";

    public string ToolShapeHotkey { get; set; } = "M";

    /// <summary>Interface colours: Dark (default), Light or System (follow Windows).</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Interface size: Medium (default) or Large (everything one step larger).</summary>
    public string UiSize { get; set; } = "Medium";

    /// <summary>Pin shadow: blur size (DIP, 0 = none), colour (#RRGGBB) and opacity (0–1).</summary>
    public double PinShadowSize { get; set; } = 12;

    public string PinShadowColor { get; set; } = "#6A6A6A";

    public double PinShadowOpacity { get; set; } = 0.47;

    /// <summary>Copying a capture / pin also puts a PNG file on the clipboard (paste in Explorer).</summary>
    public bool CopyImageAsFile { get; set; } = true;

    /// <summary>After saving an image, ask whether to open its folder.</summary>
    public bool AskOpenFolderAfterSave { get; set; } = true;

    /// <summary>Translate every capture selection in place (the toolbar's translate toggle remembers it).</summary>
    public bool ImmersiveTranslate { get; set; }

    /// <summary>Empty means Pictures\Markshot.</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>Folder with installer packages for updates; empty = where the installer ran from.</summary>
    public string UpdatePath { get; set; } = "";

    /// <summary>Empty means "use the system/user-profile OCR language".</summary>
    public string OcrLanguage { get; set; } = "";

    public TranslationConfig Translation { get; set; } = new();

    public List<CaptureRegion> RecentRegions { get; set; } = new();

    public void EnsureDefaults()
    {
        RecentRegions ??= new List<CaptureRegion>();
        Recorder ??= new RecorderConfig();
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

    public static string ConfigPath
    {
        get
        {
            if (File.Exists(PortableConfigPath))
            {
                return PortableConfigPath;
            }

            // During development the executable lives under bin/...; also look
            // for the published portable config in a nearby publish folder.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 5 && directory != null; i++)
            {
                var candidate = Path.Combine(directory.FullName, "publish", "config.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return RoamingConfigPath;
        }
    }

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

    /// <summary>The key in plain text (memory only; saved encrypted as <see cref="ApiKeyProtected"/>).</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    /// <summary>Encrypted for this Windows user (DPAPI); another PC / user cannot read it.</summary>
    public string ApiKeyProtected
    {
        get => Secrets.Protect(ApiKey);
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                ApiKey = Secrets.Unprotect(value);
            }
        }
    }

    /// <summary>Older configs kept the key in plain text: read it once, never write it again.</summary>
    [JsonPropertyName("ApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyApiKey
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(ApiKey))
            {
                ApiKey = value;
            }
        }
    }

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

/// <summary>Remembered 录屏 choices (the key display is not remembered: it starts off every time).</summary>
public sealed class RecorderConfig
{
    public int Fps { get; set; } = 30;

    public bool DelayOn { get; set; }

    public int Delay { get; set; } = 3;

    public bool SystemAudio { get; set; } = true;

    public bool Microphone { get; set; }

    /// <summary>Where recordings are saved / copied; empty = Videos\Markshot 录屏.</summary>
    public string Directory { get; set; } = "";

    public bool ExportGif { get; set; }

    /// <summary>0 = 流畅, 1 = 清晰, 2 = 高清.</summary>
    public int Quality { get; set; } = 1;

    /// <summary>Export frame rate; 0 = the recording's.</summary>
    public int ExportFps { get; set; }

    /// <summary>Last recording area (physical pixels).</summary>
    public int LastX { get; set; }

    public int LastY { get; set; }

    public int LastWidth { get; set; }

    public int LastHeight { get; set; }
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
