using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace FModel.Desktop;

internal sealed class CosmeticImageExporter : ExporterBase
{
    private readonly List<PreviewImage> _images;
    private readonly bool _keepDirectories;
    internal CosmeticImageExporter(UObject export, List<PreviewImage> images, bool keepDirectories = true) : base(export)
    { _images = images; _keepDirectories = keepDirectories; }
    internal CosmeticImageExporter(GameFile file, List<PreviewImage> images, bool keepDirectories = true) : base(file, "Texture")
    { _images = images; _keepDirectories = keepDirectories; }
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        var files = new List<ExportFile>();
        foreach (var image in _images)
        {
            ct.ThrowIfCancellationRequested();
            if (image.EncodedFormat == "svg" && image.EncodedData is not null)
            {
                files.Add(new ExportFile("svg", image.EncodedData));
                continue;
            }
            using var stream = new MemoryStream(image.Data);
            using var bitmap = SKBitmap.Decode(stream, new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var texture = new CTexture(image.Width, image.Height, EPixelFormat.PF_R8G8B8A8, bitmap.Bytes);
            var bytes = texture.Encode(Session.Options, out var extension, ct);
            files.Add(new ExportFile(extension, bytes, _images.Count > 1 ? "_" + files.Count : ""));
        }
        return files;
    }
    protected override (string, string) ResolveOutputPath(ExportFile file) => ArchiveWorkspace.ExportFilePath(Session.BaseDirectory.FullName, SavePath + file.NameSuffix + "." + file.Extension, _keepDirectories);
}
