using Markshot.Services;
using System.Windows.Interop;

namespace Markshot.UI;

public partial class TranslationWindow : Window
{
    private readonly BitmapSource? _image;
    private readonly bool _autoTranslate;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;
    private bool _closed;

    public TranslationWindow(BitmapSource? image, bool autoTranslate = true)
    {
        InitializeComponent();
        _image = image;
        _autoTranslate = autoTranslate;
        foreach (var language in Languages)
        {
            TargetBox.Items.Add(language);
        }

        TargetBox.SelectedIndex = 0; // 自动
        TargetBox.SelectionChanged += (_, _) => UpdateMeta();
        if (!autoTranslate)
        {
            Title = "Markshot 文字识别";
            HeaderTitle.Text = "文字识别";
            HeaderSubtitle.Text = "本地 OCR · 可编辑后复制或翻译";
        }

        SourceBox.TextChanged += (_, _) => UpdateMeta();
        ResultBox.TextChanged += (_, _) => UpdateMeta();
        Loaded += OnLoaded;
        Theme.TrackTitleBar(this);
        UiScale.AttachWindow(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _ = TranslateAsync();
            e.Handled = true;
        }
    }

    private const string Auto = "自动（中↔英）";

    private static readonly string[] Languages =
    {
        Auto, "简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Deutsch", "Español", "Русский"
    };

    /// <summary>
    /// The language to translate into. Auto: Chinese source → English, anything else → 简体中文.
    /// </summary>
    private string ResolveTarget()
    {
        var chosen = TargetBox.SelectedItem as string ?? Auto;
        if (chosen != Auto)
        {
            return chosen;
        }

        return ImmersiveTranslator.DetectLanguage(SourceBox.Text) == "中文" ? "English" : "简体中文";
    }

