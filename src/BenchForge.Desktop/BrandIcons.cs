using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal static class BrandIcons
{
    private const string AssetRoot = "avares://BenchForge.Desktop/Assets/Branding/";
    private static readonly Lazy<Bitmap> Logo = new(() => LoadBitmap("benchforge.png"));
    private static readonly Dictionary<TargetKind, Bitmap> DatabaseLogos = new();

    public static WindowIcon WindowIcon()
    {
        if (!OperatingSystem.IsWindows()) return new WindowIcon(Logo.Value);

        using var stream = AssetLoader.Open(new Uri(AssetRoot + "benchforge.ico"));
        return new WindowIcon(stream);
    }

    public static Image DatabaseMark(TargetKind target, double size)
    {
        if (!DatabaseLogos.TryGetValue(target, out var bitmap))
        {
            bitmap = LoadBitmap(target.ToString().ToLowerInvariant() + ".png");
            DatabaseLogos.Add(target, bitmap);
        }
        return Mark(bitmap, size);
    }

    private static Bitmap LoadBitmap(string name)
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + name));
        return new Bitmap(stream);
    }

    private static Image Mark(Bitmap bitmap, double size) => new()
    {
        Source = bitmap,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
}
