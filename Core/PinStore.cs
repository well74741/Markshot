using System.Text.Json;

namespace Markshot.Core;

/// <summary>Persists closed pins under %APPDATA%\Markshot\pins (or next to the exe).</summary>
public static class PinStore
{
    private const int MaxEntries = 20;
    private static readonly object Gate = new();
    private static string? _root;

    private static string Root
    {
        get
        {
            if (_root != null)
            {
                return _root;
            }

            try
            {
                var directory = Path.Combine(AppConfig.ConfigDirectory, "pins");
                Directory.CreateDirectory(directory);
                _root = directory;
            }
            catch (Exception ex)
            {
                Log.Info("贴图历史目录不可写，改用程序目录：" + ex.Message);
                var directory = Path.Combine(AppContext.BaseDirectory, "pins");
                Directory.CreateDirectory(directory);
                _root = directory;
            }

            return _root;
        }
    }

    private static string IndexPath => Path.Combine(Root, "history.json");

    public static IReadOnlyList<PinEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(IndexPath))
                {
                    return Array.Empty<PinEntry>();
                }

                var json = File.ReadAllText(IndexPath);
                var records = JsonSerializer.Deserialize<List<PinRecord>>(json) ?? new List<PinRecord>();
                var entries = new List<PinEntry>();

                foreach (var record in records.OrderBy(record => record.Created))
                {
                    if (string.IsNullOrWhiteSpace(record.File))
                    {
                        continue;
                    }

                    var path = Path.Combine(Root, record.File);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    entries.Add(new PinEntry
                    {
                        FilePath = path,
                        Group = string.IsNullOrWhiteSpace(record.Group) ? "默认" : record.Group,
                        Created = record.Created
                    });
                }

                return entries;
            }
            catch (Exception ex)
            {
                Log.Error("读取贴图历史失败", ex);
                return Array.Empty<PinEntry>();
            }
        }
    }

    public static void Save(PinEntry entry, BitmapSource image)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Root);

                var fileName = $"pin_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                var path = Path.Combine(Root, fileName);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                entry.FilePath = path;

                var records = ReadRecords();
                records.Add(new PinRecord
                {
                    File = fileName,
                    Group = entry.Group,
                    Created = entry.Created
                });

                while (records.Count > MaxEntries)
                {
                    var oldest = records[0];
                    records.RemoveAt(0);
                    TryDelete(Path.Combine(Root, oldest.File));
                }

                WriteRecords(records);
            }
            catch (Exception ex)
            {
                Log.Error("保存贴图历史失败", ex);
            }
        }
    }

    private static List<PinRecord> ReadRecords()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return new List<PinRecord>();
            }

            var json = File.ReadAllText(IndexPath);
            return JsonSerializer.Deserialize<List<PinRecord>>(json) ?? new List<PinRecord>();
        }
        catch (Exception ex)
        {
            Log.Error("读取贴图历史索引失败", ex);
            return new List<PinRecord>();
        }
    }

    private static void WriteRecords(List<PinRecord> records)
    {
        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(IndexPath, json);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore missing/locked files.
        }
    }

    private sealed class PinRecord
    {
        public string File { get; set; } = string.Empty;

        public string Group { get; set; } = "默认";

        public DateTime Created { get; set; } = DateTime.Now;
    }
}