    private enum StatusKind
    {
        Idle,
        Busy,
        Done,
        Error
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Busy => (Brush)FindResource("Accent"),
            StatusKind.Done => new SolidColorBrush(Color.FromRgb(0x52, 0xC4, 0x1A)),
            StatusKind.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)),
            _ => Theme.Brush(0xFF5E5E5E)
        };
    }

    private void UpdateMeta()
    {
        var source = SourceBox.Text.Trim();
        SourceMeta.Text = source.Length == 0 ? "识别结果，可编辑" : $"{source.Length} 字 · 可编辑";
        var result = ResultBox.Text.Trim();
        var auto = (TargetBox.SelectedItem as string ?? Auto) == Auto;
        var target = source.Length == 0 && auto ? "自动" : ResolveTarget() + (auto ? "（自动）" : "");
        ResultMeta.Text = result.Length == 0 ? $"译成 {target}" : $"{result.Length} 字 · {target}";
        ResultPlaceholder.Visibility = result.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text))
        {
            return;
        }

        var copied = TableToggle.IsChecked == true && _tables.Count > 0
            ? ClipboardService.CopyTables(string.Join(Environment.NewLine + Environment.NewLine, _tables.Select(table => table.ToTsv())),
                string.Join("<br>", _tables.Select(table => table.ToHtml())))
            : ClipboardService.CopyText(SourceBox.Text);
        if (copied)
        {
            SetStatus(TableToggle.IsChecked == true ? "表格已复制，可直接粘贴到 Excel / Word" : "原文已复制", StatusKind.Done);
        }
        else
        {
            SetStatus("复制失败：剪贴板被占用", StatusKind.Error);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_image == null)
        {
            SetStatus("可直接输入文本后点击“翻译”。", StatusKind.Idle);
            return;
        }

        SetStatus("正在识别文字…", StatusKind.Busy);
        _ = ReadCodesAsync(_image);

        try
        {
            var document = await OcrService.RecognizeDocumentAsync(_image, App.Config.OcrLanguage);
            if (_closed)
            {
                return;
            }

            var text = document.Text;
            SourceBox.Text = text;
            ShowTables(document.Tables);
            // 公式 only when the pack is there and what was read looks like mathematics (not for ordinary text).
            FormulaButton.Visibility = OcrPacks.FormulaInstalled && LooksLikeFormula(text) ? Visibility.Visible : Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("未识别到文字，可手动输入后翻译。", StatusKind.Idle);
                return;
            }

            if (_autoTranslate)
            {
                await TranslateAsync();
            }
            else
            {
                SetStatus("识别完成，可编辑、复制或翻译。", StatusKind.Done);
            }
        }
        catch (Exception ex)
        {
            Log.Error("OCR 识别失败", ex);
            if (!_closed)
            {
                SetStatus("识别失败：" + ex.Message, StatusKind.Error);
            }
        }
    }

    private string? _textBeforeFormula;

    /// <summary>公式: the picture read as a formula (LaTeX) by the downloaded pack; again: back to the text.</summary>
    private async void Formula_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null || _busy)
        {
            return;
        }

        if (_textBeforeFormula != null)
        {
            SourceBox.Text = _textBeforeFormula;
            _textBeforeFormula = null;
            FormulaButton.IsChecked = false;
            SetStatus("已回到文字识别结果", StatusKind.Idle);
            return;
        }

        FormulaButton.IsChecked = false; // on only once the formula is shown

        SetStatus("正在识别公式…", StatusKind.Busy);
        FormulaButton.IsEnabled = false;
        try
        {
            var latex = await FormulaRecognizer.RecognizeAsync(_image, _cts.Token);
            if (_closed)
            {
                return;
            }

            _textBeforeFormula = SourceBox.Text;
            TableToggle.IsChecked = false;
            SourceBox.Text = latex;
            FormulaButton.IsChecked = true;
            SetStatus(latex.Length == 0 ? "没有识别出公式" : "已识别为 LaTeX 公式，可复制到 Word / Markdown / 论文里", latex.Length == 0 ? StatusKind.Idle : StatusKind.Done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("公式识别失败", ex);
            SetStatus("公式识别失败：" + ex.Message, StatusKind.Error);
        }
        finally
        {
            FormulaButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// What was read looks like mathematics: a few lines at most, few Chinese characters, and operators or symbols of
    /// formulas (= with +, −, ×, ^, √, ∑, ∫, fractions …). Ordinary sentences and tables do not.
    /// </summary>
    public static bool LooksLikeFormula(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 400 || text.Split('\n').Length > 8)
        {
            return false;
        }

        var cjk = text.Count(c => c is >= '\u4e00' and <= '\u9fff');
        var letters = text.Count(c => !char.IsWhiteSpace(c));
        if (cjk > letters * 0.3)
        {
            return false;
        }

        var strong = text.Count(c => "√∑∫∏∂∇∞≤≥≠≈±×÷πθαβγδλμσΩ^_{}".Contains(c));
        var operators = text.Count(c => "=+-−*/<>()".Contains(c));
        var fraction = System.Text.RegularExpressions.Regex.IsMatch(text, @"\)/\(|\w/\(|\)/\w");
        return strong >= 1 && operators >= 1 || fraction || (text.Contains('=') && operators >= 3);
    }

    /// <summary>导出: the text as shown (edits included) to Excel / Markdown / text; Excel first when tables were read.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text))
        {
            SetStatus("没有可导出的文字", StatusKind.Idle);
            return;
        }

        var formats = _tables.Count > 0 ? OcrExport.Formats : OcrExport.Formats.OrderBy(f => f.Extension == ".txt" ? 0 : 1).ToArray();
        var path = SaveImageDialog.Ask(this, "导出识别结果", $"文字识别_{DateTime.Now:yyyyMMdd_HHmmss}", formats: formats);
        if (path == null)
        {
            return;
        }

        try
        {
            OcrExport.Save(path, SourceBox.Text);
            SetStatus("已导出：" + Path.GetFileName(path), StatusKind.Done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("导出识别结果失败", ex);
            SetStatus("导出失败：" + ex.Message, StatusKind.Error);
        }
    }

    private IReadOnlyList<OcrTable> _tables = Array.Empty<OcrTable>();

    /// <summary>Tests: the tables found.</summary>
    public IReadOnlyList<OcrTable> Tables => _tables;

    /// <summary>
    /// Tables found in the picture: a 表格 switch over the text, and the tables drawn as tables (header row bold, lines
    /// between cells, every cell selectable). The text keeps them tab-separated.
    /// </summary>
    public void ShowTables(IReadOnlyList<OcrTable> tables)
    {
        _tables = tables;
        TablePanel.Children.Clear();
        TableToggle.Visibility = tables.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tables.Count == 0)
        {
            TableToggle.IsChecked = false;
            return;
        }

        foreach (var table in tables)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
            for (var c = 0; c < table.ColumnCount; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            for (var r = 0; r < table.Rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var c = 0; c < table.ColumnCount; c++)
                {
                    var cell = new TextBox
                    {
                        Text = c < table.Rows[r].Count ? table.Rows[r][c] : "",
                        IsReadOnly = true,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        FontSize = 13,
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        MaxWidth = 260,
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(6, 4, 6, 4)
                    };
                    cell.SetResourceReference(ForegroundProperty, Theme.Key(0xFFF0F0F0));
                    cell.SelectionBrush = (Brush)FindResource("Accent");
                    var border = new Border
                    {
                        Child = cell,
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1)
                    };
                    border.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
                    if (r == 0)
                    {
                        border.SetResourceReference(Border.BackgroundProperty, Theme.Key(0x14FFFFFF));
                    }

                    Grid.SetRow(border, r);
                    Grid.SetColumn(border, c);
                    grid.Children.Add(border);
                }
            }

            TablePanel.Children.Add(grid);
        }

        TableToggle.IsChecked = true; // a table read: shown as one
    }

    private void TableToggle_Changed(object sender, RoutedEventArgs e)
    {
        var table = TableToggle.IsChecked == true;
        TableView.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        SourceBox.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The 二维码 / 条形码 in the picture, read beside the text recognition.</summary>
    public IReadOnlyList<CodeReader.Code> Codes { get; private set; } = Array.Empty<CodeReader.Code>();

    private async Task ReadCodesAsync(BitmapSource image)
    {
        var codes = await Task.Run(() => CodeReader.Read(image));
        if (_closed)
        {
            return;
        }

        Codes = codes;
        ShowCodes(codes);
    }

    public void ShowCodes(IReadOnlyList<CodeReader.Code> codes)
    {
        CodePanel.Children.Clear();
        CodePanel.Visibility = codes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var code in codes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var kind = new TextBlock { Text = code.Kind, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            kind.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF8A8A8A));
            DockPanel.SetDock(kind, Dock.Left);
            var copy = new Button { Content = "复制", Style = (Style)FindResource("PaneAction"), Padding = new Thickness(8, 2, 8, 2), ToolTip = "复制内容" };
            copy.Click += (_, _) =>
            {
                ClipboardService.CopyText(code.Text);
                SetStatus("已复制" + code.Kind + "内容", StatusKind.Done);
            };
            DockPanel.SetDock(copy, Dock.Right);
            var text = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (code.IsLink)
            {
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(code.Text))
                {
                    NavigateUri = new Uri(code.Text.Trim()),
                    Foreground = (Brush)FindResource("Accent"),
                    ToolTip = "在浏览器中打开"
                };
                link.RequestNavigate += (_, e) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Log.Error("打开链接失败", ex);
                    }

                    e.Handled = true;
                };
                text.Inlines.Add(link);
            }
            else
            {
                text.Text = code.Text;
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFE6E6E6));
            }

            row.Children.Add(kind);
            row.Children.Add(copy);
            row.Children.Add(text);
            CodePanel.Children.Add(row);
        }
    }

    private async Task TranslateAsync()
    {
        if (_busy || _closed)
        {
            return;
        }

        var text = SourceBox.Text.Trim();
        if (text.Length == 0)
        {
            SetStatus("没有可翻译的文本。", StatusKind.Idle);
            return;
        }

        _busy = true;
        TranslateButton.IsEnabled = false;
        SetStatus("正在翻译…", StatusKind.Busy);

        try
        {
            var target = ResolveTarget();

            var provider = TranslationService.Create(App.Config.Translation);
            var result = await provider.TranslateAsync(text, target, _cts.Token);

            if (_closed)
            {
                return;
            }

            ResultBox.Text = result;
            SetStatus(result.Length == 0 ? "翻译结果为空。" : "翻译完成", result.Length == 0 ? StatusKind.Idle : StatusKind.Done);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("已取消", StatusKind.Idle);
            }
        }
        catch (Exception ex)
        {
            Log.Error("翻译失败", ex);
            if (!_closed)
            {
                SetStatus("翻译失败：" + ex.Message, StatusKind.Error);
            }
        }
        finally
        {
            _busy = false;
            TranslateButton.IsEnabled = true;
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            return;
        }

        if (ClipboardService.CopyText(ResultBox.Text))
        {
            SetStatus("译文已复制", StatusKind.Done);
        }
        else
        {
            SetStatus("复制失败：剪贴板被占用", StatusKind.Error);
        }
    }

    /// <summary>贴到屏幕 under 译文: the translation as a pin.</summary>
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            SetStatus("还没有译文，先点“翻译”。", StatusKind.Idle);
            return;
        }

        var card = TextCardRenderer.Render($"译文 · {ResolveTarget()}", ResultBox.Text);
        ((App)Application.Current).CreatePinAtCursor(card);
        SetStatus("译文已贴到屏幕", StatusKind.Done);
    }

    /// <summary>贴到屏幕 under 原文: the recognised (edited) text as a pin.</summary>
    private void PinSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourceBox.Text))
        {
            SetStatus("没有可贴图的原文。", StatusKind.Idle);
            return;
        }

        var card = TextCardRenderer.Render("识别文字", SourceBox.Text);
        ((App)Application.Current).CreatePinAtCursor(card);
        SetStatus("原文已贴到屏幕", StatusKind.Done);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _cts.Dispose();
        base.OnClosed(e);
    }
}
