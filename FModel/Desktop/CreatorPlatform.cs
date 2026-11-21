using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using FModel.Creator;
using FModel.ViewModels.ApiEndpoints.Models;
using Newtonsoft.Json;
using SkiaSharp;

namespace FModel.Desktop;

// The original renderers access application services through these per-operation
// bindings, allowing thumbnail workers and document previews to run concurrently.
internal sealed class CreatorPlatform : IDisposable
{
    private static readonly AsyncLocal<CreatorPlatform?> Current = new();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CreatorPlatform? _previous;
    private readonly IFileProvider _provider;
    private readonly DesktopSettings _settings;
    private readonly ExportOptions _options;
    private readonly ELanguage _language;
    private readonly CancellationToken _token;
    private readonly List<Stream> _resources = [];
    private Typefaces? _typefaces;

    private CreatorPlatform(IFileProvider provider, DesktopSettings settings, ExportOptions options, CancellationToken token)
    {
        _provider = provider; _settings = settings; _options = options; _token = token;
        _language = Enum.GetValues<ELanguage>().FirstOrDefault(language =>
            provider is AbstractFileProvider fileProvider && fileProvider.GetLanguageCode(language) == settings.Language);
        _previous = Current.Value; Current.Value = this;
    }

    private static CreatorPlatform State => Current.Value ?? throw new InvalidOperationException("No cosmetic rendering operation is active.");
    internal static IFileProvider Provider => State._provider;
    internal static ExportOptions Options => State._options;
    internal static ELanguage Language => State._language;
    internal static bool DisplayAssets => State._settings.CosmeticDisplayAsset;
    internal static Typefaces Typefaces => State._typefaces ??= new Typefaces(State._provider);

    internal static Stream OpenResource(string name)
    {
        var stream = typeof(CreatorPlatform).Assembly.GetManifestResourceStream("FModel.CreatorAssets." + name)
            ?? throw new FileNotFoundException("Missing cosmetic renderer resource " + name);
        State._resources.Add(stream);
        return stream;
    }

    internal static PlaylistResponse GetPlaylist(string id) => ReadJson<PlaylistResponse>("https://fortnite-api.com/v1/playlists/" + Uri.EscapeDataString(id)) ?? new PlaylistResponse();
    internal static CommunityDesign? GetDesign(string name)
    {
        var response = ReadJson<Community>("https://api.fmodel.app/v1/designs/" + Uri.EscapeDataString(name));
        return response is null ? null : new CommunityDesign(response);
    }
    private static T? ReadJson<T>(string url)
    {
        try { return JsonConvert.DeserializeObject<T>(Client.GetStringAsync(url, State._token).GetAwaiter().GetResult()); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            State._token.ThrowIfCancellationRequested();
            Serilog.Log.Warning(error, "Could not load cosmetic design data from {Url}", url);
            return default;
        }
    }
    internal static bool TryGetBytes(Uri url, out byte[]? bytes)
    {
        try { bytes = Client.GetByteArrayAsync(url, State._token).GetAwaiter().GetResult(); return bytes.Length > 0; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            State._token.ThrowIfCancellationRequested(); bytes = null; return false;
        }
    }

    private static bool Render(UObject export, out SKBitmap[] images)
    {
        State._token.ThrowIfCancellationRequested();
        using var package = new CreatorPackage(export.Name, export.ExportType, new Lazy<UObject>(() => export), State._settings.CosmeticStyle);
        if (!package.TryConstructCreator(out var creator)) { images = []; return false; }
        creator.ParseForInfo();
        State._token.ThrowIfCancellationRequested();
        images = creator.Draw();
        return true;
    }

    internal static List<PreviewImage> Render(IFileProvider provider, DesktopSettings settings, ExportOptions options, UObject export, CancellationToken token)
    {
        using var scope = new CreatorPlatform(provider, settings, options, token);
        if (!Render(export, out var bitmaps)) return [];
        var images = new List<PreviewImage>();
        try
        {
            foreach (var bitmap in bitmaps)
            {
                token.ThrowIfCancellationRequested();
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                images.Add(new PreviewImage(export.Name, data.ToArray(), bitmap.Width, bitmap.Height));
            }
            return images;
        }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    public void Dispose()
    {
        Current.Value = _previous;
        foreach (var resource in _resources) resource.Dispose();
        if (_typefaces is null) return;
        foreach (var font in typeof(Typefaces).GetFields().Select(field => field.GetValue(_typefaces)).OfType<SKTypeface>().Distinct()) font.Dispose();
    }
}
