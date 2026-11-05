using Microsoft.Win32;

namespace Markshot.Core;

/// <summary>
/// Update by installer package. The update source is either the GitHub repository (default:
/// the latest release's <c>Markshot-Setup-&lt;版本&gt;.exe</c> asset) or a folder holding such
/// files; a package newer than the running version can be installed from the settings window
/// (a GitHub one is downloaded first). The source can be changed in the settings.
/// </summary>
public static class UpdateService
{
    public const string SetupPrefix = "Markshot-Setup-";

    /// <summary>Built-in update source: this project's GitHub repository (its releases).</summary>
    public const string DefaultRepository = "https://github.com/well74741/Markshot";

    /// <param name="Path">Local file, or the download URL of a GitHub release asset.</param>
    public sealed record Package(string Path, string Version, bool Remote = false);

    public static bool IsGitHub(string source) =>
        source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default update source: the GitHub repository.</summary>
    public static string DefaultPath() => DefaultRepository;

    /// <summary>Folder the installer ran from, or the repository's dist folder for a development build.</summary>
    public static string LocalPackageFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Markshot");
            if (key?.GetValue("SetupDir") is string dir && !string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }
        catch (Exception ex)
        {
            Log.Info("读取安装包位置失败：" + ex.Message);
        }

        // Development layout: <repo>\publish\Markshot.exe next to <repo>\dist.
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/'))?.FullName;
        var dist = parent == null ? null : System.IO.Path.Combine(parent, "dist");
        return dist != null && Directory.Exists(dist) ? dist : "";
    }

    /// <summary>
    /// Newest package from the built-in GitHub repository and, when set, the custom folder /
    /// repository URL; null when nothing is newer than this build.
    /// </summary>
    /// <summary>Why the last check found nothing when it could not reach the source (null = it did check).</summary>
    public static string? LastFailure { get; private set; }

    public static async Task<Package?> FindNewestAsync(string? custom, CancellationToken cancellationToken = default)
    {
        LastFailure = null;
        var builtIn = FindNewerAsync(DefaultRepository, cancellationToken);
        Package? own = null;
        if (!string.IsNullOrWhiteSpace(custom) &&
            !string.Equals(custom.Trim().TrimEnd('/'), DefaultRepository, StringComparison.OrdinalIgnoreCase))
        {
            own = await FindNewerAsync(Environment.ExpandEnvironmentVariables(custom.Trim()), cancellationToken);
        }

        var github = await builtIn;
        if (own == null)
        {
            return github;
        }

        return github == null || Compare(own.Version, github.Version) >= 0 ? own : github;
    }

    /// <summary>A newer package from the source (GitHub repository or folder), or null.</summary>
    public static async Task<Package?> FindNewerAsync(string source, CancellationToken cancellationToken = default)
    {
        if (!IsGitHub(source))
        {
            return await Task.Run(() => FindNewer(source), cancellationToken);
        }

        var segments = new Uri(source).AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            return null;
        }

        // The release web page first: the anonymous API allows only 60 requests an hour per IP,
        // which a shared office network uses up quickly (403 rate limit = "no update" before).
        var fromPage = await FindNewerOnPageAsync(segments[0], segments[1].Replace(".git", ""), cancellationToken);
        if (fromPage.Checked)
        {
            return fromPage.Package;
        }

        try
        {
            var parts = segments;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Markshot/" + AppVersion.Display);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await client.GetStringAsync($"https://api.github.com/repos/{parts[0]}/{parts[1].Replace(".git", "")}/releases/latest", cancellationToken);
            using var document = JsonDocument.Parse(json);
            Package? best = null;
            foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = System.IO.Path.GetFileNameWithoutExtension(name)[SetupPrefix.Length..];
                if (Parse(version).Length > 0 && (best == null || Compare(version, best.Version) > 0))
                {
                    best = new Package(asset.GetProperty("browser_download_url").GetString() ?? "", version, Remote: true);
                }
            }

            return best != null && Compare(best.Version, AppVersion.Display) > 0 ? best : null;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, private repository (404) …: no answer this time (not "up to date").
            Log.Info("检查 GitHub 更新失败：" + ex.Message);
            LastFailure = ex.Message.Contains("403") ? "GitHub 暂时限制了访问次数" : "连不上 GitHub";
            return null;
        }
    }

    /// <summary>
    /// Latest release via github.com itself (no API quota): /releases/latest redirects to the tag,
    /// the tag's asset list names the installer. Checked = false when the page could not be read.
    /// </summary>
    private static async Task<(bool Checked, Package? Package)> FindNewerOnPageAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var (found, latest) = await LatestOnPageAsync(owner, repo, cancellationToken);
        return (found, latest != null && Compare(latest.Version, AppVersion.Display) > 0 ? latest : null);
    }

    /// <summary>The newest installer the release page offers, whatever this build is (Checked = false: page not readable).</summary>
    public static async Task<(bool Checked, Package? Package)> LatestOnPageAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Markshot/" + AppVersion.Display);
            using var latest = await client.GetAsync($"https://github.com/{owner}/{repo}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            latest.EnsureSuccessStatusCode();
            var landed = latest.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            var marker = "/releases/tag/";
            var at = landed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return (false, null);
            }

            var tag = Uri.UnescapeDataString(landed[(at + marker.Length)..]);
            var html = await client.GetStringAsync($"https://github.com/{owner}/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", cancellationToken);
            Package? best = null;
            var pattern = new System.Text.RegularExpressions.Regex(
                @"/releases/download/[^""/]+/" + System.Text.RegularExpressions.Regex.Escape(SetupPrefix) + @"([0-9][0-9.]*)\.exe",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(html))
            {
                var version = match.Groups[1].Value.TrimEnd('.');
                if (Parse(version).Length > 0 && (best == null || Compare(version, best.Version) > 0))
                {
                    best = new Package($"https://github.com/{owner}/{repo}" + match.Value, version, Remote: true);
                }
            }

            if (best == null)
            {
                return (false, null); // page layout changed: let the API try
            }

            return (true, best);
        }
        catch (Exception ex)
        {
            Log.Info("从 GitHub 页面检查更新失败：" + ex.Message);
            return (false, null);
        }
    }

    /// <summary>Downloads a GitHub package to %TEMP%\Markshot\update and returns the local copy.</summary>
    public static async Task<Package> DownloadAsync(Package package, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (!package.Remote)
        {
            return package;
        }

        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Markshot", "update");
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, $"{SetupPrefix}{package.Version}.exe");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Markshot/" + AppVersion.Display);
        using var response = await client.GetAsync(package.Path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        return new Package(target, package.Version);
    }

    /// <summary>The newest installer in <paramref name="folder"/> when it is newer than this build.</summary>
    public static Package? FindNewer(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        Package? best = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, SetupPrefix + "*.exe"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                var version = name[SetupPrefix.Length..];
                if (Parse(version).Length == 0)
                {
                    continue;
                }

                if (best == null || Compare(version, best.Version) > 0)
                {
                    best = new Package(file, version);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("检查更新失败：" + ex.Message);
            return null;
        }

        return best != null && Compare(best.Version, AppVersion.Display) > 0 ? best : null;
    }

    /// <summary>Numeric comparison of dotted versions ("0.8.04" &lt; "0.8.10").</summary>
    public static int Compare(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var left = i < x.Length ? x[i] : 0;
            var right = i < y.Length ? y[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>
    /// Installs in the background (no wizard: the update window shows the progress) and quits; the
    /// installer opens Markshot again when it is done.
    /// </summary>
    public static void InstallQuietly(Package package)
    {
        Log.Info($"开始后台更新到 {package.Version}：{package.Path}");
        Process.Start(new ProcessStartInfo(package.Path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAFTERUPDATE")
        {
            UseShellExecute = true
        });
        ((App)Application.Current).ShutdownApp();
    }

    /// <summary>Starts the installer and quits so it can replace the files.</summary>
    public static void Install(Package package)
    {
        Log.Info($"开始更新到 {package.Version}：{package.Path}");
        Process.Start(new ProcessStartInfo(package.Path) { UseShellExecute = true });
        ((App)Application.Current).ShutdownApp();
    }

    private static int[] Parse(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return Array.Empty<int>();
            }

            numbers.Add(number);
        }

        return numbers.ToArray();
    }
}
