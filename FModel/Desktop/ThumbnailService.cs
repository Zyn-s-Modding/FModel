using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.Paper2D;
using CUE4Parse.UE4.Assets.Exports.Paper2D;
using FModel.Extensions;
using SkiaSharp;
using SixLabors.ImageSharp;

namespace FModel.Desktop;

internal sealed class ThumbnailService(ArchiveWorkspace workspace, DesktopSettings settings)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _workers = new(2);
    private readonly Dictionary<string, (byte[]? Data, string? Type, EAssetCategory Category, EBulkType Actions)> _cache = [];
    private readonly HashSet<Task> _pending = [];
    private CancellationTokenSource _generation = new();
    private bool _paused;
    internal int LoadedCount { get { lock (_sync) return _cache.Values.Count(x => x.Data is not null); } }
    internal string? LastError { get; private set; }

    public Task<byte[]?> Load(AssetEntry entry, CancellationToken cancellation)
    {
        lock (_sync)
        {
            if (_paused || workspace.Provider is null) return Task.FromResult<byte[]?>(null);
            if (_cache.TryGetValue(entry.Path, out var cached))
            {
                entry.ResolvedAssetType = cached.Type; entry.Category = cached.Category; entry.Actions = cached.Actions;
                return Task.FromResult(cached.Data);
            }
            var provider = workspace.Provider;
            var generation = _generation.Token;
            var task = Decode();
            _pending.Add(task);
            _ = task.ContinueWith(t => { lock (_sync) _pending.Remove(t); }, TaskScheduler.Default);
            return task;

            async Task<byte[]?> Decode()
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(generation, cancellation);
                var token = linked.Token;
                await _workers.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var data = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        SKBitmap? decoded = null;
                        if (entry.File.IsUePackage)
                        {
                            workspace.ResolveAsset(entry, token);
                            if (!settings.PreviewExplorerTextures || (entry.Category.GetBaseCategory() != EAssetCategory.Texture && entry.Category != EAssetCategory.ItemDefinitionBase)) return null;
                            var exports = provider.LoadPackage(entry.File).GetExports().ToArray();
                            if (exports.OfType<UTexture>().FirstOrDefault() is { } texture)
                            {
                                entry.ResolvedAssetType = texture.ExportType;
                                decoded = texture.Decode(128, settings.ExportOptions(workspace.Profile!.TexturePlatform), token)?.ToSkBitmap();
                            }
                            else if (exports.OfType<UPaperSprite>().FirstOrDefault() is { } sprite)
                                decoded = sprite.Decode(settings.ExportOptions(workspace.Profile!.TexturePlatform), cancellationToken: token)?.ToSkBitmap();
                            else if (exports.OfType<UPaperFlipbook>().FirstOrDefault() is { } flipbook)
                            {
                                var animation = flipbook.Decode(settings.ExportOptions(workspace.Profile!.TexturePlatform), cancellationToken: token);
                                if (animation is not null)
                                {
                                    using var frames = animation.ToImage(settings.ExportOptions(workspace.Profile!.TexturePlatform), 128, token);
                                    using var first = frames.Frames.CloneFrame(0);
                                    using var png = new MemoryStream();
                                    first.SaveAsPng(png);
                                    decoded = SKBitmap.Decode(png.ToArray());
                                }
                            }
                            else if (exports.OfType<CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets.UItemDefinitionBase>().FirstOrDefault() is { } item)
                            {
                                decoded = ItemPreview(item);
                                if (decoded is null && item is CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets.UAthenaPickaxeItemDefinition pickaxe &&
                                    pickaxe.WeaponDefinition.TryLoad<CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets.UItemDefinitionBase>(out var weapon))
                                    decoded = ItemPreview(weapon);
                            }

                            SKBitmap? ItemPreview(CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets.UItemDefinitionBase item)
                            {
                                foreach (var part in item.DataList ?? [])
                                    if (part.NonConstStruct is not null && part.NonConstStruct.TryGetValue(out CUE4Parse.UE4.Objects.UObject.FSoftObjectPath icon, "Icon", "LargeIcon") &&
                                        icon.TryLoad<UTexture2D>(out var texture))
                                        return texture.Decode(128, settings.ExportOptions(workspace.Profile!.TexturePlatform), token)?.ToSkBitmap();
                                return null;
                            }
                        }
                        else if (entry.Type.ToLowerInvariant() is "png" or "jpg" or "jpeg" or "webp" or "bmp") decoded = SKBitmap.Decode(provider.SaveAsset(entry.File));
                        using var bitmap = decoded;
                        if (bitmap is null) return null;
                        var ratio = Math.Min(128.0 / bitmap.Width, 128.0 / bitmap.Height);
                        using var scaled = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * ratio)), Math.Max(1, (int)(bitmap.Height * ratio))), SKFilterQuality.Medium);
                        using var image = SKImage.FromBitmap(scaled);
                        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                        token.ThrowIfCancellationRequested();
                        return encoded.ToArray();
                    }, token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (!generation.IsCancellationRequested)
                        {
                            if (_cache.Count >= 256) _cache.Remove(_cache.Keys.First());
                            _cache[entry.Path] = (data, entry.ResolvedAssetType, entry.Category, entry.Actions);
                        }
                    }
                    return data;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { LastError = error.Message; Serilog.Log.Debug(error, "No thumbnail for {Path}", entry.Path); return null; }
                finally { _workers.Release(); }
            }
        }
    }

    public async Task ResetAsync()
    {
        Task[] pending;
        CancellationTokenSource previous;
        lock (_sync)
        {
            _paused = true;
            previous = _generation;
            previous.Cancel();
            _generation = new();
            _cache.Clear();
            pending = _pending.ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { previous.Dispose(); }
    }

    public void Resume() { lock (_sync) _paused = false; }
}
