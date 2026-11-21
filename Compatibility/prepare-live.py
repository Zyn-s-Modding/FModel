"""Reuse FModel's Valorant manifest parser/streams and endpoint constants."""
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
destination.mkdir(parents=True, exist_ok=True)
api = root / "ViewModels/ApiEndpoints"
epic = (api / "EpicApiEndpoint.cs").read_text()
constants = []
for original, name in [("_OAUTH_URL", "EpicOAuthUrl"), ("_BASIC_TOKEN", "EpicClientAuthentication"), ("_APP_URL", "EpicManifestUrl")]:
    value = re.search(r'private const string ' + original + r' = ("[^"]+");', epic)[1]
    constants.append(f"    internal const string {name} = {value};")
(destination / "LiveApiConfiguration.cs").write_text("namespace FModel.Desktop;\ninternal static class LiveApiConfiguration\n{\n" + "\n".join(constants) + "\n}\n")

text = (api / "ValorantApiEndpoint.cs").read_text()
imports = "\n".join(line for line in text.splitlines() if line.startswith("using ") and "FModel." not in line and "RestSharp" not in line)
text = text[text.index("public class VManifest"):]
text = text.replace("public class VManifest", "public class VManifest : IDisposable")
text = text.replace("private readonly HttpClient _client;", "private readonly HttpClient _client;\n    private readonly CancellationToken _sessionToken;")
text = text.replace('VManifest(byte[] data) : this(new FByteArchive("CompressedValorantManifest", data))', 'VManifest(byte[] data, CancellationToken token = default) : this(new FByteArchive("CompressedValorantManifest", data), token)')
text = text.replace("private VManifest(FArchive Ar)", "private VManifest(FArchive Ar, CancellationToken token)")
start = text.index("        _client = new HttpClient(")
end = text.index("public readonly struct VHeader")
text = text[:start] + '''        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _sessionToken = token;
    }

    public async ValueTask PrefetchChunk(VChunk chunk, CancellationToken token) => await GetChunkBytes(chunk, token).ConfigureAwait(false);
    public Task<byte[]> GetChunkBytes(VChunk chunk, CancellationToken token) => LiveChunkCache.Read(_client, chunk.GetUrl(), chunk.Id.ToString(), chunk.Size, _sessionToken, token);
    public void Dispose() => _client.Dispose();
}

''' + text[end:]
text = text.replace("if (value >= Length || value < 0)", "if (value > Length || value < 0)")
anchor = "        var (i, startPos) = GetChunkIndex(position);"
text = text.replace(anchor, '''        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("The buffer range is invalid.");
        if (position >= Length || count == 0) return 0;
        count = (int)Math.Min(count, Length - position);
''' + anchor)
start = text.index("    private async Task PrefetchAsync(")
end = text.index("    private (int Index, uint ChunkPos)", start)
text = text[:start] + '''    private async Task PrefetchAsync(int index, uint start, long count, CancellationToken token, int concurrentDownloads = 4)
    {
        var chunks = new List<VChunk>();
        while (count > 0 && index < _chunks.Length)
        {
            var chunk = _chunks[index++];
            chunks.Add(chunk);
            count -= chunk.Size - start;
            start = 0;
        }
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = concurrentDownloads, CancellationToken = token },
            (chunk, cancellation) => _manifest.PrefetchChunk(chunk, cancellation)).ConfigureAwait(false);
    }

''' + text[end:]
(destination / "ValorantManifest.cs").write_text("#nullable disable\n" + imports + "\nnamespace FModel.Desktop;\n" + text)
