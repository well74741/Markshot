using System.Windows.Interop;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Capture window: composing the result and closing (copy, save, pin, translate, record, OCR).
public partial class OverlayWindow
{
    private BitmapSource Compose() => Compose(includeAnnotations: true);

    private BitmapSource Compose(bool includeAnnotations) => Compose(includeAnnotations, withTranslation: true);

    private BitmapSource Compose(bool includeAnnotations, bool withTranslation)
    {
        var rect = ClampToScreen(_selection);
        var x = (int)Math.Round(rect.X);
        var y = (int)Math.Round(rect.Y);
        var width = Math.Max(1, (int)Math.Round(rect.Width));
        var height = Math.Max(1, (int)Math.Round(rect.Height));

        var translation = withTranslation ? VisibleTranslation() : null;
        var lassoClip = LassoGeometry();
        if ((!includeAnnotations || Editor.Items.Count == 0) && translation == null && _cornerRadius < 0.5 && lassoClip == null)
        {
            var cropped = new CroppedBitmap(_screen, new Int32Rect(x, y, width, height));
            cropped.Freeze();
            return cropped;
        }

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-x, -y));
            var area = lassoClip ?? new RectangleGeometry(new Rect(x, y, width, height), _cornerRadius, _cornerRadius);
            dc.PushClip(area);
            dc.PushClip(includeAnnotations ? Cutouts.Exclude(Editor.Items, area) : area); // 抠除: the screen under the holes
            dc.DrawImage(_screen, new Rect(0, 0, _screen.PixelWidth, _screen.PixelHeight));
            if (translation != null)
            {
                dc.DrawImage(translation, _translatedRect);
            }

            dc.Pop();
            if (includeAnnotations)
            {
                AnnotationEditor.DrawLayers(dc, Editor.Items); // each element cut only by the holes above it
            }

            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void CopyAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        Log.Info($"CopyAndClose：{_selection.Width}x{_selection.Height}，批注 {Editor.Items.Count} 个");
        var image = Compose();
        if (!ClipboardService.CopyImage(image))
        {
            ThemedMessage.Show(this, "提示", "复制失败：剪贴板被其他程序占用，请重试。", "确定");
            return;
        }

        CloseWith(CaptureAction.Copy, image);
    }

    private void SaveAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        var path = SaveImageDialog.Ask(this, "保存截图", $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png", picture: image);
        if (path == null)
        {
            Focus();
            return;
        }

        try
        {
            SaveToFile(image, path);
            SavedPath = path;
            CloseWith(CaptureAction.Save, image);
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
            ThemedMessage.Show(this, "保存失败", ex.Message, "确定");
        }
    }

    private void QuickSaveAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        try
        {
            var directory = App.Config.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"Markshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            SaveToFile(image, path);
            SavedPath = path;
            CloseWith(CaptureAction.Save, image);
        }
        catch (Exception ex)
        {
            Log.Error("快速保存失败", ex);
            ThemedMessage.Show(this, "出错了", "快速保存失败：" + ex.Message, "确定");
        }
    }

    private void PinAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        // Hand the pin the clean selection plus the annotations so they stay editable there.
        var rect = ClampToScreen(_selection);
        var origin = new Point(Math.Round(rect.X), Math.Round(rect.Y));
        PinCornerRadius = _cornerRadius;
        PinShapedShadow = _lasso != null;
        PinBaseImage = Compose(includeAnnotations: false);
        if (VisibleTranslation() != null)
        {
            PinOriginalBase = Compose(includeAnnotations: false, withTranslation: false);
            PinLanguages = (_sourceLabel, _targetLabel);
        }
        PinAnnotations = AnnotationOps.ToPinSpace(Editor.Items, origin, PinBaseImage);
        var pinBase = PinBaseImage;
        PinHistory = _history.Convert(step => AnnotationOps.ToPinSpace(step, origin, pinBase));
        var output = Compose();
        SelectionScreenRect = new Rect(_bounds.X + _selection.X, _bounds.Y + _selection.Y, _selection.Width, _selection.Height);

        // The pin appears at once over this window (same pixels, so nothing flickers); then the
        // dimming clears from the pin's outline outwards and the window closes.
        if (PinNow?.Invoke(this, output) != null)
        {
            PinnedDirectly = true;
            var area = new Rect(_selection.X / _scale, _selection.Y / _scale, _selection.Width / _scale, _selection.Height / _scale);
            FinishWithTransition(Close, area, keepAnnotations: true);
            return;
        }

        FinishWithTransition(() => CloseWith(CaptureAction.Pin, output), Rect.Empty, keepAnnotations: true);
    }

    /// <summary>Esc / right-click / ✕: the dimming clears from the pointer outwards, then the window closes.</summary>
    private void CancelCapture()
    {
        var cursor = Native.CursorPosition();
        var at = new Point((cursor.X - _bounds.X) / _scale, (cursor.Y - _bounds.Y) / _scale);
        FinishWithTransition(Close, new Rect(at, new Size(0, 0)), keepAnnotations: false);
    }

    /// <summary>Right-click on a finished capture: back to choosing an area (the annotations go too).</summary>
    private void ResetCapture()
    {
        _textEditor.Commit();
        CommitWatermark();
        CancelTranslation();
        Editor.Items.Clear();
        _history.Clear();
        _numberCounter = 1;
        ClearAnnotationSelection();
        _lasso = null;
        _cornerRadius = 0;
        _hasSelection = false;
        _selection = Rect.Empty;
        _mode = DragMode.None;
        SetTool(AnnotationTool.Select);
        UpdateUndoButtons();
        UpdateSelectionVisuals();
        UpdateCursorToPointer();
    }

    private bool _finishing;

    /// <summary>
    /// Turning the capture into a pin: the toolbar folds away, the dimming, the area frame and
    /// the selection handles fade out (the screen then looks like the pin about to appear in
    /// the same place), and only then the window closes. Input is ignored meanwhile.
    /// </summary>
    private void FinishWithTransition(Action done, Rect clearFrom, bool keepAnnotations)
    {
        if (_finishing)
        {
            return;
        }

        _finishing = true;
        InputLayer.IsHitTestVisible = false;
        Toolbar.IsHitTestVisible = false;
        Editor.ChromeVisible = false;
        Editor.Selected = null;
        Editor.HoverWatermark = null;
        Editor.LassoDraft = null;
        if (!keepAnnotations)
        {
            Editor.Visibility = Visibility.Collapsed; // discarded: the screen looks as it really is
            TranslatedImage.Visibility = Visibility.Collapsed;
        }

        Editor.InvalidateVisual();
        Magnifier.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Collapsed;
        SizeBox.Visibility = Visibility.Collapsed;
        Motion.SetShown(TranslateStatus, false);
        Motion.SetShown(TranslateProgress, false);
        Motion.PopOut(Toolbar);

        var duration = Motion.Base;
        if (clearFrom.IsEmpty)
        {
            Motion.Fade(DimLayer, 0, duration);
        }
        else
        {
            // Mirror of the opening: a clear area grows from the pointer / the pin outwards.
            var center = new Point(clearFrom.X + clearFrom.Width / 2, clearFrom.Y + clearFrom.Height / 2);
            var width = _bounds.Width / _scale;
            var height = _bounds.Height / _scale;
            var reach = new[] { new Point(0, 0), new Point(width, 0), new Point(0, height), new Point(width, height) }
                .Max(corner => (corner - center).Length) + Math.Max(clearFrom.Width, clearFrom.Height);
            var startX = Math.Max(0.5, clearFrom.Width / 2 * 1.42) / 0.7;
            var startY = Math.Max(0.5, clearFrom.Height / 2 * 1.42) / 0.7;
            var mask = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = startX,
                RadiusY = startY
            };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.7));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
            DimLayer.BeginAnimation(OpacityProperty, null);
            DimLayer.OpacityMask = mask;
            mask.BeginAnimation(RadialGradientBrush.RadiusXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(startX, reach / 0.7, duration) { EasingFunction = Motion.Ease });
            mask.BeginAnimation(RadialGradientBrush.RadiusYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(startY, reach / 0.7, duration) { EasingFunction = Motion.Ease });
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration.TimeSpan + TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_closed)
            {
                done();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Opening transition: the dimming starts at the screen edges and closes in on the pointer
    /// (a clear disc around it shrinks away, 320 ms with the shared curve).
    /// </summary>
    private void StartDimConverge()
    {
        var cursor = Native.CursorPosition();
        var center = new Point((cursor.X - _bounds.X) / _scale, (cursor.Y - _bounds.Y) / _scale);
        var width = _bounds.Width / _scale;
        var height = _bounds.Height / _scale;
        var reach = new[] { new Point(0, 0), new Point(width, 0), new Point(0, height), new Point(width, height) }
            .Max(corner => (corner - center).Length);
        if (reach < 1)
        {
            return;
        }

        // Clear inside 70 % of the radius, a soft rim, dim outside.
        var mask = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = center,
            GradientOrigin = center,
            RadiusX = reach / 0.7,
            RadiusY = reach / 0.7
        };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.7));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
        DimLayer.OpacityMask = mask;

        // Quick (the frame of the hovered window appears at once; the dimming must not lag behind it).
        var shrink = new System.Windows.Media.Animation.DoubleAnimation(reach / 0.7, 0.5, Motion.Fast)
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        shrink.Completed += (_, _) => DimLayer.OpacityMask = null;
        mask.BeginAnimation(RadialGradientBrush.RadiusXProperty, shrink);
        mask.BeginAnimation(RadialGradientBrush.RadiusYProperty, shrink);
    }

    private void TranslateAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var rect = ClampToScreen(_selection);
        var cropped = new CroppedBitmap(
            _screen,
            new Int32Rect(
                (int)Math.Round(rect.X),
                (int)Math.Round(rect.Y),
                Math.Max(1, (int)Math.Round(rect.Width)),
                Math.Max(1, (int)Math.Round(rect.Height))));
        cropped.Freeze();
        CloseWith(CaptureAction.Translate, cropped);
    }

    private static void SaveToFile(BitmapSource image, string path) => ImageFile.Save(image, path);

    /// <summary>录屏: the selected area becomes the recording area.</summary>
    private void RecordAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CloseWith(CaptureAction.Record, _screen);
    }

    /// <summary>长截图: the selected area is captured while its content is scrolled.</summary>
    private void ScrollAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        // Like making a pin: the dimming clears from the area outwards, then the long screenshot starts.
        var area = new Rect(_selection.X / _scale, _selection.Y / _scale, _selection.Width / _scale, _selection.Height / _scale);
        FinishWithTransition(() => CloseWith(CaptureAction.Scroll, _screen), area, keepAnnotations: false);
    }

    /// <summary>编辑: the selection goes to the image editor, its annotations stay editable there.</summary>
    private void EditAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var rect = ClampToScreen(_selection);
        PinBaseImage = Compose(includeAnnotations: false);
        var origin = new Point(Math.Round(rect.X), Math.Round(rect.Y));
        PinAnnotations = AnnotationOps.ToPinSpace(Editor.Items, origin, PinBaseImage);
        var pinBase = PinBaseImage;
        PinHistory = _history.Convert(step => AnnotationOps.ToPinSpace(step, origin, pinBase));
        CloseWith(CaptureAction.Edit, PinBaseImage);
    }

    private void OcrAndClose()
    {
        if (!_hasSelection)
        {
            return;
        }

        CommitPendingText();
        var image = Compose();
        CloseWith(CaptureAction.None, image);
        ((App)Application.Current).ShowOcr(image);
    }

    private void CloseWith(CaptureAction action, BitmapSource image)
    {
        Log.Info($"CloseWith：{action}，{image.PixelWidth}x{image.PixelHeight}");
        Result = action;
        OutputImage = image;
        SelectionScreenRect = new Rect(
            _bounds.X + _selection.X,
            _bounds.Y + _selection.Y,
            _selection.Width,
            _selection.Height);
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();

    private void Pin_Click(object sender, RoutedEventArgs e) => PinAndClose();

    private void Translate_Click(object sender, RoutedEventArgs e) => TranslateAndClose();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture();
}
