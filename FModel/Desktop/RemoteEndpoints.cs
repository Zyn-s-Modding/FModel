using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

namespace FModel.Desktop;

public sealed class RemoteEndpoint
{
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsConfigured => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && Path.Length > 0;

    internal static RemoteEndpoint Default(string gameName, bool mappings)
    {
        var game = gameName.StartsWith("Fortnite", StringComparison.OrdinalIgnoreCase) ? "fortnite" :
            gameName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase) ? "valorant" : null;
        return game is null ? new() : new()
        {
            Url = $"https://uedb.dev/svc/api/v1/{game}/{(mappings ? "mappings" : "aes")}",
            Path = mappings ? "$.mappings.ZStandard" : "$.['mainKey','dynamicKeys']"
        };
    }
}

internal static class RemoteEndpoints
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static async Task<JToken> Request(RemoteEndpoint endpoint, CancellationToken token)
    {
        var body = await Client.GetStringAsync(endpoint.Url, token).ConfigureAwait(false);
        return JToken.Parse(body);
    }

    internal static Dictionary<string, string> ReadKeys(JToken body, string expression)
    {
        var values = body.SelectTokens(expression).ToArray();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string guid, string key)
        {
            key = key.Trim();
            if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = "0x" + key;
            _ = new FAesKey(key);
            keys[new FGuid(guid).ToString()] = key;
        }
        if (values.ElementAtOrDefault(0)?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(values[0].ToString()))
            Add("00000000000000000000000000000000", values[0].ToString());
        if (values.ElementAtOrDefault(1) is JArray dynamicKeys)
            foreach (var key in dynamicKeys)
                if (key["guid"] is { } guid && key["key"] is { } value) Add(guid.ToString(), value.ToString());
        if (keys.Count == 0) throw new InvalidDataException("The endpoint expression returned no AES keys.");
        return keys;
    }

    internal static async Task<Dictionary<string, string>> FetchKeys(RemoteEndpoint endpoint, CancellationToken token) =>
        ReadKeys(await Request(endpoint, token).ConfigureAwait(false), endpoint.Path);

    internal static async Task<string> FetchMappings(RemoteEndpoint endpoint, CancellationToken token)
    {
        var response = await Request(endpoint, token).ConfigureAwait(false);
        var values = response.SelectTokens(endpoint.Path).ToArray();
        var url = values.FirstOrDefault()?.ToString() ?? throw new InvalidDataException("The endpoint expression returned no mapping URL.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("The mapping URL must be an HTTP or HTTPS address.");
        var bytes = await Client.GetByteArrayAsync(uri, token).ConfigureAwait(false);
        var folder = System.IO.Path.Combine(DesktopSettings.DataDirectory, "Mappings");
        Directory.CreateDirectory(folder);
        var name = System.IO.Path.GetFileName(values.ElementAtOrDefault(1)?.ToString() ?? uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name)) name = "mapping.usmap";
        var destination = System.IO.Path.Combine(folder, Convert.ToHexString(SHA256.HashData(bytes)) + "-" + name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            _ = new FileUsmapTypeMappingsProvider(temporary);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
