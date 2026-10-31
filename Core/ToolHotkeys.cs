using Markshot.Annotations;

namespace Markshot.Core;

public static class ToolHotkeys
{
    public static bool TryMap(Key key, AppConfig config, out AnnotationTool tool)
    {
        tool = AnnotationTool.None;
        var name = key.ToString();

        if (Matches(name, config.ToolPenHotkey)) tool = AnnotationTool.Pen;
        else if (Matches(name, config.ToolSelectHotkey)) tool = AnnotationTool.Select;
        else if (Matches(name, config.ToolEraserHotkey)) tool = AnnotationTool.Eraser;
        else if (Matches(name, config.ToolTextHotkey)) tool = AnnotationTool.Text;
        else if (Matches(name, config.ToolNumberHotkey)) tool = AnnotationTool.Number;
        else if (Matches(name, config.ToolLineHotkey)) tool = AnnotationTool.Line;

        return tool != AnnotationTool.None;
    }

    private static bool Matches(string keyName, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return string.Equals(keyName, configured.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
