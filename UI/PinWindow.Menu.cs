using System.Globalization;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Markshot.Annotations;
using Markshot.Services;
using Microsoft.Win32;

namespace Markshot.UI;

// Pin window: context menu, click-through, copy and save.
public partial class PinWindow
{
    private protected virtual void BuildContextMenu()
    {
        // Only the entries that do something in the current state.
        var menu = new ContextMenu();
        void Add(string header, Action action, bool? isChecked = null, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            if (isChecked is { } value)
            {
                item.IsCheckable = true;
                item.IsChecked = value;
            }

            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        void Separator()
        {
            if (menu.Items.Count > 0 && menu.Items[^1] is not System.Windows.Controls.Separator)
            {
                menu.Items.Add(new Separator());
            }
        }

        if (TextLayer.HasSelection)
        {
            Add("复制选中文字", () => ClipboardService.CopyText(TextLayer.SelectedText), gesture: "Ctrl+C");
        }

        Add("复制图片", Copy, gesture: TextLayer.HasSelection ? null : "Ctrl+C");
        Add("另存为…", Save, gesture: "Ctrl+S");
        Separator();

        if (_translateCts != null)
        {
            menu.Items.Add(new MenuItem { Header = "正在原位翻译…", IsEnabled = false });
        }
        else if (_untranslatedBase != null)
        {
            Add("显示原文", ShowOriginal);
        }
        else
        {
            Add("原位翻译", TranslateInPlace);
        }

        Add("文字识别 / 翻译…", () => ((App)Application.Current).TranslateImage(CurrentImage, this));
        Separator();

        Add(_editMode ? "完成标注" : "编辑标注", ToggleEditing, gesture: "Space");
        Add("在图片编辑器里打开", OpenInEditor);
        if (Editor.Items.Count > 0)
        {
            Add("清除批注内容", ClearAnnotations);
        }

        Separator();
        Add("置顶", () =>
        {
            Topmost = !Topmost;
            ApplyBounds();
        }, Topmost, "T");
        Add("锁定", () =>
        {
            _locked = !_locked;
            UpdateFrame();
        }, _locked, "L");
        Add("鼠标穿透", () => SetClickThrough(!_clickThrough), _clickThrough);
        if (Math.Abs(_zoom - 1) > 0.001 || _opacity < 0.999 || _orientation.Count > 0)
        {
            Add("重置大小和方向", ResetPinImageTransform, gesture: "F");
        }

        Separator();
        Add("设置…", () => ((App)Application.Current).ShowSettings());
        if ((Application.Current as App)?.PinCount > 1)
        {
            Add("关闭所有贴图", () => ((App)Application.Current).CloseAllPins());
        }

        Add("关闭", Close, gesture: "Esc");
        ContextMenu = menu;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled)
        {
            return;
        }

        _clickThrough = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            var updated = enabled
                ? style | Native.WS_EX_TRANSPARENT
                : style & ~(long)Native.WS_EX_TRANSPARENT;

            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(updated));
            Native.SetWindowPos(
                handle,
                Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        UpdateFrame();
    }

    private protected virtual void Copy()
    {
        ClipboardService.CopyImage(CurrentImage);
    }

    private protected virtual void Save()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存贴图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg",
            FileName = $"Markshot_pin_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        var initialDirectory = App.Config.ResolveSaveDirectory();
        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var wasTopmost = Topmost;
        Topmost = false;
        try
        {
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
        }
        finally
        {
            Topmost = wasTopmost;
        }

        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() is ".jpg" or ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CurrentImage));
            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }

            ((App)Application.Current).ShowSavedPrompt(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("保存贴图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "Markshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
