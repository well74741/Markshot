using System.Text.Json.Serialization;

namespace Markshot.Core;

/// <summary>Persisted application settings. Stored in %APPDATA%\Markshot\config.json.</summary>
public sealed class AppConfig
{
    public string CaptureHotkey { get; set; } = "F1";
    public string PinHotkey { get; set; } = "F3";

    public string MouseThroughHotkey { get; set; } = "Ctrl+Alt+M";

    public string RestorePinHotkey { get; set; } = "Ctrl+Alt+Z";

    /// <summary>关闭所有贴图 (global; empty = none).</summary>
    public string CloseAllPinsHotkey { get; set; } = "Shift+Esc";

    /// <summary>禁用所有快捷键 (full-screen games): no global hotkeys, no Win + mouse.</summary>
    public bool HotkeysDisabled { get; set; }

    /// <summary>Win + 鼠标键 draws an area on the screen at once (no dimming, no hint) and does what is set for that button.</summary>
    public bool WinMouseEnabled { get; set; } = true;

    /// <summary>What Win + left / middle / right does: "Ocr" (识别文字), "Pin" (贴图), "Copy" (复制) or "None".</summary>
    public string WinMouseLeft { get; set; } = "Ocr";

    public string WinMouseMiddle { get; set; } = "Pin";

    public string WinMouseRight { get; set; } = "Copy";

    public string TextBypassModifier { get; set; } = "Alt";

    /// <summary>贴图上的文字可直接拖选复制 (a text layer from OCR); off: a drag on text moves the pin.</summary>
    public bool PinTextSelect { get; set; } = true;

    public string PinFlipHorizontalHotkey { get; set; } = "Q";

    public string PinFlipVerticalHotkey { get; set; } = "W";

    public string PinRotateHotkey { get; set; } = "E";

    public string PinResetHotkey { get; set; } = "F";

    public string CaptureToolOrder { get; set; } = "Shape,Pen,Arrow,Text,Number,Mosaic,Eraser";

    public string CaptureToolHidden { get; set; } = "";

    public string PinToolOrder { get; set; } = "Shape,Pen,Arrow,Text,Number,Mosaic,Eraser";

    public string PinToolHidden { get; set; } = "";

    /// <summary>Order / hidden of the action buttons on the right of the annotation bar.</summary>
    public string CaptureActionOrder { get; set; } = "Scroll,Record,Ocr,Close,Clear,Pin,Save,Translate,Edit,Copy";

    public string CaptureActionHidden { get; set; } = "";

    public string PinActionOrder { get; set; } = "Close,Save,Clear,Edit,Apply,Copy";

    public string PinActionHidden { get; set; } = "";

    /// <summary>The image editor's floating bar: tools and its own buttons (open, save, copy, pin).</summary>
    public string EditorToolOrder { get; set; } = "Shape,Pen,Arrow,Text,Number,Mosaic,Eraser";

    public string EditorToolHidden { get; set; } = "";

    public string EditorActionOrder { get; set; } = "New,Paste,Open,Save,Clear,Pin,Copy";

    public string EditorActionHidden { get; set; } = "";

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
    public string Theme { get; set; } = "Light"; // (0.8.63: a first start is light; a choice is kept)

    /// <summary>Interface size: Medium (default) or Large (everything one step larger).</summary>
    /// <summary>界面大小: Small (as designed, default) / Medium / Large. Stored before 0.8.48: "Medium" = 小, "Large" = 中.</summary>
    public string UiSize { get; set; } = "Medium"; // (0.8.63: a first start is 中)

    /// <summary>The meaning of <see cref="UiSize"/> moved in 0.8.48 (old 中 → 小, old 大 → 中); set once that is done.</summary>
    public int UiSizeVersion { get; set; } = 2;

    /// <summary>Pin shadow: blur size (DIP, 0 = none), colour (#RRGGBB) and opacity (0–1).</summary>
    public double PinShadowSize { get; set; } = 12;

    public string PinShadowColor { get; set; } = "#6A6A6A";

    public double PinShadowOpacity { get; set; } = 0.47;

    /// <summary>Copying a capture / pin also puts a PNG file on the clipboard (paste in Explorer).</summary>
    public bool CopyImageAsFile { get; set; } = true;

