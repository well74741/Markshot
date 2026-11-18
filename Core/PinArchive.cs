using System.IO.Compression;
using System.Security.Cryptography;
using Markshot.Annotations;
using Markshot.UI;

namespace Markshot.Core;

public sealed record PinArchiveState(PinSnapshot Snapshot, AnnotationHistory History,
    double CornerRadius = 0, bool ShapedShadow = false, string Orientation = "",
    BitmapSource? Untranslated = null, BitmapSource? Translated = null, string? TranslationText = null,
    string? SourceLabel = null, string? TargetLabel = null, bool ShowsOriginal = false);

/// <summary>
/// Lossless, versioned editable pin package. Each bitmap is content-addressed once per package;
/// its original pixel format/DPI/palette and pixels are compressed in streaming rows. Only the
/// current state is decoded on restore; undo/redo entries and their images remain on disk.
/// </summary>
public sealed partial class PinArchive
{
    public const string Extension = ".markpin";
    private const int Version = 2;
    private const long MaxMetadataBytes = 64L * 1024 * 1024;
    private const long MaxImageBytes = 512L * 1024 * 1024;
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const long MaxSessionBytes = 1024L * 1024 * 1024;
    private static string? _sessionDirectory;
    private static readonly object SessionGate = new();
    private readonly OwnedFile? _lease;
    public string FilePath { get; }
    public long StoredBytes => new FileInfo(FilePath).Length;
    public PinArchive(string path) : this(path, owned: false) { }
    private PinArchive(string path, bool owned)
    {
        FilePath = Path.GetFullPath(path);
        if (owned) _lease = new OwnedFile(FilePath);
    }
    // History factories hold this archive/lease, so GC cannot remove a live undo source.
    private sealed class OwnedFile
    {
        private readonly string _path;
        public OwnedFile(string path) => _path = path;
        ~OwnedFile() { try { File.Delete(_path); } catch { /* The exit cleanup retries. */ } }
    }

