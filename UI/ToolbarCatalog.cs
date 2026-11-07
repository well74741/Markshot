using Path = System.Windows.Shapes.Path;

namespace Markshot.UI;

/// <summary>The annotation bar's buttons by key: default orders, names and icons (toolbar + settings).</summary>
internal static class ToolbarCatalog
{
    public static readonly string[] Tools = { "Shape", "Pen", "Arrow", "Text", "Number", "Mosaic", "Eraser" };
    public static readonly string[] CaptureActions = { "Scroll", "Record", "Ocr", "Close", "Clear", "Pin", "Save", "Translate", "Edit", "Copy" };
    public static readonly string[] PinActions = { "Close", "Save", "Clear", "Edit", "Apply", "Copy" };
    public static readonly string[] EditorActions = { "New", "Paste", "Open", "Save", "Clear", "Pin", "Copy" };

    public static string Label(string key) => key switch
    {
        "Shape" => "图形",
        "Pen" => "画笔",
        "Arrow" => "箭头 / 直线",
        "Text" => "文字 / 水印",
        "Number" => "序号",
        "Mosaic" => "马赛克",
        "Eraser" => "橡皮擦",
        "Ocr" => "文字识别",
        "Close" => "关闭",
        "Clear" => "清空批注",
        "Open" => "打开图片",
        "New" => "新建画布",
        "Paste" => "粘贴图片",
        "Pin" => "贴图",
        "Save" => "保存",
        "Copy" => "复制",
        "Translate" => "原位翻译",
        "Apply" => "应用",
        "Record" => "录屏",
        "Scroll" => "长截图",
        "Edit" => "图片编辑器",
        "Editor" => "图片编辑器",
        "Capture" => "截屏",
        "History" => "贴图历史",
        _ => key
    };

    public static UIElement Icon(string key)
    {
        var mask = key switch
        {
            "Shape" => "IconRectMask",
            "Pen" => "IconPenMask",
            "Arrow" => "IconArrowMask",
            "Text" => "IconTextMask",
            "Number" => "IconNumberMask",
            "Mosaic" => "IconMosaicMask",
            "Eraser" => "IconEraserMask",
            "Close" => "IconCloseMask",
            "Clear" => "IconTrashMask",
            "Open" => "IconOpenMask",
            "New" => "IconNewMask",
            "Paste" => "IconPasteMask",
            "Pin" => "IconPinMask",
            "Save" => "IconSaveMask",
            "Copy" => "IconCopyMask",
            "Apply" => "IconConfirmMask",
            "Record" => "IconRecordMask",
            "Scroll" => "IconScrollMask",
            "Edit" => "IconEditMask",
            "Ocr" => "IconOcrMask",
            "Editor" => "IconEditMask",
            "Capture" => "IconCaptureMask",
            "History" => "IconHistoryMask",
            _ => "IconTranslateMask"
        };
        return new Border { Style = (Style)Application.Current.FindResource(mask) };
    }

    /// <summary>Saved order, cleaned up: unknown keys dropped, missing ones added next to their default neighbour.</summary>
    public static List<string> Order(string? saved, string[] defaults)
    {
        var keys = (saved ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(defaults.Contains)
            .Distinct()
            .ToList();
        foreach (var key in defaults)
        {
            if (keys.Contains(key))
            {
                continue;
            }

            var before = Array.IndexOf(defaults, key) - 1;
            var at = before >= 0 ? keys.IndexOf(defaults[before]) : -1;
            if (at >= 0)
            {
                keys.Insert(at + 1, key);
            }
            else if (before < 0)
            {
                keys.Insert(0, key); // first by default: stays first
            }
            else
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    public static HashSet<string> Set(string? text) =>
        new((text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}