    /// <summary>Markshot is offered in Explorer's 打开方式 for pictures (refreshed on every start).</summary>
    public bool RegisterOpenWith { get; set; } = true;

    /// <summary>截图: the mouse pointer is part of the screenshot (toggled with <see cref="CaptureCursorKey"/>, remembered).</summary>
    public bool CaptureShowCursor { get; set; }

    /// <summary>截图: shows / hides the mouse pointer in the screenshot.</summary>
    public string CaptureCursorKey { get; set; } = "Ctrl+M";

    /// <summary>截图: shows / hides the crosshair through the mouse (a lone modifier = tap it).</summary>
    public string CrosshairKey { get; set; } = "Alt";

    /// <summary>截图识别: hovering frames the window under the mouse.</summary>
    public bool DetectWindows { get; set; } = true;

    /// <summary>截图识别: hovering frames the UI element under the mouse (text, picture, table, button …).</summary>
    public bool DetectElements { get; set; } = true;

    /// <summary>自动识别截图区域 as a whole (元素 / 窗口 are its two parts).</summary>
    public bool DetectAuto { get; set; } = true;

    /// <summary>
    /// 识别范围 1…4: what the frame takes first. 1 窗口 (the window only), 2 大区块 (large blocks), 3 常规元素
    /// (pictures, cards, paragraphs, tables; no lone buttons or icons), 4 细小元素 (everything). The wheel still
    /// steps out to the window and back in from there.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int DetectRange { get; set; } = 3;

    /// <summary>The 识别范围 the wheel picks in a capture is kept for the next one (off: each capture starts at the set range).</summary>
    public bool DetectRangeRemember { get; set; } = true;

    /// <summary>The 0.8.33–0.8.37 识别灵敏度 (1…5), read once to set <see cref="DetectRange"/>; not written any more.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("DetectSensitivity")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacySensitivity
    {
        get => null;
        set
        {
            if (value is { } old && !_rangeRead)
            {
                DetectRange = old switch { <= 1 => 2, >= 5 => 4, _ => 3 };
            }
        }
    }

    private bool _rangeRead;

    [System.Text.Json.Serialization.JsonPropertyName("DetectRange")]
    public int DetectRangeStored
    {
        get => DetectRange;
        set
        {
            DetectRange = value;
            _rangeRead = true;
        }
    }

    /// <summary>The frame's slide to the new element (ms); it starts at once, nothing waits for the pointer to rest.</summary>
    public const int HoverSlide = 80;

    /// <summary>Edge contrast the picture-based detection needs.</summary>
    public static int EdgeThresholdFor(int range) => Math.Clamp(range, 1, 4) >= 4 ? 14 : 28;

    /// <summary>Smallest element (pixels, either side) the detection offers.</summary>
    public static int MinSideFor(int range) => Math.Clamp(range, 1, 4) >= 4 ? 3 : 4;

    /// <summary>
    /// The smallest frame taken first at a range (physical pixels and the share of the window's area): 2 大区块 takes
    /// blocks of at least 1/12 of the window, 3 常规元素 things at least ~40 DIP on their short side or a wide line of text.
    /// </summary>
    public static bool StartsAt(int range, Rect frame, Rect window, double scale)
    {
        var area = frame.Width * frame.Height;
        var windowArea = Math.Max(1, window.Width * window.Height);
        return Math.Clamp(range, 1, 4) switch
        {
            1 => false,
            2 => area >= windowArea / 12 && Math.Min(frame.Width, frame.Height) >= 120 * scale,
            3 => Math.Min(frame.Width, frame.Height) >= 40 * scale || (frame.Width >= 240 * scale && frame.Height >= 16 * scale),
            _ => true
        };
    }

    /// <summary>After saving an image, ask whether to open its folder.</summary>
    public bool AskOpenFolderAfterSave { get; set; } = true;

    /// <summary>贴图历史 window size as last left (DIP; 0 = the default).</summary>
    public double PinHistoryWidth { get; set; }

    public double PinHistoryHeight { get; set; }

    /// <summary>图片编辑器: the panel sections left folded (their titles).</summary>
    public string EditorFoldedSections { get; set; } = "";

    /// <summary>图片编辑器 移动吸附: moved elements snap to the canvas and each other.</summary>
    public bool EditorSnap { get; set; } = true;

    /// <summary>The editor's 适应内容 margin (pixels).</summary>
    public int EditorFitMargin { get; set; }

