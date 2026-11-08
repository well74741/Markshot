using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Pin window: in-place translation and the status pill.
public partial class PinWindow
{
    private async void TranslateInPlace()
    {
        if (_translateCts != null)
        {
            return;
        }

        if (_editMode)
        {
            ExitEditMode(true);
        }

        var original = _baseImage;
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        ShowPinStatus("正在识别文字…", hideAfter: null);
        var progress = new Progress<ImmersiveTranslator.Update>(update =>
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_translateCts, cts))
            {
                return;
            }

            if (update.Translated > 0)
            {
                _untranslatedBase ??= original;
                _translatedBase = update.Image;
                _pinSourceLabel = update.Source ?? _pinSourceLabel;
                _pinTargetLabel = update.Target ?? _pinTargetLabel;
                _pinShowsOriginal = false;
                SetBaseImage(update.Image);
                UpdatePinLanguageSwitch();
            }

            ShowPinStatus(update.Status, hideAfter: update.Done ? TimeSpan.FromSeconds(2.5) : null);
        });

        try
        {
            await ImmersiveTranslator.TranslateAsync(original, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("贴图原位翻译失败", ex);
            if (ReferenceEquals(_translateCts, cts))
            {
                ShowPinStatus("翻译失败：" + ex.Message, hideAfter: TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                _translateCts = null;
            }

            cts.Dispose();
        }
    }

    private void ShowOriginal() => ShowPinLanguage(original: true);

    /// <summary>Pinned from a capture translated in place: it shows the translation, the original one click away.</summary>
    public void AdoptTranslation(BitmapSource original, string? source, string? target)
    {
        _untranslatedBase = original;
        _translatedBase = _baseImage;
        _pinSourceLabel = source;
        _pinTargetLabel = target;
        _pinShowsOriginal = false;
        UpdatePinLanguageSwitch();
    }

    /// <summary>Translated: the original or the translation, switched with [原文 | 译文] (top right) or the menu.</summary>
    private void ShowPinLanguage(bool original)
    {
        if (_untranslatedBase == null || _translatedBase == null)
        {
            return;
        }

        if (_editMode)
        {
            ExitEditMode(true);
        }

        _pinShowsOriginal = original;
        SetBaseImage(original ? _untranslatedBase : _translatedBase);
        UpdatePinLanguageSwitch();
    }

    /// <summary>The switch shows while the pin has a translation; the lit half is what is shown.</summary>
    private void UpdatePinLanguageSwitch()
    {
        var translated = _untranslatedBase != null && _translatedBase != null;
        PinLanguageSwitch.Visibility = translated ? Visibility.Visible : Visibility.Collapsed;
        if (!translated)
        {
            return;
        }

        PinSourceButton.Content = _pinSourceLabel ?? "原文";
        PinTargetButton.Content = _pinTargetLabel is { } target && target != _pinSourceLabel ? target : "译文";
        PinSourceButton.IsChecked = _pinShowsOriginal;
        PinTargetButton.IsChecked = !_pinShowsOriginal;
    }

    private void PinSourceLanguage_Click(object sender, RoutedEventArgs e) => ShowPinLanguage(original: true);

    private void PinTargetLanguage_Click(object sender, RoutedEventArgs e) => ShowPinLanguage(original: false);

    private void CancelInPlaceTranslation()
    {
        _translateCts?.Cancel();
        _translateCts = null;
        _untranslatedBase = null;
        _translatedBase = null;
        _pinShowsOriginal = false;
        UpdatePinLanguageSwitch();
    }

    /// <summary>Swaps the image under the annotations and re-composes what the pin shows.</summary>
    private void SetBaseImage(BitmapSource image)
    {
        _baseImage = image;
        Editor.Source = image;
        var composed = ComposeEditedImage();
        SourceImage = composed;
        Img.Source = composed;
        if (!_editMode)
        {
            RefreshTextLayer();
        }
    }

    private void ShowPinStatus(string text, TimeSpan? hideAfter)
    {
        PinStatusText.Text = text;
        PinStatus.Visibility = Visibility.Visible;
        _statusTimer?.Stop();
        if (hideAfter is { } delay)
        {
            _statusTimer ??= new DispatcherTimer();
            _statusTimer.Interval = delay;
            _statusTimer.Tick -= HidePinStatus;
            _statusTimer.Tick += HidePinStatus;
            _statusTimer.Start();
        }
    }

    private void HidePinStatus(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        PinStatus.Visibility = Visibility.Collapsed;
    }

    /// <summary>OCRs what the pin shows so its text can be selected and copied.</summary>
    private async void RefreshTextLayer()
    {
        var version = ++_textLayerVersion;
        TextLayer.Reset();
        var image = SourceImage;
        try
        {
            var blocks = await OcrService.RecognizeBlocksAsync(image, App.Config.OcrLanguage);
            if (version == _textLayerVersion && ReferenceEquals(image, SourceImage))
            {
                TextLayer.SetLines(blocks);
            }
        }
        catch (Exception ex)
        {
            Log.Error("贴图文字层识别失败", ex);
        }
    }
}
