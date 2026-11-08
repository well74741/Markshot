using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Capture window: in-place translation of the selection.
public partial class OverlayWindow
{
    /// <summary>The translated pixels when they match the current selection and are not toggled off.</summary>
    private BitmapSource? VisibleTranslation() =>
        _autoTranslate && !_showOriginal && _translated != null && _hasSelection &&
        SnapRect(_selection) == _translatedRect
            ? _translated
            : null;

    /// <summary>
    /// The screen as currently shown (with the translation), used as the pixel
    /// source for the eraser and mosaic so they act on what the user sees.
    /// </summary>
    private BitmapSource EffectiveScreen
    {
        get
        {
            var translation = VisibleTranslation();
            if (translation == null)
            {
                return _screen;
            }

            if (_effectiveScreen == null)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(_screen, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
                    dc.DrawImage(translation, _translatedRect);
                }

                var bitmap = new RenderTargetBitmap(_screen.PixelWidth, _screen.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                _effectiveScreen = bitmap;
            }

            return _effectiveScreen;
        }
    }

    private async void StartTranslation()
    {
        if (!_autoTranslate || !_hasSelection || _closed || SelectionDragging)
        {
            return;
        }

        var rect = SnapRect(_selection);
        if (rect == _translatedRect && _translated != null)
        {
            UpdateTranslationVisuals();
            return;
        }

        CancelTranslation();
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        _translatingRect = rect;
        _translated = null;
        _translatedRect = Rect.Empty;
        _effectiveScreen = null;
        _showOriginal = false;

        var cropped = new CroppedBitmap(_screen, new Int32Rect(
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height));
        cropped.Freeze();

        ShowTranslateStatus("正在识别文字…", busy: true);
        var progress = new Progress<ImmersiveTranslator.Update>(update =>
        {
            if (cts.IsCancellationRequested || _closed)
            {
                return;
            }

            // Show partial results as they arrive, like a browser filling in a page.
            _translated = update.Image;
            _translatedRect = rect;
            _effectiveScreen = null;
            _sourceLabel = update.Source ?? _sourceLabel;
            _targetLabel = update.Target ?? _targetLabel;
            UpdateTranslationVisuals();
            var partial = update.Done && update.Translated < update.Total;
            ShowTranslateStatus(update.Done && !partial && update.Translated > 0 ? null : update.Status,
                busy: !update.Done, error: partial, retry: partial);
        });

        try
        {
            Log.Info($"原位翻译开始：{rect.Width}x{rect.Height}");
            await ImmersiveTranslator.TranslateAsync(cropped, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("原位翻译失败", ex);
            if (!cts.IsCancellationRequested && !_closed)
            {
                ShowTranslateStatus("翻译失败：" + ex.Message, busy: false, error: true, retry: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                _translatingRect = Rect.Empty;
            }
        }
    }

    private void CancelTranslation()
    {
        _translateDebounce.Stop();
        if (_translateCts != null)
        {
            _translateCts.Cancel();
            _translateCts.Dispose();
            _translateCts = null;
        }

        _translatingRect = Rect.Empty;
    }

    private void UpdateTranslationVisuals()
    {
        if (_closed)
        {
            return;
        }

        var translation = VisibleTranslation();
        if (translation == null || SelectionDragging)
        {
            TranslatedImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            TranslatedImage.Source = translation;
            Canvas.SetLeft(TranslatedImage, _translatedRect.X / _scale);
            Canvas.SetTop(TranslatedImage, _translatedRect.Y / _scale);
            TranslatedImage.Width = _translatedRect.Width / _scale;
            TranslatedImage.Height = _translatedRect.Height / _scale;
            // A lasso capture shows the translation only inside its shape.
            if (LassoGeometry() is { } lassoShape)
            {
                var clip = lassoShape.Clone();
                clip.Transform = new TransformGroup
                {
                    Children =
                    {
                        new TranslateTransform(-_translatedRect.X, -_translatedRect.Y),
                        new ScaleTransform(1 / _scale, 1 / _scale)
                    }
                };
                TranslatedImage.Clip = clip;
            }
            else
            {
                TranslatedImage.Clip = null;
            }
            TranslatedImage.Visibility = Visibility.Visible;
        }

        if (!_autoTranslate || !_hasSelection)
        {
            TranslateStatus.Visibility = Visibility.Collapsed;
            TranslateProgress.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Switched off and on again (the translation kept): the bar with its [原文 | 译文] comes back at once,
            // not only after the frame is moved.
            if (TranslateStatus.Visibility != Visibility.Visible && _translated != null && _translateCts == null)
            {
                TranslateStatusText.Text = string.Empty;
                TranslateStatusText.Visibility = Visibility.Collapsed;
                TranslateRetry.Visibility = Visibility.Collapsed;
                TranslateDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
                TranslateStatus.Visibility = Visibility.Visible;
            }

            UpdateLanguageSwitch();
            PositionTranslateStatus();
        }
    }

    /// <summary>Updates the translate bar; <paramref name="text"/> null hides the status (only the switch shows).</summary>
    private void ShowTranslateStatus(string? text, bool busy, bool error = false, bool retry = false)
    {
        TranslateStatusText.Text = text ?? string.Empty;
        TranslateStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        TranslateDot.Fill = error
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
            : busy
                ? (Brush)FindResource("Accent")
                : new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
        TranslateRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        UpdateLanguageSwitch();
        var shown = _autoTranslate && _hasSelection;
        TranslateStatus.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // The progress / error goes in its own pill inside the area: the bar keeps its width (no jumping about).
        TranslateProgress.Visibility = shown && (!string.IsNullOrEmpty(text) || retry) ? Visibility.Visible : Visibility.Collapsed;
        PositionTranslateStatus();
    }

    /// <summary>[source | target]: the highlighted half is the language currently shown.</summary>
    private void UpdateLanguageSwitch()
    {
        // Always there (原文 | 译文 until the languages are known): the bar does not change its size.
        var known = _sourceLabel != null && _targetLabel != null && _sourceLabel != _targetLabel;
        LanguageSwitch.Visibility = Visibility.Visible;
        SourceLanguageButton.Content = known ? _sourceLabel : "原文";
        TargetLanguageButton.Content = known ? _targetLabel : "译文";
        SourceLanguageButton.IsChecked = _showOriginal;
        TargetLanguageButton.IsChecked = !_showOriginal;
    }

    private void PositionTranslateStatus()
    {
        if (TranslateStatus.Visibility != Visibility.Visible)
        {
            return;
        }

        TranslateStatus.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TranslateStatus.DesiredSize;
        var selection = new Rect(_selection.X / _scale, _selection.Y / _scale,
            _selection.Width / _scale, _selection.Height / _scale);
        // Always above the area's top right corner, right-aligned with it (as on a pin); just under the top edge
        // of the area only when the screen ends above it. The toolbar is under the area's left edge.
        var left = Math.Clamp(selection.Right - size.Width, 4, Math.Max(4, ActualWidth - size.Width - 4));
        var top = selection.Top - size.Height - 6;
        if (top < 4)
        {
            top = selection.Top + 6;
        }

        Canvas.SetLeft(TranslateStatus, left);
        Canvas.SetTop(TranslateStatus, top);
        if (TranslateProgress.Visibility == Visibility.Visible)
        {
            // Inside the area's top right corner (under the bar when that is inside too).
            TranslateProgress.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var pill = TranslateProgress.DesiredSize;
            var pillTop = top >= selection.Top ? top + size.Height + 4 : selection.Top + 6;
            Canvas.SetLeft(TranslateProgress, Math.Max(selection.Left + 4, selection.Right - pill.Width - 6));
            Canvas.SetTop(TranslateProgress, pillTop);
        }
    }

    /// <summary>✕: 原位翻译 off (as the toolbar's switch).</summary>
    private void TranslateClose_Click(object sender, RoutedEventArgs e)
    {
        _autoTranslate = false;
        _showOriginal = false;
        Toolbar.SetTranslateEnabled(false);
        App.Config.ImmersiveTranslate = false;
        App.Config.Save();
        CancelTranslation();
        UpdateTranslationVisuals();
    }

    private void SourceLanguage_Click(object sender, RoutedEventArgs e)
    {
        // Show the original text.
        _showOriginal = true;
        _effectiveScreen = null;
        UpdateLanguageSwitch();
        UpdateTranslationVisuals();
        Focus();
    }

    private void TargetLanguage_Click(object sender, RoutedEventArgs e)
    {
        _showOriginal = false;
        _effectiveScreen = null;
        UpdateLanguageSwitch();
        UpdateTranslationVisuals();
        Focus();
    }

    private void TranslateRetry_Click(object sender, RoutedEventArgs e)
    {
        _translatedRect = Rect.Empty;
        StartTranslation();
        Focus();
    }
}
