using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Paneless.Core;

namespace Paneless.App;

/// <summary>A remote app's pictures: its own icon (id.png, from the device or a letter tile) and the shortcut icon
/// (id.ico: the app's icon with a small Paneless logo in the bottom-right corner, so you can tell where it came from;
/// the bottom-left stays free for Windows' shortcut arrow). Must run on the UI thread (it draws with Avalonia).</summary>
public static class RemoteAppIcons
{
    static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 256];
    static Bitmap? _logo;
    static Bitmap Logo => _logo ??= new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Paneless/Assets/Paneless.png")));

    /// <summary>Writes both pictures. <paramref name="png"/> is the app's icon from the device, or null for a letter tile.</summary>
    public static void Save(RemoteApp app, byte[]? png)
    {
        Directory.CreateDirectory(RemoteAppStore.IconDir);
        using var own = png != null ? TryLoad(png) : null;
        var ownPng = own != null ? png! : Encode(LetterTile(app.Name, 256));
        File.WriteAllBytes(RemoteAppStore.IconPng(app.Id), ownPng);
        WriteIco(app);
        _cache.Remove(app.Id);
    }

    /// <summary>Rewrites the shortcut icon from the saved app icon (e.g. after the name changed and it's a letter tile).</summary>
    public static void WriteIco(RemoteApp app)
    {
        using var source = Load(app.Id, cached: false) ?? LetterTile(app.Name, 256);
        var frames = IcoSizes.Select(s => (s, Encode(Badged(source, s)))).ToList();
        File.WriteAllBytes(RemoteAppStore.IconIco(app.Id), IcoFile.FromPngs(frames));
    }

    static readonly Dictionary<string, Bitmap?> _cache = new();

    /// <summary>The app's own icon for the list (null if there is none yet).</summary>
    public static Bitmap? Load(string id, bool cached = true)
    {
        if (cached && _cache.TryGetValue(id, out var b)) return b;
        Bitmap? bmp = null;
        try { if (File.Exists(RemoteAppStore.IconPng(id))) bmp = new Bitmap(RemoteAppStore.IconPng(id)); } catch { }
        if (cached) _cache[id] = bmp;
        return bmp;
    }

    public static Bitmap? FromPng(byte[] png) => TryLoad(png);

    static Bitmap? TryLoad(byte[] png)
    {
        try { return new Bitmap(new MemoryStream(png)); } catch { return null; }
    }

    /// <summary>Testing aid: a letter tile with the badge, as the shortcut icon would look at one size.</summary>
    public static Bitmap BadgedPreview(string name, int size)
    {
        using var tile = LetterTile(name, 256);
        return Badged(tile, size);
    }

    static RenderTargetBitmap Badged(Bitmap icon, int size)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawImage(icon, new Rect(0, 0, size, size));
            // Small sizes get a bigger badge, or it would be a speck.
            var b = Math.Round(size * (size <= 32 ? 0.56 : 0.42));
            var r = new Rect(size - b, size - b, b, b);
            var ring = Math.Max(1, size / 40.0);
            ctx.DrawRectangle(Brushes.White, null, r, b * 0.24, b * 0.24);
            ctx.DrawImage(Logo, r.Deflate(ring));
        }
        return rtb;
    }

    /// <summary>A coloured rounded square with the name's first letter.</summary>
    public static RenderTargetBitmap LetterTile(string name, int size)
    {
        var letter = name.Trim().FirstOrDefault(char.IsLetterOrDigit);
        var text = letter == default ? "?" : char.ToUpper(letter, CultureInfo.CurrentCulture).ToString();
        Color[] colours = [Color.Parse("#4f8cff"), Color.Parse("#2bb673"), Color.Parse("#f5a524"), Color.Parse("#e5484d"), Color.Parse("#8e6cef"), Color.Parse("#12a5b8")];
        var colour = colours[(int)((uint)name.Aggregate(17, (h, c) => h * 31 + c) % colours.Length)];
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(colour), null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size * 0.56, Brushes.White);
            ctx.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        return rtb;
    }

    static byte[] Encode(Bitmap bmp)
    {
        using var ms = new MemoryStream();
#pragma warning disable CS0618   // PNG is what the old overload writes, and what an icon file needs
        bmp.Save(ms);
#pragma warning restore CS0618
        return ms.ToArray();
    }

    /// <summary>Deletes its pictures (when the remote app is removed).</summary>
    public static void Delete(string id)
    {
        _cache.Remove(id);
        try { File.Delete(RemoteAppStore.IconPng(id)); } catch { }
        try { File.Delete(RemoteAppStore.IconIco(id)); } catch { }
    }
}
