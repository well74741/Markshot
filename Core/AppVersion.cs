using System.Reflection;

namespace Markshot.Core;

/// <summary>Display version (InformationalVersion, e.g. "0.8.01").</summary>
public static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "";
}
