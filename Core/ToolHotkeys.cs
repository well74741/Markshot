using Markshot.Annotations;

namespace Markshot.Core;

public static class ToolHotkeys
{
    public static bool TryMap(Key key, AppConfig config, out AnnotationTool tool)
    {
        tool = AnnotationTool.None;
        var name = Name(key);

        if (Matches(name, config.ToolPenHotkey)) tool = AnnotationTool.Pen;
        else if (Matches(name, config.ToolSelectHotkey)) tool = AnnotationTool.Select;
        else if (Matches(name, config.ToolEraserHotkey)) tool = AnnotationTool.Eraser;
        else if (Matches(name, config.ToolTextHotkey)) tool = AnnotationTool.Text;
        else if (Matches(name, config.ToolNumberHotkey)) tool = AnnotationTool.Number;
        else if (Matches(name, config.ToolLineHotkey)) tool = AnnotationTool.Line;
        else if (Matches(name, config.ToolShapeHotkey)) tool = AnnotationTool.Rectangle;

        return tool != AnnotationTool.None;
    }

    /// <summary>A key as the settings write it: letters / digits as themselves, ` for the key above Tab.</summary>
    public static string Name(Key key) => key switch
    {
        Key.Oem3 => "`",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString()
    };

    private static bool Matches(string keyName, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return string.Equals(keyName, configured.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
