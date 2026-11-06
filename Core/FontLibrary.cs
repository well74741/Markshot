using System.Windows.Markup;

namespace Markshot.Core;

/// <summary>The fonts installed on this computer, by the name people know them by (Chinese names where they have one).</summary>
public static class FontLibrary
{
    /// <summary>One font: the family name to use, and its display name.</summary>
    public sealed record Entry(string Name, string Display, FontFamily Family)
    {
        public override string ToString() => Display;
    }

    private static List<Entry>? _all;

    public static IReadOnlyList<Entry> All()
    {
        if (_all != null)
        {
            return _all;
        }

        var chinese = XmlLanguage.GetLanguage("zh-cn");
        var english = XmlLanguage.GetLanguage("en-us");
        var list = new List<Entry>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var name = family.FamilyNames.TryGetValue(english, out var en) ? en : family.Source;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            {
                continue; // vertical variants
            }

            var display = family.FamilyNames.TryGetValue(chinese, out var zh) ? zh : name;
            list.Add(new Entry(name, display, family));
        }

        // Chinese-named fonts first (the ones used most here), then the rest alphabetically.
        _all = list
            .GroupBy(e => e.Name).Select(g => g.First())
            .OrderBy(e => e.Display.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? 0 : 1)
            .ThenBy(e => e.Display, StringComparer.CurrentCulture)
            .ToList();
        return _all;
    }

    public static string DisplayName(string name) => All().FirstOrDefault(e => e.Name == name)?.Display ?? name;
}
