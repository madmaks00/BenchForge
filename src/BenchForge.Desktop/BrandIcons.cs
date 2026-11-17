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
        if (target is TargetKind.DuckDb or TargetKind.Oracle or TargetKind.CockroachDb or TargetKind.Elasticsearch or TargetKind.OpenSearch)
        {
            var (color, path) = target switch
            {
                TargetKind.DuckDb => ("#FFE34D", "M 4,17 C 3,31 26,32 29,17 L 19,17 C 20,14 21,10 18,6 C 14,1 6,3 7,9 L 2,10 L 7,13 L 7,17 Z"),
                TargetKind.Oracle => ("#F05454", "M 10,4 L 22,4 C 37,4 37,28 22,28 L 10,28 C -5,28 -5,4 10,4 Z M 11,10 C 3,10 3,22 11,22 L 21,22 C 29,22 29,10 21,10 Z"),
                TargetKind.CockroachDb => ("#9B8BF0", "M 16,2 L 28,9 L 28,23 L 16,30 L 4,23 L 4,9 Z M 16,8 L 10,12 L 10,20 L 16,24 L 22,20 L 22,12 Z M 14,12 L 18,12 L 18,20 L 14,20 Z"),
                TargetKind.Elasticsearch => ("#F3C761", "M 4,8 C 9,-1 26,0 29,12 L 17,12 L 14,8 Z M 3,12 L 14,12 L 19,17 L 29,17 C 26,32 8,34 3,23 L 13,23 L 17,19 L 3,19 Z"),
                _ => ("#55BDE6", "M 7,3 C 20,-3 31,7 29,19 C 24,12 19,8 7,3 Z M 27,23 C 17,36 0,27 3,14 C 8,21 14,25 27,23 Z")
            };
            return new Image { Source = new DrawingImage(new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse(color)), Geometry = Geometry.Parse(path) }),
                Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        }
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
