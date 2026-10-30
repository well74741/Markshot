namespace Markshot.Core;

/// <summary>Keeps the most recently closed pins so they can be restored.</summary>
public sealed class PinHistory
{
    private readonly List<BitmapSource> _items = new();

    public int Capacity { get; } = 12;

    public BitmapSource? Last => _items.Count == 0 ? null : _items[^1];

    public void Add(BitmapSource image)
    {
        if (image == null)
        {
            return;
        }

        _items.Add(image);
        while (_items.Count > Capacity)
        {
            _items.RemoveAt(0);
        }
    }

    public void Clear() => _items.Clear();
}
