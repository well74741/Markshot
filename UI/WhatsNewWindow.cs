using System.Runtime.InteropServices;

namespace Markshot.UI;

/// <summary>What changed, in a few words per version (shown once after an update).</summary>
public static class ReleaseNotes
{
    public static readonly (string Version, string[] Lines)[] All =
    {
        ("0.8.75", new[]
        {
            "修复工具栏靠近屏幕边缘时，样式列表整体偏离按钮的问题",
        }),
        ("0.8.74", new[]
        {
            "批注栏样式列表：底盘变窄，只显示当前按钮的选项，旁边的选项随底盘移开逐渐淡出；最左、最右的按钮下底盘不再被截断",
        }),
        ("0.8.73", new[]
        {
            "批注栏样式列表改为固定宽度的底盘：底盘跟着鼠标在按钮间滑动，滑到哪里露出哪里的选项，更跟手",
            "取消截图时悬浮按钮不再闪烁",
            "录屏框调整时按住空格可整体移动，右键撤回这次调整",
            "图片编辑器按 F 回中时平滑过渡",
            "内存：关闭截图、贴图、编辑器、识别窗口后及时归还内存；关闭贴图的快照只保留最近 8 张",
        }),
        ("0.8.72", new[]
        {
            "修复内存泄漏：关闭的截图窗口、贴图、编辑器不再一直留在内存里（之前用久了会占用数 GB）；识别 / 翻译模型闲置几分钟后自动释放",
            "批注栏样式列表在按钮之间平移滑动，宽度平滑过渡，不再被副工具栏挡住；所有工具栏改为不透明",
            "文字识别窗口：操作按钮有底色，开关有描边和状态点；只有识别结果像公式时才出现「公式」",
            "可关闭「记住滚轮调到的识别范围」；滚轮提示始终居中；设置分区改名「识别与保存」",
            "点悬浮按钮截图后直接退出时不再闪一下；更新后刷新系统图标缓存（任务管理器显示新图标）",
        }),
        ("0.8.71", new[]
        {
            "文字识别可导出为 Excel / Markdown / 文本，表格逐格导出",
            "可下载「公式识别」扩展模型（设置 → 识别与保存），把公式图片识别成 LaTeX；日常文字识别不变",
            "工具栏按钮的悬停高亮更跟手；鼠标快速划过时样式列表不再闪烁，展开收起更顺滑",
        }),
        ("0.8.70", new[]
        {
            "新增帮助文档：托盘菜单「帮助」或设置窗口右上角的「?」打开，含功能介绍、使用案例和操作动图",
            "长截图：来回滚动、滚轮一次滚很多、屏幕缩放下都不再重复拼接或跟丢",
            "设置窗口：标题栏跟随主题颜色，拖动时不再出现选中文字、按钮消失",
            "截图能识别 Markshot 自己的设置、编辑器等窗口",
            "Win + 鼠标划出的区域沿用截图的圆角设置",
            "文字识别：识别表格（窗口里以表格显示，复制到 Excel / Word 仍是表格）、符号和分数，分栏文章按栏阅读，长图分段识别",
            "长截图工具条增加「文字识别」",
            "贴图文字拖选复制可以关闭，拖动贴图的按键可自定义（设置 → 快捷键 → 贴图窗口）",
        }),
        ("0.8.69", new[]
        {
            "长截图支持平滑滚动 / 缩放屏幕上的页面，一侧固定一侧滚动的窗口也能拼接",
            "截图时屏幕左上角不再先黑一块",
            "窗口识别能看穿微信 / QQ 等窗口外的阴影边框，框住窗口本身",
            "其他软件的通知弹出时，悬浮按钮立即回到最上层",
            "长截图工具栏与批注工具栏样式完全一致，放在选区外",
            "后台占用更低：截图后及时归还内存",
        }),
        ("0.8.68", new[]
        {
            "截图呼出更快（截图窗口提前准备好，约快 0.3–0.5 秒）",
            "窗口自动识别更准：按窗口实际边框识别，跳过隐藏和透明的窗口",
            "任何软件复制图片，悬浮按钮都会显示缩略图；悬浮按钮不再被其他软件的弹窗遮住",
            "悬浮菜单的提示不再挡住按钮；长截图工具栏改成统一的圆角样式",
            "矩形记住上次的圆角；设置说明更简洁，拖动设置窗口更流畅",
        }),
        ("0.8.67", new[]
        {
            "尖头箭头弯曲后保持燕尾和收尖的箭尾",
            "橡皮擦擦过的箭头 / 直线 / 折线，调整弯曲或端点后空洞跟着线条走",
            "拖动悬浮按钮每帧只移动一次，不再偶发闪现",
            "文字识别窗口：原文下方也有「贴到屏幕」，两侧分别贴原文和译文",
        }),
        ("0.8.66", new[]
        {
            "箭头、直线、折线去掉调节框：拖首尾定位点改方向，拖中间的弯曲点让线条弯曲；序号的箭头头部也能拖",
            "任何复制图片的操作，悬浮按钮都显示缩略图（铺满按钮）；截图不再弹出右下角提示",
            "划截图区域时按右键取消这次划选（Win + 鼠标同样）",
            "悬浮按钮右键菜单加「鼠标穿透」；拖动更跟手；按 F 一步还原，不再重放旋转",
            "原位翻译的标签位置固定，翻译进度显示在截图框里；设置里批注栏页打开更流畅",
        }),
        ("0.8.65", new[]
        {
            "新的彩色渐变 logo（编辑器、设置、任务栏、托盘统一）",
            "每次截图后悬浮按钮先显示截图缩略图，半秒后向右滑走；可设置截图时不隐藏悬浮按钮",
            "录屏工具栏新增「截屏」按钮，随时截取录屏框内容并复制；进入录屏时边框不再闪两下",
            "原位翻译的语言标签固定在图片右上方，带关闭按钮；贴图右键菜单可关闭原位翻译",
            "按 F 还原贴图一步动画到位；悬浮按钮菜单「关闭所有贴图」后可「打开所有贴图」",
            "编辑器悬停轮廓与选中框一致；旋转序号时箭头不跟着转；拖动悬浮按钮不再闪",
        }),
        ("0.8.64", new[]
        {
            "马赛克色块恢复清晰的方块；模糊和聚焦的柔边改为平滑过渡，没有一圈圈的断层",
            "模糊、抠除之后再画、再移动元素更流畅；编辑器里拖动图片时马赛克实时跟上",
            "新增离线翻译包（中 ↔ 英，约 120 MB，手动下载）：没有大模型 API 时也能翻译",
            "贴图原位翻译的语言切换放到图片右上角外面，不挡内容",
            "悬浮按钮按住拖动时保持按下的大小；设置里图标预览更大",
        }),
        ("0.8.63", new[]
        {
            "马赛克分「色块」和「模糊」两种；模糊可调程度和柔边范围",
            "截图时工具栏贴在左下，展开设置向右伸，不再挤动工具按钮",
            "拖动调节框的一边越过对边时，内容翻转过去（镜像）",
            "抠除穿透下面所有图层，移动被抠的元素时空洞跟着走",
            "原位翻译的语言切换标签一直显示，贴图里也能切换原文 / 译文",
            "悬浮按钮按下有回弹，拖动时菜单平滑收起；新装默认浅色主题、工具栏中号",
        }),
        ("0.8.62", new[]
        {
            "悬浮按钮默认开启；单击悬浮按钮做什么可在设置里选（滑条对准哪个按钮就是哪个，默认截屏）",
            "在悬浮菜单里拖动排序，设置里的按钮顺序同步滑动更新；点「显示贴图」时菜单不收起",
            "隐藏 / 显示所有贴图时淡出淡入",
            "马赛克加模糊后边缘是柔和的，不再被形状和笔刷边缘切齐",
            "贴图用抠除画笔抠掉内容后，阴影照样沿形状显示",
        }),
        ("0.8.61", new[]
        {
            "聚焦的暗度对整张图统一、实时生效；马赛克加「模糊」滑条",
            "抠除只挖掉它下面的内容，之后画的元素不再被遮住",
            "悬浮按钮可加「显示贴图」按钮，一键隐藏 / 显示所有贴图",
            "设置只浏览不修改时，关闭不再提示未保存；二维码点外面自动收回",
            "图片编辑器元素样式区平滑展开 / 收起，标题显示版本号；长截图从截图区向外变亮",
        }),
        ("0.8.60", new[]
        {
            "新工具「聚焦」（序号按钮里）：画矩形 / 椭圆，里面保持原样、外面变暗，可调暗度和羽化",
            "图片编辑器：元素样式加透明度，形状元素也能编辑；没改过的功能区重置按钮变灰",
            "工具设置栏统一：样式按钮放最左侧；马赛克改为图标；抠除换成蒙版图标",
            "编辑器贴到屏幕时，修改和批注直接合进图片",
            "截图区外光标不再闪烁",
        }),
        ("0.8.59", new[]
        {
            "划截图区域和画图形：按住 Alt 从中心对称，按住 Shift 为方形（同 Photoshop）",
            "长截图预览实时显示状态：正在拼接、滚动太快、无法拼接等，并有诊断数字",
            "选择模式按元素实际内容选中，空心图形、透明区域下面的元素也能点中",
            "图片编辑器：功能区可收起并记住，元素样式常驻；贴图历史可编辑图片",
            "工具栏弹出更快；悬浮按钮和托盘菜单加了新选项",
        }),
        ("0.8.58", new[]
        {
            "鼠标移到设置里的版本号上，显示最近几版的更新内容",
            "悬浮菜单与批注工具栏同一样式，修改菜单按钮时悬浮按钮不再闪",
            "录屏工具栏改为批注栏样式，展开 / 收起 / 切换有过渡；尺寸菜单加「全屏」，选预设后可继续拖动调整",
            "快捷键可以用 ` 键；恢复默认设置移到最左侧",
        }),
        ("0.8.57", new[]
        {
            "悬浮按钮：添加贴图（图片、动图）；右键菜单加 粘贴剪贴板、关闭所有贴图",
            "设置窗口：恢复默认设置；修复连续按 F1 时右下角弹出的错误",
        }),
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
