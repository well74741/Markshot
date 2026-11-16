namespace Markshot.Core;

/// <summary>Persists closed editable pins (and reads pre-archive PNG history).</summary>
public static class PinStore
{
    private const int MaxEntries = 30;
    private const long MaxDiskBytes = 512L * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _root;

    private static string Root
    {
        get
        {
            if (_root != null) return _root;
            try
            {
                var directory = Path.Combine(AppConfig.ConfigDirectory, "pins");
                Directory.CreateDirectory(directory); _root = directory;
            }
            catch (Exception ex)
            {
                Log.Info("贴图历史目录不可写，改用程序目录：" + ex.Message);
                var directory = Path.Combine(AppContext.BaseDirectory, "pins");
                Directory.CreateDirectory(directory); _root = directory;
            }
            return _root;
        }
    }

    /// <summary>Tests use their own fresh directory, even if the process already initialized Root.
    /// No AppConfig/APPDATA lookup or unwritable-directory fallback occurs inside this scope.</summary>
    public static IDisposable UseDirectoryForTests(string directory)
    {
        var path = Path.GetFullPath(directory);
        Directory.CreateDirectory(path);
        lock (Gate)
        {
            var previous = _root; _root = path;
            return new DirectoryScope(previous);
        }
    }
    private sealed class DirectoryScope : IDisposable
    {
        private readonly string? _previous; private bool _disposed;
        public DirectoryScope(string? previous) => _previous = previous;
        public void Dispose() { lock (Gate) { if (_disposed) return; _disposed = true; _root = _previous; } }
    }

    private static string IndexPath => Path.Combine(Root, "history.json");
    private static string? RecordPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file) || file.Contains('/') || file.Contains('\\')) return null;
        var extension = Path.GetExtension(file);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(PinArchive.Extension, StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(Root, file);
    }

    public static IReadOnlyList<PinEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                return ReadRecords().OrderBy(record => record.Created).Select(record => (Record: record, Path: RecordPath(record.File)))
                    .Where(item => item.Path != null && File.Exists(item.Path)).TakeLast(MaxEntries)
                    .Select(item => new PinEntry { FilePath = item.Path, Group = string.IsNullOrWhiteSpace(item.Record.Group) ? "默认" : item.Record.Group, Created = item.Record.Created }).ToArray();
            }
            catch (Exception ex) { Log.Error("读取贴图历史失败", ex); return Array.Empty<PinEntry>(); }
        }
    }

    public static bool Save(PinEntry entry, PinArchive archive) => Persist(entry, PinArchive.Extension, path => File.Copy(archive.FilePath, path));

    /// <summary>Legacy callers can still save PNG; new closed pins always call the archive overload.</summary>
    public static void Save(PinEntry entry, BitmapSource image) => Persist(entry, ".png", path =>
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    });

    private static bool Persist(PinEntry entry, string extension, Action<string> save)
    {
        lock (Gate)
        {
            string? staged = null; string? target = null; bool indexed = false;
            try
            {
                var name = "pin_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N") + extension;
                target = Path.Combine(Root, name); staged = target + ".tmp";
                save(staged); File.Move(staged, target);
                var records = ReadRecords().Where(record => RecordPath(record.File) is { } path && File.Exists(path)).OrderBy(record => record.Created).ToList();
                records.Add(new PinRecord { File = name, Group = entry.Group, Created = entry.Created });
                var removed = new List<PinRecord>();
                var total = records.Sum(record => new FileInfo(RecordPath(record.File)!).Length);
                while (records.Count > MaxEntries || (records.Count > 1 && total > MaxDiskBytes))
                {
                    var oldest = records[0]; records.RemoveAt(0); removed.Add(oldest);
                    total -= new FileInfo(RecordPath(oldest.File)!).Length;
                }
                if (total > MaxDiskBytes) throw new IOException("贴图归档超过历史磁盘限额（512 MB）");
                WriteRecords(records); indexed = true;
                entry.FilePath = target; entry.Image = null;
                foreach (var record in removed) TryDelete(RecordPath(record.File));
                return true;
            }
            catch (Exception ex) { Log.Error("保存贴图历史失败", ex); return false; }
            finally { TryDelete(staged); if (!indexed) TryDelete(target); }
        }
    }

    /// <summary>Only this store's indexed file can be deleted, never an arbitrary supplied path.</summary>
    public static void Delete(PinEntry entry)
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(entry.FilePath)) return;
                var path = Path.GetFullPath(entry.FilePath);
                var name = Path.GetFileName(path);
                if (!string.Equals(RecordPath(name), path, StringComparison.OrdinalIgnoreCase)) return;
                var records = ReadRecords();
                if (records.RemoveAll(record => string.Equals(record.File, name, StringComparison.OrdinalIgnoreCase)) == 0) return;
                WriteRecords(records); TryDelete(path);
            }
            catch (Exception ex) { Log.Error("删除贴图历史失败", ex); }
        }
    }

    private static List<PinRecord> ReadRecords()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<PinRecord>();
            if (new FileInfo(IndexPath).Length > 1024 * 1024) throw new InvalidDataException("贴图历史索引过大");
            return JsonSerializer.Deserialize<List<PinRecord>>(File.ReadAllText(IndexPath)) ?? new List<PinRecord>();
        }
        catch (Exception ex) { Log.Error("读取贴图历史索引失败", ex); return new List<PinRecord>(); }
    }
    private static void WriteRecords(List<PinRecord> records) => AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* Missing/locked files are harmless. */ }
    }
    private sealed class PinRecord
    {
        public string File { get; set; } = string.Empty;
        public string Group { get; set; } = "默认";
        public DateTime Created { get; set; } = DateTime.Now;
    }
}
