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
        if (target == TargetKind.SqlServer)
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.Parse("#E45252")),
                Geometry = Geometry.Parse("M 3,7 C 3,1 29,1 29,7 L 29,25 C 29,31 3,31 3,25 Z")
            });
            drawing.Children.Add(new GeometryDrawing
            {
                Pen = new Pen(new SolidColorBrush(Color.Parse("#30191D")), 1.6),
                Geometry = Geometry.Parse("M 3,7 C 3,13 29,13 29,7 M 3,16 C 3,22 29,22 29,16 M 10,3 C 17,12 17,22 10,29 M 22,3 C 15,12 15,22 22,29")
            });
            return new Image { Source = new DrawingImage(drawing), Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        }
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
