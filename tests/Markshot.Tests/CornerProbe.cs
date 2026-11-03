using System;
using System.Windows;
using Markshot.Annotations;

static class CornerProbe
{
    public static void Run()
    {
        var sel = new Rect(100, 100, 400, 300);
        var ok = true;
        foreach (var r in new[] { 0.0, 20.0, 60.0 })
        {
            var dots = AnnotationEditor.CornerDots(sel, r, 1.5);
            for (var i = 0; i < 4; i++)
            {
                var back = AnnotationEditor.CornerRadiusFor(sel, i, dots[i], 1.5);
                if (Math.Abs(back - r) > 0.01) { ok = false; Console.WriteLine($"corner {i} r={r} -> {back}"); }
            }
        }
        Console.WriteLine(ok ? "PASS rounding dots map back to their radius on every corner" : "FAIL rounding dots");
        var max = AnnotationEditor.CornerRadiusFor(sel, 0, new Point(450, 400), 1.5);
        Console.WriteLine(max <= 150 ? $"PASS radius capped at half the short side ({max:F0})" : $"FAIL radius {max}");
        Console.WriteLine(Markshot.Core.UpdateService.DefaultPath() == "https://github.com/well74741/Markshot" && Markshot.Core.UpdateService.IsGitHub(Markshot.Core.UpdateService.DefaultPath())
            ? "PASS update source defaults to the GitHub repository" : "FAIL update default");
    }
}