    private static string SessionDirectory
    {
        get
        {
            lock (SessionGate)
            {
                if (_sessionDirectory == null)
                {
                    var directory = Path.Combine(Path.GetTempPath(), "Markshot-pin-session-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    _sessionDirectory = directory;
                }
                return _sessionDirectory;
            }
        }
    }

    public static void CleanupSession()
    {
        lock (SessionGate)
        {
            if (_sessionDirectory == null) return;
            try { Directory.Delete(_sessionDirectory, recursive: true); } catch (Exception ex) { Log.Info("清理本次运行贴图归档失败：" + ex.Message); }
            _sessionDirectory = null;
        }
    }

    private static void CheckSessionQuota()
    {
        var bytes = Directory.EnumerateFiles(SessionDirectory).Sum(path => new FileInfo(path).Length);
        if (bytes > MaxSessionBytes) throw new IOException("本次运行贴图归档超过磁盘限额（1 GB）；在用的撤销源不会被删除");
    }

    public static PinArchive CreateSession(PinArchiveState state)
    {
        var saved = Create(state, Path.Combine(SessionDirectory, Guid.NewGuid().ToString("N") + Extension));
        try { CheckSessionQuota(); return new PinArchive(saved.FilePath, owned: true); }
        catch { File.Delete(saved.FilePath); throw; }
    }

    // A private session copy outlives persistent-history eviction and deletion while a restored
    // pin still needs its undo steps. No full-size BitmapSource belongs to a history entry.
    public PinArchive KeepForSession()
    {
        if (Path.GetDirectoryName(FilePath) == _sessionDirectory) return this;
        var target = Path.Combine(SessionDirectory, Guid.NewGuid().ToString("N") + Extension);
        try
        {
            File.Copy(FilePath, target);
            CheckSessionQuota();
            return new PinArchive(target, owned: true);
        }
        catch { if (File.Exists(target)) File.Delete(target); throw; }
    }

    public static PinArchive Create(PinArchiveState state, string path) => Create(state, path, Version);
    internal static PinArchive CreateLegacyForTests(PinArchiveState state, string path) => Create(state, path, 1);

    private static PinArchive Create(PinArchiveState state, string path, int schemaVersion)
    {
        var target = Path.GetFullPath(path);
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using (var file = File.Create(staging))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var resources = new ResourceWriter(zip);
                var undo = 0;
                foreach (var step in state.History.UndoSteps) WriteStep(zip, resources, "undo/" + undo++, step, schemaVersion);
                var redo = 0;
                foreach (var step in state.History.RedoSteps) WriteStep(zip, resources, "redo/" + redo++, step, schemaVersion);
                Data(zip, "current", w =>
                {
                    var a = new AnnotationWriter(w, resources, schemaVersion);
                    w.Write(schemaVersion); a.Image(state.Snapshot.Image); a.Image(state.Snapshot.BaseImage);
                    w.Write(state.Snapshot.Left); w.Write(state.Snapshot.Top); a.Number(state.Snapshot.Zoom); a.Number(state.Snapshot.Opacity);
                    a.Number(state.CornerRadius); w.Write(state.ShapedShadow); a.Text(state.Orientation);
                    a.Image(state.Untranslated); a.Image(state.Translated); a.Text(state.TranslationText); a.Text(state.SourceLabel); a.Text(state.TargetLabel); w.Write(state.ShowsOriginal);
                    w.Write(state.History.ChangeCount); w.Write(undo); w.Write(redo); a.Items(state.Snapshot.Annotations);
                });
                using var thumbnailBytes = new MemoryStream();
                var image = state.Snapshot.Image;
                var scale = Math.Min(1, 320.0 / Math.Max(image.PixelWidth, image.PixelHeight));
                BitmapSource thumbnail = image;
                if (scale < 1) { thumbnail = new TransformedBitmap(image, new ScaleTransform(scale, scale)); thumbnail.Freeze(); }
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(thumbnail)); encoder.Save(thumbnailBytes);
                thumbnailBytes.Position = 0;
                using var stream = zip.CreateEntry("thumbnail.png", CompressionLevel.NoCompression).Open();
                thumbnailBytes.CopyTo(stream);
            }
            if (new FileInfo(staging).Length > MaxPackageBytes) throw new IOException("单张贴图归档超过磁盘限额（512 MB）");
            File.Move(staging, target, overwrite: true);
            return new PinArchive(target);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void Data(ZipArchive zip, string name, Action<BinaryWriter> write)
    {
        // Metadata is small; resources cannot be written while another ZIP entry is open.
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) write(writer);
        if (buffer.Length > MaxMetadataBytes) throw new InvalidDataException("Pin metadata too large");
        buffer.Position = 0;
        using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); buffer.CopyTo(output);
    }

    private static void WriteStep(ZipArchive zip, ResourceWriter resources, string name, AnnotationHistory.ArchiveStep step, int schemaVersion = Version)
    {
        // PinWindow has no extra state callbacks; refuse foreign owner extras instead of silently
        // discarding them or constructing unknown private/user types.
        if (step.Extra != null) throw new InvalidDataException("Unsupported history extra state");
        Data(zip, name, w => new AnnotationWriter(w, resources, schemaVersion).Items(step.Items));
    }

    private Func<AnnotationHistory.ArchiveStep>? StoreStep(AnnotationHistory.ArchiveStep step)
    {
        // Most pin checkpoints contain only vectors: keeping those is cheap and does not keep
        // the base/composed bitmap alive. Image-bearing checkpoints remain compressed on disk.
        if (step.Extra == null && !step.Items.Any(a => a is ImageAnnotation or MosaicAnnotation or MosaicBrushAnnotation)) return () => step;
        var path = Path.Combine(SessionDirectory, "step-" + Guid.NewGuid().ToString("N") + Extension);
        try
        {
            HashSet<string> existing;
            using (var file = OpenArchive(FilePath))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                existing = zip.Entries.Where(e => e.FullName.StartsWith("images/", StringComparison.Ordinal)).Select(e => e.FullName[7..]).ToHashSet(StringComparer.Ordinal);
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                WriteStep(zip, new ResourceWriter(zip, existing), "step", step);
            CheckSessionQuota();
            var journal = new PinArchive(path, owned: true);
            var source = new ResourceReader(FilePath, _lease);
            var reader = new ResourceReader(path, journal._lease, source.Get);
            return () => ReadStep(path, "step", reader);
        }
        catch (IOException ex)
        {
            try { File.Delete(path); } catch { }
            // Do not break an active edit/undo because disk is full. The owner falls back to its
            // normal shared frozen-image snapshot with the history's existing memory limit.
            Log.Info("撤销快照改用受限内存：" + ex.Message);
            return null;
        }
        catch { try { File.Delete(path); } catch { } throw; }
    }

    private AnnotationHistory.ArchiveBatch? StoreMappedSteps(IEnumerable<AnnotationHistory.ArchiveStep> steps, Action<List<Annotation>> change)
    {
        var path = Path.Combine(SessionDirectory, "mapped-" + Guid.NewGuid().ToString("N") + Extension);
        try
        {
            var count = 0;
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var resources = new ResourceWriter(zip);
                foreach (var step in steps)
                {
                    var mapped = new AnnotationHistory.ArchiveStep(step.Items.Select(item => item.Clone()).ToList(), step.Extra);
                    change(mapped.Items);
                    WriteStep(zip, resources, "mapped/" + count++, mapped);
                }
            }
            if (new FileInfo(path).Length > MaxPackageBytes) throw new IOException("变换后的撤销归档超过单包限额");
            CheckSessionQuota();
            var archive = new PinArchive(path, owned: true);
            var reader = new ResourceReader(path, archive._lease);
            var loaders = Enumerable.Range(0, count).Select(i => (Func<AnnotationHistory.ArchiveStep>)(() => ReadStep(path, "mapped/" + i, reader))).ToArray();
            return new AnnotationHistory.ArchiveBatch(loaders, archive.StoreStep, archive.StoreMappedSteps);
        }
        catch (IOException ex)
        {
            try { File.Delete(path); } catch { }
            Log.Info("变换后的撤销记录改用受限内存：" + ex.Message);
            return null;
        }
        catch { try { File.Delete(path); } catch { } throw; }
    }

    public PinArchiveState Restore()
    {
        var resources = new ResourceReader(FilePath, _lease);
        return ReadData(FilePath, "current", r =>
        {
            var a = new AnnotationReader(r, resources);
            var version = r.ReadInt32();
            if (version is not (1 or Version)) throw new InvalidDataException("Unsupported pin archive version");
            resources.SchemaVersion = version;
            var image = a.Image() ?? throw new InvalidDataException("Missing composed image");
            var baseImage = a.Image() ?? throw new InvalidDataException("Missing base image");
            var left = r.ReadInt32(); var top = r.ReadInt32(); var zoom = a.Number(); var opacity = a.Number();
            var corner = a.Number(); var shadow = r.ReadBoolean(); var orientation = a.Text() ?? "";
            if (orientation.Length > 100_000 || orientation.Any(c => c is not ('H' or 'V' or 'R'))) throw new InvalidDataException("Invalid orientation");
            var original = a.Image(); var translated = a.Image(); var text = a.Text(); var source = a.Text(); var target = a.Text(); var showsOriginal = r.ReadBoolean();
            var changes = r.ReadInt64(); var undo = a.Count(50); var redo = a.Count(50);
            var annotations = a.Items();
            var history = new AnnotationHistory { StoreSnapshot = StoreStep, StoreTransformedSteps = StoreMappedSteps };
            history.LoadArchive(Enumerable.Range(0, undo).Select(i => (Func<AnnotationHistory.ArchiveStep>)(() => ReadStep(FilePath, "undo/" + i, resources))),
                Enumerable.Range(0, redo).Select(i => (Func<AnnotationHistory.ArchiveStep>)(() => ReadStep(FilePath, "redo/" + i, resources))), changes);
            return new PinArchiveState(new PinSnapshot(image, baseImage, annotations, left, top, zoom, opacity), history,
                corner, shadow, orientation, original, translated, text, source, target, showsOriginal);
        });
    }

    public BitmapSource LoadImage() => ReadData(FilePath, "current", r =>
    {
        if (r.ReadInt32() is not (1 or Version)) throw new InvalidDataException("Unsupported pin archive version");
        return new AnnotationReader(r, new ResourceReader(FilePath, _lease)).Image() ?? throw new InvalidDataException("Missing image");
    });

    public BitmapSource Thumbnail()
    {
        using var file = OpenArchive(FilePath); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = zip.GetEntry("thumbnail.png") ?? throw new InvalidDataException("Missing thumbnail");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Thumbnail too large");
        using var stream = entry.Open(); using var data = new MemoryStream(); stream.CopyTo(data);
        var png = data.GetBuffer().AsSpan(0, (int)data.Length);
        if (png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)) is not (> 0 and <= 320) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)) is not (> 0 and <= 320))
            throw new InvalidDataException("Invalid thumbnail dimensions");
        data.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 320; bitmap.StreamSource = data; bitmap.EndInit(); bitmap.Freeze();
        GC.KeepAlive(_lease);
        return bitmap;
    }

    private static AnnotationHistory.ArchiveStep ReadStep(string path, string name, ResourceReader resources) =>
        ReadData(path, name, r => new AnnotationHistory.ArchiveStep(new AnnotationReader(r, resources).Items(), null));

    private static FileStream OpenArchive(string path)
    {
        var file = File.OpenRead(path);
        if (file.Length <= MaxPackageBytes) return file;
        file.Dispose();
        throw new InvalidDataException("Pin archive exceeds the package limit");
    }

    private static T ReadData<T>(string path, string name, Func<BinaryReader, T> read)
    {
        using var file = OpenArchive(path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing archive entry: " + name);
        if (entry.Length > MaxMetadataBytes) throw new InvalidDataException("Archive metadata too large");
        using var stream = entry.Open(); using var buffered = new BufferedStream(stream, 64 * 1024);
        using var reader = new BinaryReader(buffered, Encoding.UTF8);
        return read(reader);
    }

    // Explicit pixel-format whitelist. This also preserves premultiplied low-alpha pixels, unlike
    // a PNG round-trip (which can round them when un-premultiplying and re-premultiplying).
    private static readonly PixelFormat[] Formats =
    {
        PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Bgr32, PixelFormats.Bgr24,
        PixelFormats.Gray8, PixelFormats.Gray16, PixelFormats.BlackWhite, PixelFormats.Gray2, PixelFormats.Gray4,
        PixelFormats.Indexed1, PixelFormats.Indexed2, PixelFormats.Indexed4, PixelFormats.Indexed8,
        PixelFormats.Bgr555, PixelFormats.Bgr565, PixelFormats.Bgr101010, PixelFormats.Rgb24,
        PixelFormats.Rgb48, PixelFormats.Rgba64, PixelFormats.Prgba64, PixelFormats.Rgb128Float,
        PixelFormats.Rgba128Float, PixelFormats.Prgba128Float, PixelFormats.Gray32Float, PixelFormats.Cmyk32
    };

    private static byte[] ImageHeader(BitmapSource image)
    {
        var format = Array.IndexOf(Formats, image.Format);
        if (format < 0) throw new InvalidDataException("Unsupported image pixel format");
        using var memory = new MemoryStream(); using var w = new BinaryWriter(memory);
        w.Write(image.PixelWidth); w.Write(image.PixelHeight); w.Write(image.DpiX); w.Write(image.DpiY); w.Write((byte)format);
        var palette = image.Palette?.Colors; w.Write(palette?.Count ?? 0);
        if (palette != null) foreach (var color in palette) { w.Write(color.A); w.Write(color.R); w.Write(color.G); w.Write(color.B); }
        return memory.ToArray();
    }

    private static void Rows(BitmapSource image, Action<byte[], int> receive)
    {
        var stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
        if ((long)stride * image.PixelHeight > MaxImageBytes) throw new InvalidDataException("Image exceeds archive size limit");
        var rows = Math.Max(1, 64 * 1024 / stride);
        var buffer = new byte[checked(stride * Math.Min(rows, image.PixelHeight))];
        for (var y = 0; y < image.PixelHeight; y += rows)
        {
            var height = Math.Min(rows, image.PixelHeight - y);
            image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, height), buffer, stride, 0);
            receive(buffer, stride * height);
        }
    }

    private sealed class ResourceWriter
    {
        private readonly ZipArchive _zip;
        private sealed record ResourceId(string Value);
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, ResourceId> _known = new();
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string>? _existing;
        public ResourceWriter(ZipArchive zip, HashSet<string>? existing = null) { _zip = zip; _existing = existing; }
        public string Add(BitmapSource image)
        {
            if (_known.TryGetValue(image, out var known)) return known.Value;
            var header = ImageHeader(image);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            Rows(image, (bytes, count) => hash.AppendData(bytes, 0, count));
            var id = Convert.ToHexString(hash.GetHashAndReset());
            if (_ids.Add(id) && _existing?.Contains(id) != true)
            {
                using var stream = _zip.CreateEntry("images/" + id, CompressionLevel.Optimal).Open();
                stream.Write(header); Rows(image, (bytes, count) => stream.Write(bytes, 0, count));
            }
            _known.Add(image, new ResourceId(id)); return id;
        }
    }

    private sealed class ResourceReader
    {
        public int SchemaVersion { get; set; } = Version;
        private readonly string _path;
        private readonly OwnedFile? _lease;
        private readonly Func<string, BitmapSource>? _fallback;
        // Shared while alive in the current scene, never pinned by the history loader.
        private readonly Dictionary<string, WeakReference<BitmapSource>> _known = new(StringComparer.Ordinal);
        public ResourceReader(string path, OwnedFile? lease = null, Func<string, BitmapSource>? fallback = null) { _path = path; _lease = lease; _fallback = fallback; }
        public BitmapSource Get(string id)
        {
            if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid image resource ID");
            if (_known.TryGetValue(id, out var weak) && weak.TryGetTarget(out var cached)) return cached;
            using var file = OpenArchive(_path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = zip.GetEntry("images/" + id);
            if (entry == null) return _fallback?.Invoke(id) ?? throw new InvalidDataException("Missing image resource");
            if (entry.Length > MaxImageBytes + 4096) throw new InvalidDataException("Image resource too large");
            using var stream = entry.Open(); using var r = new BinaryReader(stream);
            var width = r.ReadInt32(); var height = r.ReadInt32(); var dpiX = r.ReadDouble(); var dpiY = r.ReadDouble(); var formatId = r.ReadByte();
            if (width < 1 || height < 1 || width > 100_000 || height > 100_000 || formatId >= Formats.Length ||
                !double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX <= 0 || dpiY <= 0) throw new InvalidDataException("Invalid image header");
            var format = Formats[formatId]; var count = r.ReadInt32(); if (count < 0 || count > 256) throw new InvalidDataException("Invalid palette");
            var colors = new List<Color>(count); for (var i = 0; i < count; i++) colors.Add(Color.FromArgb(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
            var stride = checked((width * format.BitsPerPixel + 7) / 8); var length = checked((long)stride * height);
            if (length > MaxImageBytes || length != entry.Length - (4 + 4 + 8 + 8 + 1 + 4 + count * 4)) throw new InvalidDataException("Invalid image length");
            var resident = _known.Values.Select(weak => weak.TryGetTarget(out var held) ? held.PixelWidth * (long)((held.Format.BitsPerPixel + 7) / 8) * held.PixelHeight : 0).Sum();
            if (resident + length > MaxImageBytes) throw new InvalidDataException("Decoded pin resources exceed the memory limit");
            var data = new byte[(int)length]; stream.ReadExactly(data);
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, format, colors.Count == 0 ? null : new BitmapPalette(colors), data, stride);
            bitmap.Freeze(); _known[id] = new WeakReference<BitmapSource>(bitmap); return bitmap;
        }
    }
}
