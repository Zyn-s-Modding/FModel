using System.Text;
using System.Text.Json;
using CUE4Parse.FileProvider.Objects;
using K4os.Compression.LZ4.Streams;

namespace FModel.Desktop;

internal sealed record BackupEntry(long Size, bool? Encrypted);

internal static class ArchiveBackup
{
    private const uint Magic = 0x504B4246;

    public static Dictionary<string, BackupEntry> Read(string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return (JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? [])
                .ToDictionary(x => x.Key, x => new BackupEntry(x.Value, null), StringComparer.OrdinalIgnoreCase);
        using var input = File.OpenRead(path);
        using var probe = new BinaryReader(input, Encoding.UTF8, true);
        var compressed = probe.ReadUInt32() == 0x184D2204;
        input.Position = 0;
        using var data = compressed ? LZ4Stream.Decode(input, leaveOpen: true) : (Stream)input;
        using var reader = new BinaryReader(data);
        var signature = reader.ReadUInt32();
        if (signature != Magic)
        {
            using var legacy = new MemoryStream();
            legacy.Write(BitConverter.GetBytes(signature));
            data.CopyTo(legacy);
            legacy.Position = 16;
            using var oldReader = new BinaryReader(legacy);
            var oldEntries = new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
            while (legacy.Position < legacy.Length)
            {
                var size = oldReader.ReadInt64();
                var encrypted = oldReader.ReadBoolean();
                _ = oldReader.ReadInt32();
                var name = oldReader.ReadString();
                _ = oldReader.ReadInt32();
                oldEntries[name.TrimStart('/')] = new BackupEntry(size, encrypted);
            }
            return oldEntries;
        }
        var version = reader.ReadByte();
        if (version is < 1 or > 2) throw new InvalidDataException($"Unsupported backup version {version}.");
        var count = reader.ReadInt32();
        if (count is < 0 or > 20_000_000) throw new InvalidDataException("Invalid backup entry count.");
        var entries = new Dictionary<string, BackupEntry>(count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var size = reader.ReadInt64(); var encrypted = reader.ReadBoolean(); var name = reader.ReadString();
            entries[name.TrimStart('/')] = new BackupEntry(size, encrypted);
        }
        return entries;
    }

    public static void Write(string path, IEnumerable<GameFile> files, CancellationToken token = default)
    {
        var entries = files.Where(x => !x.IsUePackagePayload).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            using (var compressed = LZ4Stream.Encode(output))
            using (var writer = new BinaryWriter(compressed))
            {
                writer.Write(Magic); writer.Write((byte)2); writer.Write(entries.Length);
                foreach (var file in entries)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(file.Size); writer.Write(file.IsEncrypted); writer.Write(file.Path);
                }
            }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool Changed(BackupEntry before, GameFile now)
        => before.Size != now.Size || before.Encrypted is { } encrypted && encrypted != now.IsEncrypted;
}
