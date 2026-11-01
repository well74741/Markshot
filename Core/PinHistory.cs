namespace Markshot.Core;

public sealed class PinEntry
{
    public BitmapSource? Image { get; set; }

    public string? FilePath { get; set; }

    public string Group { get; set; } = "默认";

    public DateTime Created { get; set; } = DateTime.Now;

    public BitmapSource? LoadImage()
    {
        if (Image != null)
        {
            return Image;
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
            bitmap.UriSource = new Uri(FilePath);
            bitmap.EndInit();
            bitmap.Freeze();
            Image = bitmap;
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

    public int Capacity { get; } = 10;

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
