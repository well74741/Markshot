using System.Windows.Documents;
using Markshot.Annotations;

namespace Markshot.UI;

/// <summary>
/// 二维码 / 条形码 in the chosen area: they are read in the background once the area rests; the pointer over one shows
/// its box and a small card next to it with what it says (a link opens on a click).
/// </summary>
public partial class OverlayWindow
{
    private IReadOnlyList<CodeReader.Code> _codes = Array.Empty<CodeReader.Code>();
    private Rect _codesFor = Rect.Empty;
    private int _codesRequest;
    private DispatcherTimer? _codeTimer;
    private CodeReader.Code? _shownCode;
    private Border? _codeFrame;
    private Border? _codeCard;
    private TextBlock? _codeCardText;

    /// <summary>For tests: the codes found in the area (screen pixels of the capture).</summary>
    internal IReadOnlyList<CodeReader.Code> CodesInArea => _codes;

    /// <summary>The area changed: read it again once it rests (a lasso area too: its bounding box).</summary>
    private void ScheduleCodes()
    {
        if (!_hasSelection || _selection.Width < 20 || _selection.Height < 20)
        {
            ClearCodes();
            return;
        }

        if (SnapRect(_selection) == _codesFor)
        {
            return;
        }

        HideCode();
        _codes = Array.Empty<CodeReader.Code>();
        if (_codeTimer == null)
        {
            _codeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _codeTimer.Tick += (_, _) =>
            {
                _codeTimer.Stop();
                ReadCodes();
            };
        }

        _codeTimer.Stop();
        _codeTimer.Start();
    }

    private void ReadCodes()
    {
        if (_closed || !_hasSelection || _dragging)
        {
            return;
        }

        var area = SnapRect(_selection);
        var crop = new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
        if (crop.Width < 8 || crop.Height < 8 || crop.X + crop.Width > _screenPlain.PixelWidth || crop.Y + crop.Height > _screenPlain.PixelHeight)
        {
            return;
        }

        var picture = new CroppedBitmap(_screenPlain, crop);
        picture.Freeze();
        var request = ++_codesRequest;
        Task.Run(() => CodeReader.Read(picture)).ContinueWith(task => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || request != _codesRequest || task.IsFaulted)
            {
                return;
            }

            // In the capture's pixels.
            _codes = task.Result
                .Select(code => code with { Area = code.Area is { } a ? new Rect(a.X + area.X, a.Y + area.Y, a.Width, a.Height) : null })
                .ToList();
            _codesFor = area;
        }), TaskScheduler.Default);
    }

    /// <summary>For tests: the codes of the area, read now.</summary>
    internal void ReadCodesNow()
    {
        _codesFor = Rect.Empty;
        ReadCodes();
    }

    private void ClearCodes()
    {
        _codeTimer?.Stop();
        _codesRequest++;
        _codes = Array.Empty<CodeReader.Code>();
        _codesFor = Rect.Empty;
        HideCode();
    }

    /// <summary>The pointer moved (capture pixels): the code under it shows its box and card. True when it is on one.</summary>
    private bool UpdateCodeHover(Point point)
    {
        if (_codes.Count == 0 || _dragging || !_hasSelection)
        {
            HideCode();
            return false;
        }

        var code = _codes.FirstOrDefault(c => c.Area is { } a && a.Contains(point));
        if (code == null)
        {
            // Over the card itself it stays (to click the link).
            if (_codeCard is { IsVisible: true, IsMouseOver: true })
            {
                return false;
            }

            HideCode();
            return false;
        }

        if (!ReferenceEquals(code, _shownCode))
        {
            ShowCode(code);
        }

        return true;
    }

    private void ShowCode(CodeReader.Code code)
    {
        EnsureCodeParts();
        _shownCode = code;
        var area = code.Area!.Value;
        var dip = new Rect(area.X / _scale, area.Y / _scale, area.Width / _scale, area.Height / _scale);
        _codeFrame!.Width = dip.Width + 6;
        _codeFrame.Height = dip.Height + 6;
        Canvas.SetLeft(_codeFrame, dip.X - 3);
        Canvas.SetTop(_codeFrame, dip.Y - 3);
        _codeFrame.Visibility = Visibility.Visible;

        _codeCardText!.Inlines.Clear();
        var kind = new Run(code.Kind + "　") { Foreground = Theme.Brush(0xFF8A8A8A) };
        _codeCardText.Inlines.Add(kind);
        if (code.IsLink)
        {
            var link = new Hyperlink(new Run(code.Text)) { NavigateUri = new Uri(code.Text.Trim()), Foreground = (Brush)FindResource("Accent"), Cursor = Cursors.Hand };
            link.RequestNavigate += (_, e) =>
            {
                OpenLink(e.Uri);
                e.Handled = true;
            };
            _codeCardText.Inlines.Add(link);
        }
        else
        {
            _codeCardText.Inlines.Add(new Run(code.Text));
        }

        _codeCard!.Visibility = Visibility.Visible;
        _codeCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _codeCard.DesiredSize;
        // Right of the code, else left of it; never off the screen.
        var left = dip.Right + 10;
        if (left + size.Width > ActualWidth - 6)
        {
            left = dip.Left - size.Width - 10;
        }

        Canvas.SetLeft(_codeCard, Math.Clamp(left, 6, Math.Max(6, ActualWidth - size.Width - 6)));
        Canvas.SetTop(_codeCard, Math.Clamp(dip.Top, 6, Math.Max(6, ActualHeight - size.Height - 6)));
        Motion.PopIn(_codeCard, fromAbove: false);
    }

    private void HideCode()
    {
        _shownCode = null;
        if (_codeFrame != null)
        {
            _codeFrame.Visibility = Visibility.Collapsed;
        }

        if (_codeCard is { Visibility: Visibility.Visible })
        {
            _codeCard.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureCodeParts()
    {
        if (_codeFrame != null)
        {
            return;
        }

        // The box: corner brackets in the accent colour, like a scanner's frame.
        _codeFrame = new Border
        {
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)FindResource("Accent"),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF)),
            Visibility = Visibility.Collapsed
        };
        _codeCardText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        _codeCardText.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF0F0F0));
        var copy = new Button { Content = "复制", Style = (Style)FindResource("ToolButton"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) =>
        {
            if (_shownCode is { } code)
            {
                ClipboardService.CopyText(code.Text);
                ShowWheelTip("已复制" + code.Kind + "内容");
            }
        };
        var row = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(_codeCardText);
        _codeCard = new Border
        {
            Padding = new Thickness(12, 7, 6, 7),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = row,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 }
        };
        _codeCard.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
        _codeCard.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
        UiScale.Attach(_codeCard);
        _codeCard.MouseLeave += (_, _) =>
        {
            if (_shownCode?.Area is { } a && !a.Contains(ToPixels(Mouse.GetPosition(InputLayer))))
            {
                HideCode();
            }
        };
        UiLayer.Children.Add(_codeFrame);
        UiLayer.Children.Add(_codeCard);
    }

    private void OpenLink(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            CancelCapture(); // the browser comes forward; the capture is done
        }
        catch (Exception ex)
        {
            Log.Error("打开链接失败", ex);
        }
    }
}
