using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Paper2D;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.Paper2D;
using SixLabors.ImageSharp;
using SkiaSharp;
using Svg.Skia;

namespace FModel.Desktop;

public sealed partial class ArchiveWorkspace
{
    private List<PreviewImage> PreviewImages(UObject export, DesktopSettings settings, CancellationToken token)
    {
        var options = settings.ExportOptions(Profile!.TexturePlatform);
        switch (export)
        {
            case UTexture2DArray array:
                var slices = array.DecodeTextureArray(array.GetMipIndexByMaxSize(settings.PreviewMaxTextureSize), options, token) ?? [];
                return slices.Select((slice, index) => TexturePreview(array.Name + "_" + index, slice, token)).ToList();
            case UTexture texture:
                var decoded = texture.Decode(settings.PreviewMaxTextureSize, options, token);
                return decoded is null ? [] : [TexturePreview(texture.Name, decoded, token)];
            case UPaperSprite sprite:
                var image = sprite.Decode(options, cancellationToken: token);
                return image is null ? [] : [TexturePreview(sprite.Name, image, token)];
            case UPaperFlipbook flipbook:
                var animation = flipbook.Decode(options, cancellationToken: token);
                if (animation is null) return [];
                using (var frames = animation.ToImage(options, settings.PreviewMaxTextureSize, token))
                {
                    var previews = new List<PreviewFrame>();
                    for (var index = 0; index < frames.Frames.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        using var frame = frames.Frames.CloneFrame(index);
                        using var png = new MemoryStream();
                        frame.SaveAsPng(png);
                        var duration = animation.FramesPerSecond > 0 ? Math.Max(1.0 / 120, animation.Frames[index].FrameRun / animation.FramesPerSecond) : 1;
                        previews.Add(new PreviewFrame(png.ToArray(), TimeSpan.FromSeconds(duration)));
                    }
                    using var encoded = new MemoryStream(); frames.SaveAsPng(encoded);
                    return [new(flipbook.Name, previews[0].Data, frames.Width, frames.Height, Frames: previews, EncodedData: encoded.ToArray())];
                }
            case CUE4Parse.GameTypes.KRD.Assets.Exports.USvgAsset svg:
                var data = svg.GetOrDefault<byte[]>("SvgData");
                return data is { Length: > 0 } ? [SvgPreview(Path.GetFileName((svg.GetOrDefault<string>("SourceFile") ?? svg.Name + ".svg").Replace('\\', '/')), data)] : [];
            case CUE4Parse.GameTypes.Dawnwalker.Assets.Exports.UBiomesMaskTextureData { MaskData: not null } mask:
                using (var bitmap = SKBitmap.Decode(mask.MaskData.Value))
                {
                    if (bitmap is null) return [];
                    using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    return [new(mask.Name, png.ToArray(), bitmap.Width, bitmap.Height)];
                }
            default:
                return CreatorPlatform.Render(Provider!, settings, options, export, token);
        }
    }

    private static PreviewImage TexturePreview(string name, CTexture texture, CancellationToken token) =>
        new(name, texture.Encode(new ExportOptions(exportHdrTexturesAsHdr: false), out _, token), texture.Width, texture.Height, texture);

    private static PreviewImage SvgPreview(string name, byte[] data)
    {
        using var source = new MemoryStream(data);
        using var svg = new SKSvg(); svg.Load(source);
        if (svg.Picture is null) throw new InvalidDataException("The SVG has no drawable image.");
        var bounds = svg.Picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidDataException("The SVG has an empty canvas.");
        using var bitmap = new SKBitmap(512, 512);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(512 / bounds.Width, 512 / bounds.Height);
            canvas.Scale(scale); canvas.Translate(-bounds.Left, -bounds.Top); canvas.DrawPicture(svg.Picture);
        }
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new(name, png.ToArray(), bitmap.Width, bitmap.Height, EncodedData: data, EncodedFormat: "svg");
    }
}
