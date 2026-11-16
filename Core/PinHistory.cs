namespace Markshot.Core;

public sealed class PinEntry
{
    public BitmapSource? Image { get; set; }

    public string? FilePath { get; set; }

    /// <summary>Session archive handle only; never decoded full-size pixels/history in the closed-pin list.</summary>
    public PinArchive? Archive { get; set; }

    public PinArchive? LoadArchive() => Archive ??
        (FilePath != null && Path.GetExtension(FilePath).Equals(PinArchive.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(FilePath)
            ? new PinArchive(FilePath) : null);

    public string Group { get; set; } = "默认";

    public DateTime Created { get; set; } = DateTime.Now;

    private BitmapSource? _thumbnail;

    /// <summary>The full picture: in memory until it is on disk, then read from the file each time (not kept for every entry).</summary>
    public BitmapSource? LoadImage() => Image ?? Read(0);

    /// <summary>A small picture for the history window, never a reference to the full-size image.</summary>
    public BitmapSource? Thumbnail()
    {
        if (_thumbnail != null) return _thumbnail;
        if (Image == null) return _thumbnail = Read(320);
        var scale = Math.Min(1, 320.0 / Math.Max(Image.PixelWidth, Image.PixelHeight));
        BitmapSource small = Image;
        if (scale < 1) { small = new TransformedBitmap(Image, new ScaleTransform(scale, scale)); small.Freeze(); }
        // Detach the scaled thumbnail from the original's pixel storage.
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(small)); encoder.Save(stream); stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return _thumbnail = bitmap;
    }

    private BitmapSource? Read(int width)
    {
        if (LoadArchive() is { } archive)
        {
            try { return width == 0 ? archive.LoadImage() : archive.Thumbnail(); }
            catch (Exception ex) { Log.Error("读取可编辑贴图历史失败", ex); return null; }
        }
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (width > 0)
            {
                bitmap.DecodePixelWidth = width;
            }

            bitmap.UriSource = new Uri(FilePath);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("读取贴图历史失败：" + FilePath, ex);
            return null;
        }
    }
}

public sealed class PinHistory
{
    private readonly List<PinEntry> _items = new();

    public int Capacity { get; } = 30;

    public IReadOnlyList<PinEntry> Items => _items;

    public PinEntry? Last => _items.Count == 0 ? null : _items[^1];

    public void Add(PinEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        _items.Add(entry);
        while (_items.Count > Capacity)
        {
            _items.RemoveAt(0);
        }
    }

    public void Add(BitmapSource image, string group)
    {
        if (image == null)
        {
            return;
        }

        Add(new PinEntry { Image = image, Group = group });
    }

    /// <summary>Takes an entry out once it was restored, so the next restore goes one further back.</summary>
    public void Remove(PinEntry entry) => _items.Remove(entry);

    public void Clear() => _items.Clear();
}