    /// <summary>JPEG / WebP quality last chosen in the save window.</summary>
    public int SaveQuality { get; set; } = 92;

    /// <summary>快速双击: a double-click on a capture or pin copies and closes it, whatever tool is in hand.</summary>
    public bool QuickDoubleClickCopy { get; set; }

    /// <summary>The capture area's rounded corners (pixels) as last adjusted; the next capture starts with them.</summary>
    public double CaptureCornerRadius { get; set; }

    /// <summary>The pen's 软边 as last set (0…100 % of the brush width is feathered).</summary>
    public int PenSoftness { get; set; }

    /// <summary>The 圆角 of new rectangles, as last set.</summary>
    public double RectCornerRadius { get; set; }

    /// <summary>软边 of the eraser (0–100): how much of its width erases only partly, fading to the edge.</summary>
    public int EraserSoftness { get; set; }

    /// <summary>序号: 空心 (ring) instead of a filled disc.</summary>
    public bool NumberHollow { get; set; }

    /// <summary>序号: a light outline around the badge.</summary>
    public bool NumberOutlined { get; set; } = true;

    /// <summary>文字的字体 (empty = Microsoft YaHei UI).</summary>
    public string TextFont { get; set; } = "";

    /// <summary>The ▾ beside the tool buttons is hidden; the tool's styles open when the pointer rests on the tool.</summary>
    public bool HideToolChevrons { get; set; } = true;

    /// <summary>截图里按一次 Esc 直接退出；关掉时逐步退出（先放下工具 / 取消选中，再取消选区，最后退出）。</summary>
    public bool EscExitsCapture { get; set; } = true;

    /// <summary>Translate every capture selection in place (the toolbar's translate toggle remembers it).</summary>
    public bool ImmersiveTranslate { get; set; }

    /// <summary>Empty means Pictures\Markshot.</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>Folder with installer packages for updates; empty = where the installer ran from.</summary>

    /// <summary>Empty means "use the system/user-profile OCR language".</summary>
    public string OcrLanguage { get; set; } = "";

    public TranslationConfig Translation { get; set; } = new();

    public List<CaptureRegion> RecentRegions { get; set; } = new();

    /// <summary>The version that wrote this file last (empty before 0.8.53): an update shows what is new once.</summary>
    public string LastVersion { get; set; } = "";

    /// <summary>悬浮按钮: a round button on the desktop (click = capture, hover = a row of actions).</summary>
    public bool FloatButtonEnabled { get; set; } = true;

    /// <summary>The user has switched the floating button on / off once (until then it is on).</summary>
    public bool FloatButtonChosen { get; set; }

    /// <summary>What a click on the floating button does: one of its row's actions.</summary>
    public string FloatClickAction { get; set; } = "Capture";

    /// <summary>Where the floating button was left (physical screen pixels); int.MinValue = the default, bottom right.</summary>
    public int FloatButtonX { get; set; } = int.MinValue;

    public int FloatButtonY { get; set; } = int.MinValue;

    /// <summary>The floating button's row: Capture (截屏), Record (录屏), Ocr (文字识别), Editor (图片编辑器).</summary>
    public string FloatActionOrder { get; set; } = "Capture,Record,Ocr,Editor,History,AddPin,TogglePins";

    /// <summary>显示贴图 is offered but off until the user turns it on.</summary>
    public string FloatActionHidden { get; set; } = "TogglePins";

    /// <summary>The floating button hides while a full-screen program (video, game) is in front.</summary>
    public bool FloatHideFullscreen { get; set; } = true;

    /// <summary>截图时隐藏: out of the way while a capture is open (never in the screenshot either way).</summary>
    public bool FloatHideWhileCapturing { get; set; } = true;

    /// <summary>The floating button's own picture (a file in the config folder; empty = the Markshot logo). Also the tray icon.</summary>
    public string FloatIcon { get; set; } = "";

    /// <summary>The floating button's size: 1 (as designed, the smallest) to 2 (twice as big).</summary>
    public double FloatIconSize { get; set; } = 1;

    /// <summary>The floating button's opacity (0.2–1).</summary>
    public double FloatIconOpacity { get; set; } = 1;

