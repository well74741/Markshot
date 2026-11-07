using System.Runtime.InteropServices;

namespace Markshot.UI;

/// <summary>What changed, in a few words per version (shown once after an update).</summary>
public static class ReleaseNotes
{
    public static readonly (string Version, string[] Lines)[] All =
    {
        ("0.8.56", new[]
        {
            "动图贴图旋转、翻转后照常播放（动图只贴图，不进入批注）",
            "Z / Shift+Z 撤销 / 重做；设置未保存关闭时会询问",
            "悬浮按钮截图后 Esc 可靠退出、拖动不再闪；浮窗按钮和批注栏一样大",
            "贴图历史铺满宽度并记住窗口大小；录屏工具栏不再显示不全",
            "稳定性：长时间运行的内存、光标句柄和配置写入更稳妥",
        }),
        ("0.8.55", new[]
        {
            "动图（GIF / WebP）贴图正常播放，阴影沿图片轮廓；拖到悬浮按钮的图片贴在屏幕中央",
            "悬浮按钮：尺寸滑条、贴图历史按钮，出现 / 消失有过渡，截图后保持收起",
            "图片编辑器：清空图片、移动吸附开关；浅色主题标题栏用深色 logo",
            "批注：换工具不再改变选中元素的大小；右键先取消选中；文字工具双击空白处新建",
        }),
        ("0.8.54", new[] { "把图片拖到悬浮按钮上，直接贴图在屏幕上" }),
        ("0.8.53", new[]
        {
            "悬浮按钮：单击截图，鼠标移上去展开截屏 / 录屏 / 文字识别 / 图片编辑器；可换图标（支持 GIF / WebP 动图）",
            "后台通知改为小气泡，不再弹系统通知、没有提示音",
            "升级后保留原来的设置；主题和界面大小选了就保存",
            "批注：右键先回到选择模式；各工具的大小互不影响；清空批注后序号从 1 开始",
            "录屏、文字识别、检查更新换上新图标；设置里“样式”排在第一个",
        }),
        ("0.8.52", new[]
        {
            "Win + 鼠标划区域时按住空格可移动区域",
            "复制到剪贴板更可靠；截图后任务栏不再闪",
            "批注工具栏更快、更有立体感；长截图图标改成方形",
        }),
        ("0.8.50", new[] { "二维码 / 条形码识别更快、种类更多" }),
        ("0.8.49", new[] { "截图区域里的二维码自动框出，链接可直接点开" }),
        ("0.8.48", new[]
        {
            "Win + 鼠标左 / 中 / 右键直接划区域：识别文字 / 贴图 / 复制（可在设置里换）",
            "界面大小三档；文字可选字体；Shift+Esc 关闭所有贴图；托盘可禁用所有快捷键",
        }),
        ("0.8.47", new[] { "图片编辑器可新建画布；抠色先预览再应用" }),
    };

    /// <summary>Versions newer than <paramref name="previous"/> up to <paramref name="current"/>, newest first (only the current one when the previous is unknown).</summary>
    public static List<(string Version, string[] Lines)> Since(string? previous, string current)
    {
        var now = Parse(current);
        var before = Parse(previous);
        return All.Where(entry => Parse(entry.Version) is { } version && now != null && version <= now &&
                                  (before == null ? version == now : version > before))
            .ToList();
    }

    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = text.Split('+', '-')[0].Trim();
        return Version.TryParse(plain, out var version) ? version : null;
    }
}

/// <summary>"Markshot 已更新": the new things in short; a click anywhere outside (or Esc) closes it.</summary>
public sealed class WhatsNewWindow : Window
{
    private readonly DispatcherTimer _outsideWatch = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _wasDown = true; // the click that may still be held when it opens does not count
    private bool _closing;

    public WhatsNewWindow(string current, IReadOnlyList<(string Version, string[] Lines)> notes)
    {
        Title = "Markshot 已更新";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var body = new StackPanel { Width = 380 };
        var heading = new TextBlock { Text = $"Markshot 已更新到 {current.Split('+')[0]}", FontSize = 15, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFF5F5F5));
        body.Children.Add(heading);
        foreach (var (version, lines) in notes.Take(4))
        {
            if (notes.Count > 1)
            {
                var label = new TextBlock { Text = version, FontSize = 11.5, Margin = new Thickness(0, 12, 0, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF9A9A9A));
                body.Children.Add(label);
            }

            foreach (var line in lines)
            {
                var row = new DockPanel { Margin = new Thickness(0, notes.Count > 1 ? 5 : 9, 0, 0) };
                var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(1, 7, 9, 0), VerticalAlignment = VerticalAlignment.Top };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
                DockPanel.SetDock(dot, Dock.Left);
                var text = new TextBlock { Text = line, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
                text.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFFC8C8C8));
                row.Children.Add(dot);
                row.Children.Add(text);
                body.Children.Add(row);
            }
        }

        var hint = new TextBlock { Text = "点击窗口外关闭", FontSize = 11, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        hint.SetResourceReference(TextBlock.ForegroundProperty, Theme.Key(0xFF6E6E6E));
        body.Children.Add(hint);

        var card = new Border
        {
            LayoutTransform = UiScale.Transform,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 18, 22, 14),
            Margin = new Thickness(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 },
            Child = body,
            Opacity = 0
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Key(0xFF1E1E1E));
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Key(0x33FFFFFF));
        Content = card;

        MouseLeftButtonDown += (_, _) => DragMove();
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                Dismiss();
            }
        };
        Deactivated += (_, _) => Dismiss();
        // Started by the updater in the background, Windows may not let it take the focus: clicks are watched too.
        _outsideWatch.Tick += (_, _) =>
        {
            var down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (down && !_wasDown && !PointerInside())
            {
                Dismiss();
            }

            _wasDown = down;
        };
        Loaded += (_, _) =>
        {
            Motion.PopIn(card, fromAbove: false);
            Activate();
            _outsideWatch.Start();
        };
        Closed += (_, _) => _outsideWatch.Stop();
    }

    /// <summary>Tests: a click outside.</summary>
    internal void ClickOutside() => Dismiss();

    public bool IsClosing => _closing;

    private bool PointerInside()
    {
        var cursor = Native.CursorPosition();
        if (PresentationSource.FromVisual(this) == null)
        {
            return false;
        }

        var local = PointFromScreen(new Point(cursor.X, cursor.Y));
        return local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
    }

    private void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _outsideWatch.Stop();
        if (Content is FrameworkElement card)
        {
            Motion.PopOut(card, towardsAbove: false, completed: Close, duration: Motion.Bar);
        }
        else
        {
            Close();
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
