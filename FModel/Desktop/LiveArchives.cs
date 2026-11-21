using System.Net.Http.Headers;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using FModel.ViewModels.ApiEndpoints.Models;

namespace FModel.Desktop;

internal static class LiveChunkCache
{
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    internal static async Task<byte[]> Read(HttpClient client, string url, string id, uint expectedSize, CancellationToken session, CancellationToken request)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session, request, ArchiveWorkspace.CurrentOperation.Value);
        var token = cancellation.Token;
        var folder = Path.Combine(DesktopSettings.DataDirectory, "Cache", "Chunks", "Valorant");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".chunk");
        var gate = Locks[id.GetHashCode(StringComparison.Ordinal) & (Locks.Length - 1)];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == expectedSize)
                return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var data = await client.GetByteArrayAsync(url, token).ConfigureAwait(false);
            if (data.LongLength != expectedSize) throw new InvalidDataException("The downloaded Valorant chunk has an unexpected size.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return data;
        }
        finally { gate.Release(); }
    }
}

internal static class LiveArchives
{
    internal static readonly HttpClient Client = new(new OperationCancellationHandler()) { Timeout = TimeSpan.FromMinutes(2) };
    internal static string ChunkDirectory => Path.Combine(DesktopSettings.DataDirectory, "Cache", "Chunks", "Fortnite");

    internal static async Task<IDisposable?> Register(StreamedFileProvider provider, CancellationToken token)
    {
        Directory.CreateDirectory(ChunkDirectory);
        if (provider.LiveGame == "ValorantLive")
        {
            using var valorantResponse = await Client.GetAsync("https://valorant-api.com/v1/fmodel/manifest", token).ConfigureAwait(false);
            if (valorantResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new IOException("The original Valorant LIVE manifest endpoint returned HTTP 404. Select a local Valorant installation in Directory → Selector.");
            valorantResponse.EnsureSuccessStatusCode();
            var data = await valorantResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            var manifest = new VManifest(data, token);
            try
            {
                foreach (var pak in manifest.Paks)
                {
                    token.ThrowIfCancellationRequested();
                    provider.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                }
                return manifest;
            }
            catch { manifest.Dispose(); throw; }
        }
        if (provider.LiveGame != "FortniteLive") throw new NotSupportedException("Unknown live game " + provider.LiveGame);
        using var authentication = new HttpRequestMessage(HttpMethod.Post, LiveApiConfiguration.EpicOAuthUrl);
        authentication.Headers.Authorization = AuthenticationHeaderValue.Parse(LiveApiConfiguration.EpicClientAuthentication);
        authentication.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var authResponse = await Client.SendAsync(authentication, token).ConfigureAwait(false);
        authResponse.EnsureSuccessStatusCode();
        var auth = JObject.Parse(await authResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var accessToken = auth.Value<string>("access_token") ?? throw new InvalidDataException("Epic returned no launcher access token.");
        using var request = new HttpRequestMessage(HttpMethod.Get, LiveApiConfiguration.EpicManifestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", accessToken);
        using var response = await Client.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var info = ManifestInfo.Deserialize(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false))
            ?? throw new InvalidDataException("Epic returned an empty manifest response.");
        var options = new ManifestParseOptions
        {
            ChunkCacheDirectory = ChunkDirectory,
            ManifestCacheDirectory = Path.Combine(DesktopSettings.DataDirectory, "Cache", "Manifests"),
            ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
            Decompressor = Compression.Decompressor,
            Client = Client,
            CacheChunksAsIs = false
        };
        Directory.CreateDirectory(options.ManifestCacheDirectory);
        var (fortnite, _) = await info.DownloadAndParseAsync(options, cancellationToken: token,
            elementDownloadPredicate: static item => item.Uri.Host is "egdownload.fastly-edge.com" or "epicgames-download1.akamaized.net" or "download.epicgames.com").ConfigureAwait(false);
        RegisterFortnite(provider, fortnite, token);
        var manifests = JsonConvert.DeserializeObject<ManifestInfoDilly[]>(await Client.GetStringAsync("https://export-service-new.dillyapis.com/v1/manifests", token).ConfigureAwait(false)) ?? [];
        var uefnUrl = manifests.FirstOrDefault(item => item.AppName == "Fortnite_Studio")?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(uefnUrl)) throw new InvalidDataException("The Fortnite Studio manifest is unavailable.");
        var studio = FBuildPatchAppManifest.Deserialize(await Client.GetByteArrayAsync(uefnUrl, token).ConfigureAwait(false), options);
        RegisterFortnite(provider, studio, token);
        return null;
    }

    private static void RegisterFortnite(StreamedFileProvider provider, FBuildPatchAppManifest manifest, CancellationToken token)
    {
        var archives = manifest.Files.Where(file => file.FileName.Replace('\\', '/').StartsWith("FortniteGame/Content/Paks/", StringComparison.OrdinalIgnoreCase) &&
            (file.FileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || file.FileName.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ||
             file.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var file in archives)
        {
            token.ThrowIfCancellationRequested();
            if (file.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = file.GetStream();
                var bytes = stream.SaveBytesAsync(8, token).GetAwaiter().GetResult();
                using var archive = new FByteArchive(file.FileName, bytes, provider.Versions);
                provider.RegisterVfs(new IoChunkToc(archive));
            }
            else provider.RegisterVfs(file.FileName, [file.GetStream()], name =>
                new FRandomAccessStreamArchive(name, manifest.FindFile(name)!.GetStream(), provider.Versions));
        }
    }

    private sealed class OperationCancellationHandler : DelegatingHandler
    {
        internal OperationCancellationHandler() : base(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ArchiveWorkspace.CurrentOperation.Value);
            return await base.SendAsync(request, operation.Token).ConfigureAwait(false);
        }
    }
}