    public void EnsureDefaults()
    {
        if (UiSizeVersion < 2)
        {
            // Old 中 (designed size) is 小 now, old 大 is 中.
            UiSize = string.Equals(UiSize, "Large", StringComparison.OrdinalIgnoreCase) ? "Medium" : "Small";
            UiSizeVersion = 2;
        }

        RecentRegions ??= new List<CaptureRegion>();
        Recorder ??= new RecorderConfig();
        if (!FloatButtonChosen)
        {
            FloatButtonEnabled = true; // on until the user turns it off
        }

        if (!FloatActionOrder.Contains("TogglePins", StringComparison.Ordinal))
        {
            // 显示贴图 came in 0.8.61: there, but off until turned on.
            FloatActionOrder += ",TogglePins";
            FloatActionHidden = string.IsNullOrWhiteSpace(FloatActionHidden) ? "TogglePins" : FloatActionHidden + ",TogglePins";
        }

        // An order that is still an old default (never rearranged) takes the new default: 复制 on the far right.
        var fresh = new AppConfig();
        if (CaptureActionOrder == "Scroll,Record,Ocr,Close,Clear,Pin,Save,Copy,Translate,Edit")
        {
            CaptureActionOrder = fresh.CaptureActionOrder;
        }

        if (PinActionOrder is "Close,Save,Clear,Apply,Copy,Edit" or "Close,Save,Clear,Apply,Edit")
        {
            PinActionOrder = fresh.PinActionOrder;
        }

        if (EditorActionOrder == "Paste,Open,Save,Clear,Copy,Pin")
        {
            EditorActionOrder = fresh.EditorActionOrder;
        }
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

    /// <summary>Tests point the config somewhere of their own (never the user's file).</summary>
    public static string? PathOverride { get; set; }

    public static string ConfigPath
    {
        get
        {
            if (PathOverride != null)
            {
                return PathOverride;
            }

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

    /// <summary>True when Load found a settings file (a first start has none).</summary>
    public static bool LoadedExisting { get; private set; }

    public static AppConfig Load()
    {
        string? json = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                LoadedExisting = true;
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
            if (json != null)
            {
                // Never start over from the defaults because one value does not read (it was saved over the
                // whole file at the next save): keep a copy and take every setting that still reads.
                try
                {
                    File.Copy(ConfigPath, Path.Combine(Path.GetDirectoryName(ConfigPath) ?? ".", $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                }
                catch (Exception copyError)
                {
                    Log.Error("备份配置失败", copyError);
                }

                var salvaged = Salvage(json);
                salvaged.Translation ??= new TranslationConfig();
                salvaged.EnsureDefaults();
                return salvaged;
            }
        }

        return new AppConfig();
    }

    /// <summary>A settings file that does not read as a whole: each setting on its own, the ones that fail keep their default.</summary>
    public static AppConfig Salvage(string json)
    {
        var config = new AppConfig();
        System.Text.Json.Nodes.JsonObject? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }

        if (root == null)
        {
            return config;
        }

        foreach (var property in typeof(AppConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var ignore = (JsonIgnoreAttribute?)Attribute.GetCustomAttribute(property, typeof(JsonIgnoreAttribute));
            if (ignore is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            var name = ((JsonPropertyNameAttribute?)Attribute.GetCustomAttribute(property, typeof(JsonPropertyNameAttribute)))?.Name ?? property.Name;
            var node = root.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (node == null)
            {
                continue;
            }

            try
            {
                property.SetValue(config, node.Deserialize(property.PropertyType, JsonOptions));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                Log.Error($"配置项 {name} 无法读取，已用默认值");
            }
        }

        return config;
    }

    /// <summary>Settings were saved (anything prepared from them ahead — the spare capture window — is made again).</summary>
    public static event Action? Saved;

    public void Save()
    {
        try
        {
            WriteTo(ConfigPath);
            Saved?.Invoke();
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
        AtomicFile.WriteAllText(path, json);
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

    /// <summary>翻译方式: Auto (the API; the offline pack when no key is set), Api, Offline (离线翻译包).</summary>
    public string Engine { get; set; } = "Auto";

    /// <summary>Where offline packs are downloaded from (<see cref="Services.OfflinePacks.Mirrors"/>).</summary>
    public string OfflineMirror { get; set; } = "hf-mirror";

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

/// <summary>Files written whole or not at all: to a temporary file beside it, then moved over the old one.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
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
