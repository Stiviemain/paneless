namespace Paneless.Core;

/// <summary>How the Wall is laid out (saved in settings.json): the order the user dragged the pictures into, the size of
/// every picture and of single ones the user resized, and devices hidden from the Wall. Devices are named by their
/// group id (serial number), which survives reconnects. Plain list logic, so it can be tested without a window.</summary>
public sealed class WallLayout
{
    public const int MinWidth = 120, MaxWidth = 640, DefaultWidth = 220;

    /// <summary>Width of a picture the user didn't resize on its own.</summary>
    public int Width { get; set; } = DefaultWidth;
    public List<string> Order { get; set; } = new();
    public Dictionary<string, int> Widths { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    public static int Clamp(double width) => (int)Math.Round(Math.Clamp(double.IsFinite(width) ? width : DefaultWidth, MinWidth, MaxWidth));

    public int WidthOf(string id) => Widths.TryGetValue(id, out var w) ? Clamp(w) : Clamp(Width);
    public bool HasOwnWidth(string id) => Widths.ContainsKey(id);

    public void SetWidth(string id, double width) => Widths[id] = Clamp(width);
    public void ClearWidth(string id) => Widths.Remove(id);

    public bool IsHidden(string id) => Hidden.Contains(id);
    public void Hide(string id) { if (!Hidden.Contains(id)) Hidden.Add(id); }
    public void ShowAll() => Hidden.Clear();

    /// <summary>The ids in Wall order: devices the user placed keep their place; the others follow in the order given
    /// (by name).</summary>
    public List<string> Sort(IReadOnlyList<string> ids)
    {
        var placed = Order.Where(ids.Contains).Distinct().ToList();
        return placed.Concat(ids.Where(id => !placed.Contains(id))).ToList();
    }

    /// <summary>Moves <paramref name="id"/> to <paramref name="index"/> among <paramref name="shown"/> (the pictures on screen,
    /// in order). Devices not on screen now (offline, hidden, in another group) keep their places, so they come back
    /// where they were.</summary>
    public void Move(IReadOnlyList<string> shown, string id, int index)
    {
        var list = shown.ToList();
        if (!list.Remove(id)) return;
        list.Insert(Math.Clamp(index, 0, list.Count), id);
        var full = Order.Distinct().Concat(shown.Where(s => !Order.Contains(s))).ToList();
        var slots = full.Select((x, i) => (x, i)).Where(p => list.Contains(p.x)).Select(p => p.i).ToList();
        for (var k = 0; k < slots.Count; k++) full[slots[k]] = list[k];
        Order = full;
    }

    /// <summary>Back to sorting by name.</summary>
    public void ResetOrder() => Order.Clear();
}
