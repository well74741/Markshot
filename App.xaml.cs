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
    private long _lastErrorShown = -10000;
    private HotkeyManager? _hotkeys;
    private TrayService? _tray;
    private UI.Recording.RecorderController? _recorder;

    /// <summary>录屏 (one recorder for the app).</summary>
    public UI.Recording.RecorderController Recorder =>
        _recorder ??= new UI.Recording.RecorderController(Config.Recorder, () => Config.Save());
    private bool _capturing;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info("OnStartup begin");

        // A taskbar-menu item of the recorder (Markshot.exe --recorder stop …): hand it to the running Markshot.
        var recorderIndex = Array.IndexOf(e.Args, "--recorder");
        if (recorderIndex >= 0)
        {
            if (recorderIndex + 1 < e.Args.Length)
            {
                UI.Recording.RecorderCommands.Send(e.Args[recorderIndex + 1]);
            }

            Shutdown();
            return;
        }

        // Pictures named on the command line (打开方式 in Explorer) open in the image editor.
        var files = OpenRequests.ImagesIn(e.Args);
        _mutex = new Mutex(true, @"Local\Markshot.SingleInstance", out var createdNew);
        if (!createdNew && files.Count > 0 && OpenRequests.Send(files))
        {
            Shutdown(); // the running Markshot opens them
            return;
        }

        if (!createdNew)
        {
            UI.ThemedMessage.Show(null, "提示", "Markshot 已经在运行，请使用系统托盘图标。", "确定");
            Shutdown();
            return;
        }

        Config = AppConfig.Load();
        ImageFile.Quality = Math.Clamp(Config.SaveQuality, 10, 100);
        _savedCorner = Config.CaptureCornerRadius;
        _savedSoftness = Config.PenSoftness;
        Theme.Apply(Config.Theme);
        UiScale.Apply(Config.UiSize);
        UiScale.HookPopups();

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
            args.Handled = true;
            // Shown later and at most once every few seconds: an error inside layout / rendering repeats
            // every frame, and a modal box inside that loop froze and then crashed the app.
            if (Environment.TickCount64 - _lastErrorShown > 5000)
            {
                _lastErrorShown = Environment.TickCount64;
                var message = args.Exception.Message;
                Dispatcher.BeginInvoke(new Action(() => _tray?.ShowBalloon("Markshot 出错了", message + "（详情见日志，可用托盘“导出诊断信息”反馈）")),
                    DispatcherPriority.ApplicationIdle);
            }
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
            () => _pins.Any(pin => pin.IsClickThrough),
            RecorderMenuItems,
            ExportDiagnostics,
            ShowPinHistory,
            () => OpenEditor(),
            ToggleHotkeys);

        ApplyHotkeys();
        StartUpdateChecks();
        UI.Recording.RecorderCommands.Listen(Dispatcher, command => _recorder?.Command(command));
        OpenRequests.Listen(Dispatcher, OpenFileInEditor);
        ApplyOpenWith();

        _tray.ShowBalloon(
            "Markshot 已启动",
            $"截图 {Config.CaptureHotkey} · {PinHotkeyLabel}\n右键托盘图标打开设置");

        Log.Info($"Markshot 启动，截图键 {Config.CaptureHotkey}，{PinHotkeyLabel}");

        foreach (var file in files)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenFileInEditor(file)), DispatcherPriority.ApplicationIdle);
        }

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

    /// <param name="selection">Start with this area selected (screen pixels), e.g. coming back from 长截图.</param>
    private double _savedCorner;
    private int _savedSoftness;

    /// <summary>What is remembered from use (the capture's rounded corners, the pen's 软边) goes to disk when it changed.</summary>
    private void SaveRemembered()
    {
        if (Math.Abs(_savedCorner - Config.CaptureCornerRadius) > 0.01 || _savedSoftness != Config.PenSoftness)
        {
            _savedCorner = Config.CaptureCornerRadius;
            _savedSoftness = Config.PenSoftness;
            Config.Save();
        }
    }

    public void StartCapture(bool demo = false, Int32Rect? selection = null)
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
            var cursor = CursorCapture.Grab(bounds.X, bounds.Y);
            var overlay = new OverlayWindow(image, bounds, demo)
            {
                ActiveWindow = WindowDetector.ActiveProgram(), // the program in use: its elements are offered first
                // The pin is created while the capture is still on screen (no gap, no flicker).
                InitialSelection = selection,
                CursorShot = cursor,
                PinNow = (source, output) => WithHistory(CreatePin(output, (int)source.SelectionScreenRect.X, (int)source.SelectionScreenRect.Y,
                    source.PinBaseImage, source.PinAnnotations, source.PinCornerRadius, fromCapture: true, shapedShadow: source.PinShapedShadow), source)
            };
            overlay.ShowDialog();
            if (overlay.RangeChanged)
            {
                Config.Save(); // the 识别范围 the wheel chose
            }

            SaveRemembered();

            if (overlay.Result == CaptureAction.Edit && overlay.PinBaseImage != null)
            {
                var editor = OpenEditor(overlay.PinBaseImage, annotations: overlay.PinAnnotations);
                if (overlay.PinHistory != null)
                {
                    editor.AdoptHistory(overlay.PinHistory); // Ctrl+Z goes on with the capture's steps
                }

                return;
            }

            if (overlay.Result == CaptureAction.Scroll)
            {
                var area = overlay.SelectionScreenRect;
                StartScrollCapture(new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height));
                return;
            }

            if (overlay.Result == CaptureAction.Record)
            {
                // 录屏 from the capture toolbar: record the selected area.
                var area = overlay.SelectionScreenRect;
                Dispatcher.BeginInvoke(new Action(() => OpenRecorder(new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height), fromCapture: true)),
                    DispatcherPriority.Background);
                return;
            }

            if (overlay.PinnedDirectly)
            {
                Config.RememberRegion(overlay.SelectionScreenRect);
                Config.Save();
                return;
            }

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
                case CaptureAction.Save when overlay.SavedPath != null:
                    ShowSavedPrompt(overlay.SavedPath);
                    break;
                case CaptureAction.Pin:
                    WithHistory(CreatePin(output, (int)overlay.SelectionScreenRect.X, (int)overlay.SelectionScreenRect.Y,
                        overlay.PinBaseImage, overlay.PinAnnotations, overlay.PinCornerRadius), overlay);
                    break;
                case CaptureAction.Translate:
                    ShowTranslation(output);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("截图失败", ex);
            UI.ThemedMessage.Show(null, "出错了", "截图失败：" + ex.Message, "确定");
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
        BitmapSource? baseImage = null, List<Annotations.Annotation>? annotations = null, double cornerRadius = 0,
        bool fromCapture = false, bool shapedShadow = false, bool sweep = false, (double Zoom, double Opacity)? view = null)
    {
        while (_pins.Count >= MaxPins)
        {
            // Not the animated close: it is deferred and this loop would never end.
            var oldest = _pins[0];
            oldest.CloseImmediately();
            _pins.Remove(oldest);
        }

        var pin = new PinWindow(image, screenX, screenY, baseImage, annotations)
        {
            CornerRadius = cornerRadius,
            ShapedShadow = shapedShadow,
            FromCapture = fromCapture,
            Sweep = sweep
        };

        pin.Closed += (_, _) =>
        {
            _pins.Remove(pin);
            if (!_shuttingDown && !pin.SkipHistory)
            {
                var entry = new PinEntry { Image = pin.SourceImage };
                History.Add(entry);
                PinStore.Save(entry, pin.SourceImage);
                _closedPins.Add((pin.Snapshot(), entry));
                _pinHistoryWindow?.Refresh();
                if (_closedPins.Count > 30)
                {
                    _closedPins.RemoveAt(0);
                }
            }
        };

        if (view is { } shown)
        {
            pin.RestoreView(shown.Zoom, shown.Opacity); // before it shows: it opens at its size (no jump from 100 %)
        }

        _pins.Add(pin);
        pin.Show();
        return pin;
    }

    public int PinCount => _pins.Count;

    /// <summary>After an image was saved: offer to open its folder (unless turned off).</summary>
    public void ShowSavedPrompt(string path)
    {
        if (!Config.AskOpenFolderAfterSave || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        new SavedPromptWindow(path).Show();
    }

    /// <summary>Settings window: the shadow sliders preview on every open pin (null = back to the saved values).</summary>
    public void PreviewPinShadow((double Size, string Color, double Opacity)? shadow)
    {
        foreach (var pin in _pins)
        {
            pin.PreviewShadow(shadow);
        }
    }

    public void CreatePinAtCursor(BitmapSource image, bool sweep = false)
    {
        var cursor = Native.CursorPosition();
        CreatePin(image, cursor.X + 16, cursor.Y + 16, sweep: sweep);
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
            CreatePin(snapshot.Image, snapshot.Left, snapshot.Top,
                snapshot.BaseImage, snapshot.Annotations.Count > 0 ? snapshot.Annotations : null, sweep: true, view: (snapshot.Zoom, snapshot.Opacity));
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
        CreatePinAtCursor(image, sweep: true);
    }

    private UI.PinHistoryWindow? _pinHistoryWindow;

    /// <summary>Tray 贴图历史…: thumbnails of the recently closed pins.</summary>
    public void ShowPinHistory()
    {
        if (_pinHistoryWindow != null)
        {
            _pinHistoryWindow.Refresh();
            _pinHistoryWindow.Activate();
            return;
        }

        _pinHistoryWindow = new UI.PinHistoryWindow(PinHistoryEntries, RestorePinEntry, DeletePinEntry, ClearPinHistory);
        _pinHistoryWindow.Closed += (_, _) => _pinHistoryWindow = null;
        _pinHistoryWindow.Show();
    }

    public IReadOnlyList<PinEntry> PinHistoryEntries() => History.Items;

    /// <summary>Pins a history entry again: as it was (annotations, zoom) when closed in this session.</summary>
    public void RestorePinEntry(PinEntry entry)
    {
        var index = _closedPins.FindIndex(closed => ReferenceEquals(closed.Entry, entry));
        History.Remove(entry);
        if (index >= 0)
        {
            var (snapshot, _) = _closedPins[index];
            _closedPins.RemoveAt(index);
            CreatePin(snapshot.Image, snapshot.Left, snapshot.Top,
                snapshot.BaseImage, snapshot.Annotations.Count > 0 ? snapshot.Annotations : null, sweep: true, view: (snapshot.Zoom, snapshot.Opacity));
            return;
        }

        if (entry.LoadImage() is { } image)
        {
            CreatePinAtCursor(image, sweep: true);
        }
    }

    public void DeletePinEntry(PinEntry entry)
    {
        History.Remove(entry);
        _closedPins.RemoveAll(closed => ReferenceEquals(closed.Entry, entry));
        PinStore.Delete(entry);
    }

    public void ClearPinHistory()
    {
        foreach (var entry in History.Items.ToList())
        {
            PinStore.Delete(entry);
        }

        History.Clear();
        _closedPins.Clear();
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
            var package = await UpdateService.FindNewestAsync(null); // the online releases
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

        _settings = new SettingsWindow(Config);
        // The global hotkeys keep working with the settings open; they rest only while a hotkey box takes
        // keys (pressing F1 there sets the field instead of opening the capture overlay).
        _settings.RecordingHotkey += recording =>
        {
            if (recording)
            {
                _hotkeys?.UnregisterAll();
            }
            else if (!_shuttingDown)
            {
                ApplyHotkeys();
            }
        };
        // 保存 applies at once and the window stays; closing drops what was changed but not saved.
        _settings.SavedChanges += () =>
        {
            Config.Save();
            PreviewPinShadow(null);
            foreach (var pin in _pins)
            {
                pin.ApplyShadowSettings();
            }

            foreach (var editor in _editors)
            {
                editor.ApplyToolbarOrder();
            }

            _tray?.SetTooltip($"Markshot · {Config.CaptureHotkey} 截图 / {PinHotkeyLabel}");
            if (!_shuttingDown)
            {
                ApplyHotkeys();
            }
        };
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            PreviewPinShadow(null); // drop an unsaved preview
            // Back to the saved look (the 样式 page previews at once).
            Theme.Apply(Config.Theme);
            UiScale.Apply(Config.UiSize);
            foreach (var pin in _pins)
            {
                pin.ApplyShadowSettings();
            }

            if (!_shuttingDown)
            {
                ApplyHotkeys();
            }
        };
        _settings.Show();
        // In front once when opened (from the tray); after that like any window, other programs can cover it.
        _settings.Topmost = true;
        _settings.Activate();
        _settings.Topmost = false;
    }

    /// <summary>Tray 导出诊断信息: logs + config (no API key) + system report, zipped on the desktop.</summary>
    private void ExportDiagnostics()
    {
        try
        {
            var path = Diagnostics.Export();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            _tray?.ShowBalloon("诊断信息已导出", Path.GetFileName(path) + "（已放在桌面，不含 API Key）");
        }
        catch (Exception ex)
        {
            Log.Error("导出诊断信息失败", ex);
            UI.ThemedMessage.Show(null, "提示", "导出诊断信息失败：" + ex.Message, "确定");
        }
    }

    /// <summary>The tray menu's 录屏 entries follow the recorder: 录屏 → 开始 / 停止 / 暂停 / 继续 / 退出录屏.</summary>
    private IEnumerable<(string Header, Action Action)> RecorderMenuItems()
    {
        var phase = _recorder?.Phase ?? UI.Recording.RecordPhase.Idle;
        var r = _recorder;
        switch (phase)
        {
            case UI.Recording.RecordPhase.Idle:
                yield return ("录屏", () => OpenRecorder());
                yield break;
            case UI.Recording.RecordPhase.Setup:
                yield return ("开始录屏", () => r!.Command("start"));
                break;
            case UI.Recording.RecordPhase.Countdown or UI.Recording.RecordPhase.Starting:
                yield return ("取消开始", () => r!.Command("stop"));
                break;
            case UI.Recording.RecordPhase.Recording:
                yield return ("停止录屏", () => r!.Command("stop"));
                yield return ("暂停录屏", () => r!.Command("pause"));
                break;
            case UI.Recording.RecordPhase.Paused:
                yield return ("停止录屏", () => r!.Command("stop"));
                yield return ("继续录屏", () => r!.Command("pause"));
                break;
            default:
                yield return ("显示录屏", () => r!.Command("show"));
                break;
        }

        if (phase is UI.Recording.RecordPhase.Setup or UI.Recording.RecordPhase.Preview)
        {
            yield return ("录屏区域回中", () => r!.Recenter());
        }

        yield return ("退出录屏", () => r!.Command("exit"));
    }

    private readonly List<UI.ImageEditorWindow> _editors = new();

    /// <summary>Opens the image editor (empty, or on an image with its editable annotations).</summary>
    /// <summary>A pin made from a capture goes on with the capture's undo steps.</summary>
    private static UI.PinWindow WithHistory(UI.PinWindow pin, UI.OverlayWindow source)
    {
        if (source.PinHistory != null)
        {
            pin.AdoptHistory(source.PinHistory);
        }

        return pin;
    }

    /// <summary>A picture from 打开方式 / a second start: a new editor window, brought to the front.</summary>
    public void OpenFileInEditor(string path)
    {
        var editor = OpenEditor();
        if (!editor.OpenFile(path))
        {
            editor.CloseWithoutAsking();
            return;
        }

        // Another program had the foreground: a moment of topmost brings the editor up front.
        editor.Topmost = true;
        editor.Activate();
        editor.Topmost = false;
    }

    /// <summary>The 打开方式 entry follows the setting (and the program's current path).</summary>
    public void ApplyOpenWith()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith("Markshot.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (Config.RegisterOpenWith)
            {
                FileAssociation.Register(exe);
            }
            else if (FileAssociation.RegisteredCommand() != null)
            {
                FileAssociation.Unregister(exe);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("登记打开方式失败", ex);
        }
    }

    public UI.ImageEditorWindow OpenEditor(BitmapSource? image = null, string? path = null,
        List<Annotations.Annotation>? annotations = null, UI.PinWindow? sourcePin = null)
    {
        var editor = new UI.ImageEditorWindow(image, path, annotations, sourcePin);
        _editors.Add(editor);
        editor.Closed += (_, _) => _editors.Remove(editor);
        editor.Show();
        editor.Activate();
        return editor;
    }

    /// <summary>
    /// 贴到屏幕 from the editor: where the pin it came from was (that one is replaced), else in the middle of
    /// the given monitor; shrunk to fit. Annotations stay editable in the pin.
    /// </summary>
    public PinWindow PinFromEditor(BitmapSource baseImage, List<Annotations.Annotation> annotations, BitmapSource composed,
        PinWindow? replace, Int32Rect work)
    {
        var fit = Math.Min(1, Math.Min((work.Width - 16) / (double)composed.PixelWidth, (work.Height - 16) / (double)composed.PixelHeight));
        var x = work.X + (work.Width - (int)(composed.PixelWidth * fit)) / 2;
        var y = work.Y + (work.Height - (int)(composed.PixelHeight * fit)) / 2;
        if (replace != null && _pins.Contains(replace))
        {
            var snapshot = replace.Snapshot();
            x = snapshot.Left;
            y = snapshot.Top;
            fit = Math.Min(fit, snapshot.Zoom);
            replace.Close();
        }

        return CreatePin(composed, x, y, baseImage, annotations.Count > 0 ? annotations : null, shapedShadow: ImageOps.HasTransparentCorner(composed), sweep: true,
            view: (fit, 1));
    }

    /// <summary>A save dialog for an image, then the usual "saved" prompt.</summary>
    public void SaveImageAs(BitmapSource image)
    {
        var path = UI.SaveImageDialog.Ask(null, "保存图片", $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png", picture: image);
        if (path == null)
        {
            return;
        }

        try
        {
            ImageFile.Save(image, path);
            ShowSavedPrompt(path);
        }
        catch (Exception ex)
        {
            Log.Error("保存图片失败", ex);
            UI.ThemedMessage.Show(null, "保存失败", ex.Message, "确定");
        }
    }

    /// <summary>长截图 on an area; it ends in the editor, the clipboard, a file or back in the capture.</summary>
    public void StartScrollCapture(Int32Rect area)
    {
        if (area.Width < 16 || area.Height < 16)
        {
            return;
        }

        var session = new UI.ScrollCaptureSession(area, Config.ScrollHorizontal, (action, image) =>
        {
            switch (action)
            {
                case UI.ScrollCaptureAction.Edit when image != null:
                    OpenEditor(image);
                    break;
                case UI.ScrollCaptureAction.Copy when image != null:
                    ClipboardService.CopyImage(image);
                    _tray?.ShowBalloon("长截图已复制", $"{image.PixelWidth} × {image.PixelHeight}");
                    break;
                case UI.ScrollCaptureAction.Save when image != null:
                    SaveImageAs(image);
                    break;
                case UI.ScrollCaptureAction.Cancel:
                    // Back to the capture with the same area, once the session's windows are off the screen.
                    _ = Task.Delay(150).ContinueWith(_ => StartCapture(selection: area), TaskScheduler.FromCurrentSynchronizationContext());
                    break;
            }
        });
        session.DirectionChanged += horizontal =>
        {
            Config.ScrollHorizontal = horizontal;
            Config.Save();
        };
        session.Start();
    }

    /// <summary>Opens the recorder (on an area, e.g. the capture selection).</summary>
    public void OpenRecorder(Int32Rect? area = null, bool fromCapture = false)
    {
        if (_capturing)
        {
            return;
        }

        try
        {
            Recorder.Open(area, fromCapture);
        }
        catch (Exception ex)
        {
            Log.Error("打开录屏失败", ex);
            UI.ThemedMessage.Show(null, "出错了", "打开录屏失败：" + ex.Message, "确定");
        }
    }

    /// <summary>禁用所有快捷键 on / off (tray menu): saved, applied at once.</summary>
    public void ToggleHotkeys()
    {
        Config.HotkeysDisabled = !Config.HotkeysDisabled;
        Config.Save();
        ApplyHotkeys();
        _tray?.ShowBalloon(Config.HotkeysDisabled ? "已禁用所有快捷键" : "快捷键已恢复",
            Config.HotkeysDisabled ? "截图、贴图等全局快捷键和 Win + 鼠标都已停用，可从托盘菜单恢复。" : "全局快捷键和 Win + 鼠标重新可用。");
    }

    private WinMouseSelect? _winMouse;

    /// <summary>Win + mouse button draws an area at once (setting); the action per button comes from the settings.</summary>
    private void StartWinMouse()
    {
        if (!Config.WinMouseEnabled)
        {
            _winMouse?.Stop();
            return;
        }

        _winMouse ??= new WinMouseSelect(WinMouseDone);
        _winMouse.Start();
    }

    private void WinMouseDone(MouseButton button, Int32Rect area)
    {
        var action = button switch
        {
            MouseButton.Left => Config.WinMouseLeft,
            MouseButton.Middle => Config.WinMouseMiddle,
            _ => Config.WinMouseRight
        };
        if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase) || area.Width < 2 || area.Height < 2)
        {
            return;
        }

        BitmapSource image;
        try
        {
            image = ScreenCapture.CaptureRegion(area);
        }
        catch (Exception ex)
        {
            Log.Error("Win + 鼠标截取失败", ex);
            return;
        }

        switch (action)
        {
            case "Ocr":
                ShowOcr(image);
                break;
            case "Pin":
                CreatePin(image, area.X, area.Y, sweep: true);
                break;
            default:
                if (ClipboardService.CopyImage(image))
                {
                    _tray?.ShowBalloon("已复制", $"{area.Width} × {area.Height} 的区域已复制到剪贴板。");
                }

                break;
        }
    }

    public void ApplyHotkeys()
    {
        if (_hotkeys == null)
        {
            return;
        }

        if (Config.HotkeysDisabled)
        {
            // 禁用所有快捷键: nothing global is listened to (full-screen games).
            _hotkeys.RegisterAll();
            _winMouse?.Stop();
            return;
        }

        StartWinMouse();
        var bindings = new List<(string Gesture, Action Action)>
        {
            (Config.CaptureHotkey, () => StartCapture())
        };

        if (!string.IsNullOrWhiteSpace(Config.CloseAllPinsHotkey))
        {
            bindings.Add((Config.CloseAllPinsHotkey, CloseAllPins));
        }

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

        if (!string.IsNullOrWhiteSpace(Config.RecordHotkey))
        {
            bindings.Add((Config.RecordHotkey, () => Recorder.Toggle()));
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
        _recorder?.Close();

        foreach (var pin in _pins.ToArray())
        {
            pin.Close();
        }

        foreach (var editor in _editors.ToArray())
        {
            editor.CloseWithoutAsking();
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
        SaveRemembered();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
