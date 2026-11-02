using Markshot.Services;
using Markshot.UI;

namespace Markshot;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();

    public PinHistory History { get; } = new();

    private static string PinHotkeyLabel =>
        string.IsNullOrWhiteSpace(Config.PinHotkey) ? "贴图未设快捷键" : $"贴图 {Config.PinHotkey}";

    private readonly List<PinWindow> _pins = new();
    private readonly List<Window> _translations = new();

    private const int MaxPins = 20;

    // Pins closed in this session, newest last: restore reopens them exactly (place, zoom,
    // editable annotations). The disk history covers pins closed in earlier sessions.
    private readonly List<(PinSnapshot Snapshot, PinEntry Entry)> _closedPins = new();

    private Mutex? _mutex;
    private HotkeyManager? _hotkeys;
    private TrayService? _tray;
    private bool _capturing;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info("OnStartup begin");

        _mutex = new Mutex(true, @"Local\Markshot.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Markshot 已经在运行，请使用系统托盘图标。", "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Config = AppConfig.Load();

        foreach (var entry in PinStore.Load())
        {
            History.Add(entry);
        }

        if (e.Args.Contains("--logtest"))
        {
            RunLogTest();
            return;
        }

        var ocrIndex = Array.IndexOf(e.Args, "--ocr");
        if (ocrIndex >= 0 && ocrIndex + 1 < e.Args.Length)
        {
            RunServiceTest(e.Args[ocrIndex + 1], false);
            return;
        }

        var translateIndex = Array.IndexOf(e.Args, "--translate");
        if (translateIndex >= 0 && translateIndex + 1 < e.Args.Length)
        {
            RunServiceTest(e.Args[translateIndex + 1], true);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的 UI 异常", args.Exception);
            MessageBox.Show("发生错误：" + args.Exception.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的异常", args.ExceptionObject as Exception);

        _hotkeys = new HotkeyManager();
        _tray = new TrayService(
            () => StartCapture(),
            PinFromClipboard,
            RestoreLastPin,
            ShowSettings,
            DisableClickThrough,
            ShutdownApp,
            () => _closedPins.Count > 0 || History.Last != null,
            () => _pins.Any(pin => pin.IsClickThrough));

        ApplyHotkeys();
        StartUpdateChecks();

        _tray.ShowBalloon(
            "Markshot 已启动",
            $"截图 {Config.CaptureHotkey} · {PinHotkeyLabel}\n右键托盘图标打开设置");

        Log.Info($"Markshot 启动，截图键 {Config.CaptureHotkey}，{PinHotkeyLabel}");

        var arguments = e.Args;
        if (arguments.Contains("--settings"))
        {
            Dispatcher.BeginInvoke(new Action(ShowSettings), DispatcherPriority.ApplicationIdle);
        }
        else if (arguments.Contains("--capture") || arguments.Contains("--demo"))
        {
            var demo = arguments.Contains("--demo");
            Dispatcher.BeginInvoke(new Action(() => StartCapture(demo)), DispatcherPriority.ApplicationIdle);
        }
    }

    private void RunLogTest()
    {
        Log.Info("logtest");
        var info = $"dir={AppConfig.LogDirectory}{Environment.NewLine}error={Log.LastError ?? "(none)"}{Environment.NewLine}";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "logtest.txt"), info, Encoding.UTF8);
        Shutdown();
    }

    private async void RunServiceTest(string imagePath, bool translate)
    {
        try
        {
            var fullPath = Path.GetFullPath(imagePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(fullPath);
            image.EndInit();
            image.Freeze();

            var text = await OcrService.RecognizeAsync(image, Config.OcrLanguage);

            var output = new StringBuilder();
            output.AppendLine("=== OCR ===");
            output.AppendLine(text);

            if (translate)
            {
                var provider = TranslationService.Create(Config.Translation);
                var result = await provider.TranslateAsync(
                    text, Config.Translation.TargetLanguage, CancellationToken.None);
                output.AppendLine();
                output.AppendLine("=== TRANSLATION ===");
                output.AppendLine(result);
            }

            var resultPath = Path.ChangeExtension(fullPath, translate ? ".translate.txt" : ".ocr.txt");
            File.WriteAllText(resultPath, output.ToString(), Encoding.UTF8);
            Log.Info("服务自检完成：" + resultPath);
        }
        catch (Exception ex)
        {
            Log.Error("服务自检失败", ex);
            try
            {
                File.WriteAllText(
                    Path.ChangeExtension(Path.GetFullPath(imagePath), ".error.txt"),
                    ex.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Ignore.
            }
        }
        finally
        {
            Shutdown();
        }
    }

    public void StartCapture(bool demo = false)
    {
        if (_capturing || _shuttingDown)
        {
            return;
        }

        _capturing = true;
        _hotkeys?.UnregisterAll();

        try
        {
            var (image, bounds) = ScreenCapture.CaptureVirtualScreen();
            var overlay = new OverlayWindow(image, bounds, demo);
            overlay.ShowDialog();

            var result = overlay.Result;
            var output = overlay.OutputImage;

            if (result == CaptureAction.None || output == null)
            {
                return;
            }

            if (overlay.SelectionScreenRect.Width >= 2 && overlay.SelectionScreenRect.Height >= 2)
            {
                Config.RememberRegion(overlay.SelectionScreenRect);
                Config.Save();
            }

            switch (result)
            {
                case CaptureAction.Pin:
                    CreatePin(output, (int)overlay.SelectionScreenRect.X, (int)overlay.SelectionScreenRect.Y,
                        overlay.PinBaseImage, overlay.PinAnnotations);
                    break;
                case CaptureAction.Translate:
                    ShowTranslation(output);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("截图失败", ex);
            MessageBox.Show("截图失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _capturing = false;
            if (!_shuttingDown)
            {
                ApplyHotkeys();
            }
        }
    }

    public PinWindow CreatePin(BitmapSource image, int screenX, int screenY,
        BitmapSource? baseImage = null, List<Annotations.Annotation>? annotations = null)
    {
        while (_pins.Count >= MaxPins)
        {
            // Not the animated close: it is deferred and this loop would never end.
            var oldest = _pins[0];
            oldest.CloseImmediately();
            _pins.Remove(oldest);
        }

        var pin = new PinWindow(image, screenX, screenY, baseImage, annotations);

        pin.Closed += (_, _) =>
        {
            _pins.Remove(pin);
            if (!_shuttingDown)
            {
                var entry = new PinEntry { Image = pin.SourceImage };
                History.Add(entry);
                PinStore.Save(entry, pin.SourceImage);
                _closedPins.Add((pin.Snapshot(), entry));
                if (_closedPins.Count > 10)
                {
                    _closedPins.RemoveAt(0);
                }
            }
        };

        _pins.Add(pin);
        pin.Show();
        return pin;
    }

    public int PinCount => _pins.Count;

    public void CreatePinAtCursor(BitmapSource image)
    {
        var cursor = Native.CursorPosition();
        CreatePin(image, cursor.X + 16, cursor.Y + 16);
    }

    public void PinFromClipboard()
    {
        var image = ClipboardService.TryGetImage();
        if (image != null)
        {
            CreatePinAtCursor(image);
            return;
        }

        var text = ClipboardService.TryGetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var card = TextCardRenderer.Render("剪贴板文本", text);
            CreatePinAtCursor(card);
            return;
        }

        _tray?.ShowBalloon("没有可贴的内容", "剪贴板中没有图片或文字。");
    }

    public void ShowTranslation(BitmapSource image)
    {
        var window = new TranslationWindow(image);
        window.Closed += (_, _) => _translations.Remove(window);
        _translations.Add(window);
        window.Show();
        window.Activate();
    }

    public void ShowOcr(BitmapSource image)
    {
        var window = new TranslationWindow(image, autoTranslate: false);
        window.Closed += (_, _) => _translations.Remove(window);
        _translations.Add(window);
        window.Show();
        window.Activate();
    }

    public void TranslateImage(BitmapSource image, Window? owner = null)
    {
        _ = owner;
        ShowTranslation(image);
    }

    /// <summary>
    /// Reopens the most recently closed pin where it was, with its zoom, opacity and editable
    /// annotations. Each call steps one pin further back (it never pins the clipboard).
    /// </summary>
    public void RestoreLastPin()
    {
        if (_closedPins.Count > 0)
        {
            var (snapshot, entry) = _closedPins[^1];
            _closedPins.RemoveAt(_closedPins.Count - 1);
            History.Remove(entry);
            var restored = CreatePin(snapshot.Image, snapshot.Left, snapshot.Top,
                snapshot.BaseImage, snapshot.Annotations.Count > 0 ? snapshot.Annotations : null);
            restored.RestoreView(snapshot.Zoom, snapshot.Opacity);
            return;
        }

        var last = History.Last;
        var image = last?.LoadImage();
        if (last == null || image == null)
        {
            _tray?.ShowBalloon("没有可恢复的贴图", "关闭过的贴图会保留在这里，可随时恢复。");
            return;
        }

        History.Remove(last);
        CreatePinAtCursor(image);
    }

    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private string? _notifiedUpdate;

    /// <summary>Checks the update folder shortly after start and every two hours; reminds once per version.</summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(2);
            var folder = UpdateService.EffectivePath(Config);
            var package = await Task.Run(() => UpdateService.FindNewer(folder));
            if (package != null && package.Version != _notifiedUpdate)
            {
                _notifiedUpdate = package.Version;
                _tray?.ShowBalloon($"Markshot {package.Version} 可以更新", "打开设置，点版本号旁的“更新”即可安装。");
            }
        };
        _updateTimer.Start();
    }

    public void DisableClickThrough()
    {
        foreach (var pin in _pins)
        {
            pin.SetClickThrough(false);
        }
    }

    public void CloseAllPins()
    {
        foreach (var pin in _pins.ToArray())
        {
            pin.CloseImmediately();
        }
    }

    public void ToggleMouseThrough()
    {
        var target = _pins.LastOrDefault(p => p.IsActive) ?? _pins.LastOrDefault();
        target?.SetClickThrough(!target.IsClickThrough);
    }

    private SettingsWindow? _settings;

    /// <summary>
    /// Opens settings as an ordinary window (not modal), so pins stay usable
    /// while it is open. A second request just brings it to the front.
    /// </summary>
    public void ShowSettings()
    {
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }

        // Suspend global hotkeys so pressing F1 inside the recorder updates the
        // field instead of opening the capture overlay.
        _hotkeys?.UnregisterAll();

        _settings = new SettingsWindow(Config);
        _settings.Closed += (_, _) =>
        {
            var saved = _settings?.Saved == true;
            _settings = null;
            if (saved)
            {
                Config.Save();
                _tray?.SetTooltip($"Markshot · {Config.CaptureHotkey} 截图 / {PinHotkeyLabel}");
                _tray?.ShowBalloon("设置已保存", "快捷键与翻译配置已更新。");
            }

            if (!_shuttingDown)
            {
                ApplyHotkeys();
            }
        };
        _settings.Show();
        _settings.Activate();
    }

    public void ApplyHotkeys()
    {
        if (_hotkeys == null)
        {
            return;
        }

        var bindings = new List<(string Gesture, Action Action)>
        {
            (Config.CaptureHotkey, () => StartCapture())
        };

        if (!string.IsNullOrWhiteSpace(Config.PinHotkey))
        {
            bindings.Add((Config.PinHotkey, PinFromClipboard));
        }

        if (!string.IsNullOrWhiteSpace(Config.MouseThroughHotkey))
        {
            bindings.Add((Config.MouseThroughHotkey, ToggleMouseThrough));
        }

        if (!string.IsNullOrWhiteSpace(Config.RestorePinHotkey))
        {
            bindings.Add((Config.RestorePinHotkey, RestoreLastPin));
        }

        var errors = _hotkeys.RegisterAll(bindings.ToArray());

        foreach (var error in errors)
        {
            Log.Error("快捷键注册失败：" + error);
        }

        if (errors.Count > 0)
        {
            _tray?.ShowBalloon("快捷键注册失败", string.Join("\n", errors));
        }
    }

    public void ShutdownApp()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Log.Info("Markshot 退出");

        foreach (var pin in _pins.ToArray())
        {
            pin.Close();
        }

        _hotkeys?.Dispose();
        _hotkeys = null;

        _tray?.Dispose();
        _tray = null;

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // Not owned; ignore.
        }

        _mutex?.Dispose();
        _mutex = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
