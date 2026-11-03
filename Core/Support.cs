using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Markshot.Core;

/// <summary>Secrets in the config file, encrypted for the current Windows user (DPAPI).</summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return "";
        }

        try
        {
            return Prefix + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(plain), protect: true));
        }
        catch (Exception ex)
        {
            Log.Error("加密 API Key 失败", ex);
            return "";
        }
    }

    /// <summary>Plain text back; "" when it was encrypted by another user / PC (the key must be entered again).</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[Prefix.Length..]), protect: false));
        }
        catch (Exception ex)
        {
            Log.Error("解密 API Key 失败（换了电脑或用户？需要重新填写）", ex);
            return "";
        }
    }

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, "Markshot", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1 /* UI_FORBIDDEN */, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob data, string description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>Start Markshot with Windows (HKCU Run; the installer's Startup shortcut counts too).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Markshot";

    private static string StartupShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Markshot.lnk");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string { Length: > 0 } || File.Exists(StartupShortcut);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(Name, false);
                if (File.Exists(StartupShortcut))
                {
                    File.Delete(StartupShortcut); // the installer's 开机自动启动 option
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("设置开机自启失败", ex);
        }
    }
}

/// <summary>
/// 导出诊断信息: a zip on the desktop with the logs, the config (API key removed) and a short
/// system report, to send along with a problem.
/// </summary>
public static class Diagnostics
{
    public static string Export()
    {
        var name = $"Markshot-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (Directory.Exists(AppConfig.LogDirectory))
            {
                foreach (var log in Directory.GetFiles(AppConfig.LogDirectory, "*.log"))
                {
                    AddFile(zip, log, "logs/" + Path.GetFileName(log));
                }
            }

            AddText(zip, "config.json", RedactedConfig());
            AddText(zip, "system.txt", SystemReport());
        }

        Log.Info("导出诊断信息：" + path);
        return path;
    }

    private static void AddFile(ZipArchive zip, string file, string entryName)
    {
        try
        {
            // The log may be open for writing: read it shared.
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = zip.CreateEntry(entryName).Open();
            source.CopyTo(target);
        }
        catch (IOException ex)
        {
            AddText(zip, entryName + ".error.txt", ex.Message);
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>The config as saved, without anything secret.</summary>
    public static string RedactedConfig()
    {
        try
        {
            return Redact(File.Exists(AppConfig.ConfigPath) ? File.ReadAllText(AppConfig.ConfigPath) : "{}");
        }
        catch (Exception ex)
        {
            return "配置读取失败：" + ex.Message;
        }
    }

    /// <summary>Config JSON with every API key field blanked.</summary>
    public static string Redact(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (node?["Translation"] is System.Text.Json.Nodes.JsonObject translation)
            {
                foreach (var key in translation.Select(p => p.Key).ToList())
                {
                    if (key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
                    {
                        translation[key] = string.IsNullOrEmpty(translation[key]?.ToString()) ? "" : "(已移除)";
                    }
                }
            }

            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "{}";
        }
        catch (Exception ex)
        {
            return "配置读取失败：" + ex.Message;
        }
    }

    private static string SystemReport()
    {
        var text = new StringBuilder();
        text.AppendLine($"Markshot {AppVersion.Display}");
        text.AppendLine($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSDescription}), {RuntimeInformation.OSArchitecture}");
        text.AppendLine($".NET {Environment.Version}");
        text.AppendLine($"程序目录 {AppContext.BaseDirectory}");
        text.AppendLine($"配置 {AppConfig.ConfigPath}");
        text.AppendLine($"主题 {Theme.Mode}（浅色={Theme.IsLight}），界面大小 ×{UiScale.Factor:0.##}");
        text.AppendLine($"ffmpeg {(Recording.FFmpeg.Path ?? "未找到")}");
        text.AppendLine("显示器：");
        foreach (var m in Recording.Monitors.All())
        {
            text.AppendLine($"  {m.Bounds.X},{m.Bounds.Y} {m.Bounds.Width}×{m.Bounds.Height}  缩放 {m.Scale * 100:0}%  工作区 {m.WorkArea.Width}×{m.WorkArea.Height}");
        }

        return text.ToString();
    }
}
