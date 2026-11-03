using System;
using System.IO;

/// <summary>Paths inside the Markshot repository (found from the test binary upwards).</summary>
static class Repo
{
    public static string Root { get; } = Find();

    public static string File(string relative) => Path.Combine(Root, relative);

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Markshot.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Markshot.csproj not found above " + AppContext.BaseDirectory);
    }
}
