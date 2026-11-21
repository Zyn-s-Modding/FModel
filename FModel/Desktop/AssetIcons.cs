using System.Xml.Linq;
using FModel.Extensions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;

namespace FModel.Desktop;

internal static class AssetIcons
{
    internal static Control Named(string name, double size = 16, string color = "#F0F0F0") => new Path
    {
        Data = Geometries.Value.GetValueOrDefault(name), Fill = Brush.Parse(color), Width = size, Height = size, Stretch = Stretch.Uniform
    };

    private static readonly Lazy<Dictionary<string, Geometry>> Geometries = new(() =>
    {
        using var stream = typeof(AssetIcons).Assembly.GetManifestResourceStream("FModel.Views.Resources.Icons.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var result = new Dictionary<string, Geometry>();
        foreach (var item in document.Root!.Elements().Where(x => x.Name.LocalName == "Geometry"))
        {
            try { result[(string)item.Attribute(xaml + "Key")!] = Geometry.Parse(item.Value); }
            catch (FormatException) { }
        }
        return result;
    });

    internal static Control For(AssetEntry file, double size = 64)
    {
        var (icon, color) = file.Type.ToLowerInvariant() switch
        {
            _ when file.ResolvedAssetType?.StartsWith("Texture") == true => ("TextureIcon", "MediumPurple"),
            "umap" => ("WorldIcon", "Orange"),
            "wav" or "ogg" or "wem" or "mp3" or "flac" => ("AudioIcon", "MediumSeaGreen"),
            "png" or "jpg" or "webp" or "tga" => ("TextureIcon", "MediumPurple"),
            _ when file.Name.StartsWith("T_") || file.Name.StartsWith("T-") => ("TextureIcon", "MediumPurple"),
            _ when file.Name.StartsWith("SK_") => ("SkeletalMeshIconAlt", "DarkTurquoise"),
            _ when file.Name.StartsWith("SM_") => ("StaticMeshIconAlt", "SteelBlue"),
            _ when file.Name.StartsWith("MI_") || file.Name.StartsWith("M_") => ("MaterialIcon", "BurlyWood"),
            _ when file.Name.StartsWith("ABP_") || file.Name.StartsWith("BP_") => ("BlueprintIcon", "DodgerBlue"),
            _ when file.Name.EndsWith("_M.uasset") || file.Name.EndsWith("_Anim.uasset") => ("AnimationIcon", "Coral"),
            _ => ("EmbeddedFilesIcon", "LightGray")
        };
        if (file.ResolvedAssetType is not null)
            (icon, color) = file.Category.GetBaseCategory() switch
            {
                EAssetCategory.Blueprints => ("BlueprintIcon", "DodgerBlue"),
                EAssetCategory.Mesh => (file.Category == EAssetCategory.SkeletalMesh ? "SkeletalMeshIconAlt" : "StaticMeshIconAlt", "SteelBlue"),
                EAssetCategory.Texture => ("TextureIcon", "MediumPurple"),
                EAssetCategory.Materials => ("MaterialIcon", "BurlyWood"),
                EAssetCategory.Animation => ("AnimationIcon", "Coral"),
                EAssetCategory.Level => ("WorldIcon", "Orange"),
                EAssetCategory.Media => ("AudioIcon", "MediumSeaGreen"),
                _ => ("EmbeddedFilesIcon", "LightGray")
            };
        return new Path { Data = Geometries.Value.GetValueOrDefault(icon), Fill = Brush.Parse(color), Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    internal static Control Folder(double size = 96)
    {
        var canvas = new Canvas { Width = 128, Height = 128 };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M20 34 H54 L62 46 H108 C112 46 115 49 115 53 V60 H13 V45 C13 39 16 34 20 34 Z"),
            Fill = Gradient("#E8C480", "#D9AA63"), Stroke = Brush.Parse("#D8A360"), StrokeThickness = 2 });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M13 60 H115 V104 C115 110 110 115 104 115 H24 C18 115 13 110 13 104 Z"),
            Fill = Gradient("#F3D9A4", "#E5BD77"), Stroke = Brush.Parse("#D8A360"), StrokeThickness = 2 });
        return new Viewbox { Child = canvas, Width = size, Height = size };
    }

    private static LinearGradientBrush Gradient(string top, string bottom) => new()
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = [new GradientStop(Color.Parse(top), 0), new GradientStop(Color.Parse(bottom), 1)]
    };
}
