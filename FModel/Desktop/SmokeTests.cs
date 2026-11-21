using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Options;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace FModel.Desktop;

// Integration checks use real cooked UE fixtures, native libraries and export files.
internal static class SmokeTests
{
    [DllImport("CUE4Parse-Natives")] private static extern nint nAllocate(nuint size, nuint alignment);
    [DllImport("CUE4Parse-Natives")] private static extern void nDeallocate(nint pointer, nuint size);
    public static async Task<int> Run(string[] args)
    {
        var repository = args.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        var root = Path.Combine(repository, "CUE4Parse", "CUE4Parse.Tests", "Fixtures");
        var temporary = args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "FModel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            CheckLuaDecompiler();
            CheckRemoteEndpoints();
            await CheckLiveArchiveReads();
            CheckSvgPreview(Path.Combine(temporary, "Images"));
            NativeTextureChecks.Run(repository);
            Check(CUE4ParseNatives.IsFeatureAvailable("ACL\0"u8), "native ACL decoder loads");
            var allocation = nAllocate(64, 16);
            Check(allocation != 0 && allocation % 16 == 0, "native ACL ABI and aligned allocator");
            nDeallocate(allocation, 64);
            foreach (var invalid in new[] { "../escape", "a/../../escape", "/absolute", "C:\\absolute", "..\\escape" })
            {
                try { ArchiveWorkspace.OutputPath(temporary, invalid); throw new Exception("Unsafe path was accepted: " + invalid); }
                catch (IOException) { }
            }
            Check(ArchiveWorkspace.OutputPath(temporary, "Game/Content/file.uasset").StartsWith(temporary + Path.DirectorySeparatorChar), "archive output paths remain inside destination");
            foreach (var (engine, version) in new[] { ("UE5_8", EGame.GAME_UE5_8), ("UE6_0", EGame.GAME_UE6_0) })
            {
                var fixture = Path.Combine(root, engine);
                var settings = new DesktopSettings { OutputDirectory = Path.Combine(temporary, engine), MeshFormat = EMeshFormat.Gltf2 };
                using var workspace = new ArchiveWorkspace { Log = Console.WriteLine };
                workspace.Open(new GameProfile { Name = engine, Directory = Path.Combine(fixture, "Pak", "Uncompressed"), Version = version }, settings, default);
                Check(workspace.Files.Length >= 4, engine + " pak archives mount");
                var ini = workspace.Files.First(x => x.Name == "DefaultGame.ini");
                Check(workspace.Preview(ini, settings, default).Text.Contains("["), engine + " text preview");
                var locres = workspace.Files.First(x => x.Type == "locres");
                using (var localization = JsonDocument.Parse(workspace.Preview(locres, settings, default).Text))
                    Check(localization.RootElement.ValueKind != JsonValueKind.Null, engine + " localization JSON preview");
                workspace.Open(new GameProfile { Name = engine, Directory = Path.Combine(fixture, "LegacyPak", "Tagged", "Zlib"), Version = version }, settings, default);
                Check(workspace.Files.Any(x => x.File.IsUePackage), engine + " Zlib packages mount");
                var properties = workspace.Files.First(x => x.Name == "DA_AllProperties.uasset");
                var json = workspace.Preview(properties, settings, default);
                using var document = JsonDocument.Parse(json.Text);
                Check(document.RootElement.GetArrayLength() > 0, engine + " package JSON");
                var ioDirectory = Path.Combine(temporary, engine, "IoStore");
                Directory.CreateDirectory(ioDirectory);
                foreach (var file in Directory.GetFiles(Path.Combine(fixture, "IoStore", "Tagged", "Uncompressed")))
                    File.Copy(file, Path.Combine(ioDirectory, Path.GetFileName(file)), overwrite: true);
                foreach (var file in Directory.GetFiles(Path.Combine(fixture, "IoStore", "Tagged"), "global.*"))
                    File.Copy(file, Path.Combine(ioDirectory, Path.GetFileName(file)), overwrite: true);
                workspace.Open(new GameProfile { Name = engine, Directory = ioDirectory, Version = version }, settings, default);
                Check(workspace.Files.Length > 70, engine + " IoStore packages mount");
                properties = workspace.Files.First(x => x.Name == "DA_AllProperties.uasset");
                var texture = workspace.Files.First(x => x.Name == "T_BC7.uasset");
                var preview = workspace.Preview(texture, settings, default);
                Check(preview.Images.Count > 0 && preview.Images[0].Width == 64, engine + " BC7 texture preview");
                var audio = workspace.Files.First(x => x.Name == "SW_Inline.uasset");
                var sound = workspace.Preview(audio, settings, default);
                Check(sound.Audio is { Length: > 44 }, engine + " cooked PCM decode");
                using (var player = new MacAudio(sound.Audio!))
                {
                    Check(player.Duration > 0.9 && player.Duration < 1.1, engine + " AVFoundation audio decode");
                    player.Position = 0.5;
                    Check(Math.Abs(player.Position - 0.5) < 0.05, engine + " audio seeking");
                    Check(player.SampleRate > 0 && player.Channels > 0 && player.ByteRate > 0, engine + " native audio format metadata");
                    var spectrum = new float[100];
                    Check(player.Spectrum(spectrum) && spectrum.Any(x => x > 0) && spectrum.All(x => float.IsFinite(x) && x >= 0 && x <= 1), engine + " native FFT spectrum");
                    Check(MacAudio.Devices().All(x => x.Name.Length > 0 && x.Id.Length > 0), engine + " native output-device enumeration");
                    using var converted = new MacAudio(player.DecodePcm());
                    Check(Math.Abs(converted.Duration - player.Duration) < 0.02 && converted.Channels == player.Channels, engine + " native PCM conversion");
                }
                await CheckSoundBank(sound.Audio!, version, Path.Combine(temporary, engine, "BankInput"));
                workspace.Queue(properties, "Properties", default);
                var queued = workspace.Exports.QueuedItems.First();
                var queuedCount = workspace.Exports.QueuedItems.Count;
                Check(workspace.Exports.Remove(queued.ObjectPath) && workspace.Exports.QueuedItems.Count == queuedCount - 1, engine + " export queue removal");
                workspace.Queue(properties, "Properties", default);
                Check(workspace.Exports.QueuedItems.Count == queuedCount && !ReferenceEquals(queued, workspace.Exports.QueuedItems.Single(item => item.ObjectPath == queued.ObjectPath)), engine + " export queue re-add replaces removed item");
                workspace.Queue(properties, "Raw", default); // session deduplicates by object path, just as the existing application does
                workspace.Queue(texture, "Auto", default);
                workspace.Queue(audio, "Auto", default);
                var mesh = workspace.Files.First(x => x.Name == "SM_Fixture.uasset");
                workspace.Queue(mesh, "Auto", default);
                var results = await workspace.Exports.RunAsync(settings.OutputDirectory, settings.ExportOptions());
                Check(results.Select(result => result.ObjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == results.Count, engine + " removed and re-added assets export once");
                foreach (var result in results) if (!result.Success) Console.Error.WriteLine(result.Error);
                Check(results.Count >= 3 && results.All(x => x.Success), engine + " JSON, raw, PNG and glTF export");
                Check(Directory.GetFiles(settings.OutputDirectory, "*.png", SearchOption.AllDirectories).Length > 0, engine + " PNG written");
                Check(Directory.GetFiles(settings.OutputDirectory, "*.glb", SearchOption.AllDirectories).Length > 0, engine + " glTF written");
                Check(Directory.GetFiles(settings.OutputDirectory, "*.wav", SearchOption.AllDirectories).Length > 0, engine + " cooked audio export");
                workspace.Queue(properties, "Raw", default);
                var rawResults = await workspace.Exports.RunAsync(settings.OutputDirectory, settings.ExportOptions());
                Check(rawResults.All(x => x.Success) && Directory.GetFiles(settings.OutputDirectory, "*.uasset", SearchOption.AllDirectories).Length > 0, engine + " raw package export");
                var animation = workspace.Files.First(x => x.Name == "AS_Fixture.uasset");
                workspace.Queue(animation, "Auto", default);
                var animationResults = await workspace.Exports.RunAsync(settings.OutputDirectory, new ExportOptions(meshFormat: EMeshFormat.UEFormat));
                if (animationResults.Count > 0 && animationResults.All(x => x.Success)) Console.WriteLine("PASS: " + engine + " animation export");
                else
                {
                    // These fixtures omit the imported /Engine animation compression settings.
                    // Do not report decoder availability as a successful animation conversion.
                    Check(animationResults.Count > 0 && animationResults.All(x => x.Error is ArgumentOutOfRangeException && x.Error.Message.Contains("Unsupported compressed data type")), engine + " missing animation-codec error surfaced");
                    Console.WriteLine("SKIP: " + engine + " animation conversion: fixture is missing its imported compression codec; runtime decoding remains unverified.");
                }
                workspace.Open(new GameProfile
                {
                    Name = engine, Directory = Path.Combine(fixture, "LegacyPak", "Unversioned", "Uncompressed"), Version = version,
                    MappingsPath = Path.Combine(fixture, "Mappings", "CUE4ParseFixtures-Uncompressed.usmap")
                }, settings, default);
                properties = workspace.Files.First(x => x.Name == "DA_AllProperties.uasset");
                Check(workspace.Preview(properties, settings, default).Exports.Length > 0, engine + " unversioned packages with usmap mappings");
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { workspace.Preview(properties, settings, cancellation.Token); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { Console.WriteLine("PASS: cancellation"); }
                workspace.Open(new GameProfile
                {
                    Name = engine, Directory = Path.Combine(fixture, "LegacyPak", "Tagged", "OodleEncrypted"), Version = version,
                    AesKeys = new Dictionary<string, string> { ["00000000000000000000000000000000"] = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("CUE4ParseFixtureAESKey0123456789")) }
                }, settings, default);
                Check(workspace.Files.Length > 0, engine + " AES encrypted Oodle archive mount");
                properties = workspace.Files.First(x => x.Name == "DA_AllProperties.uasset");
                Check(workspace.Preview(properties, settings, default).Exports.Length > 0, engine + " managed Oodle decompression");
                workspace.Open(new GameProfile { Name = engine, Directory = Path.Combine(fixture, "AndroidPak"), Version = version }, settings, default);
                var astc = workspace.Files.First(x => x.Name == "T_ASTC_4x4.uasset");
                Check(workspace.Preview(astc, settings, default).Images.Count > 0, engine + " Android ASTC textures");
            }
            Console.WriteLine("All macOS integration checks passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (args.Length < 2) Directory.Delete(temporary, true); }
    }

    private static void CheckRemoteEndpoints()
    {
        var mainKey = new string('1', 64);
        var dynamicKey = new string('2', 64);
        var body = new Newtonsoft.Json.Linq.JObject
        {
            ["mainKey"] = mainKey,
            ["dynamicKeys"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
            { ["guid"] = "00000001000000020000000300000004", ["key"] = dynamicKey })
        };
        var keys = RemoteEndpoints.ReadKeys(body, RemoteEndpoint.Default("Fortnite", false).Path);
        Check(keys.Count == 2 && keys.Values.Contains("0x" + mainKey) && keys.Values.Contains("0x" + dynamicKey), "Original AES endpoint JSONPath and dynamic-key parsing");
    }

    private static async Task CheckLiveArchiveReads()
    {
        var id = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray());
        var chunk = "Live archive stream regression"u8.ToArray();
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
        {
            writer.Write(id); writer.Write((uint)chunk.Length);
            writer.Write(1ul); writer.Write((uint)chunk.Length); writer.Write(1); writer.Write(0u);
            var name = "test.pak"u8.ToArray(); writer.Write((byte)name.Length); writer.Write(name);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, true))
            zlib.Write(payload.ToArray());
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0xC3D088F7u); writer.Write(33u); writer.Write(1ul);
            writer.Write((uint)payload.Length); writer.Write((uint)compressed.Length); writer.Write(1u); writer.Write(1u); writer.Write((byte)0);
            writer.Write(compressed.ToArray());
        }
        var cache = Path.Combine(DesktopSettings.DataDirectory, "Cache", "Chunks", "Valorant", id + ".chunk");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllBytesAsync(cache, chunk);
        try
        {
            using var manifest = new VManifest(bytes.ToArray());
            using var stream = manifest.Paks[0].GetStream(manifest);
            var output = new byte[chunk.Length + 8];
            var read = await stream.ReadAsync(output, 3, output.Length - 3, default);
            Check(read == chunk.Length && output.AsSpan(3, read).SequenceEqual(chunk), "Valorant cached archive reads clamp at EOF");
            stream.Position = stream.Length;
            Check(stream.Read(output, 0, 1) == 0 && stream.ReadAt(stream.Length + 1, output, 0, 1) == 0, "Valorant archive EOF position and reads");
            Check(stream.ReadAt(1, output, 0, 0) == 0, "Valorant zero-length archive read");
        }
        finally { File.Delete(cache); }
    }

    private static void CheckSvgPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        var source = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"16\"><rect width=\"32\" height=\"16\" fill=\"#ff0000\"/></svg>";
        File.WriteAllText(Path.Combine(directory, "Preview.svg"), source);
        using var workspace = new ArchiveWorkspace();
        var settings = new DesktopSettings();
        workspace.Open(new GameProfile { Name = "SVG regression", Directory = directory, Version = EGame.GAME_UE5_8 }, settings, default);
        var entry = new AssetEntry(new CUE4Parse.FileProvider.Objects.OsGameFile(new FileInfo(Path.Combine(directory, "Preview.svg")), new VersionContainer(EGame.GAME_UE5_8)));
        var image = workspace.Preview(entry, settings, default).Images.Single();
        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);
        Check(bitmap.Width == 512 && bitmap.GetPixel(128, 128).Red == 255 && image.EncodedFormat == "svg" &&
            System.Text.Encoding.UTF8.GetString(image.EncodedData!) == source, "SVG preview preserves original vector export data");
        var profile = GameProfiles.Clone(workspace.Profile!);
        profile.AesEndpoint = new RemoteEndpoint { Url = "http://127.0.0.1:1/", Path = "$" };
        settings.AesReload = EAesReload.Never;
        workspace.Open(profile, settings, default);
        Check(workspace.Profile!.AesKeys.Count == 0, "Never AES reload policy skips its endpoint");
        profile.LastAesReload = DateTime.Today;
        settings.AesReload = EAesReload.OncePerDay;
        workspace.Open(profile, settings, default);
        Check(workspace.Profile!.AesKeys.Count == 0, "Once-per-day AES policy reuses today's keys");
        profile.AesKeys["00000000000000000000000000000000"] = "0x" + new string('1', 64);
        settings.AesReload = EAesReload.Always;
        var messages = new List<string>(); workspace.Log = messages.Add;
        workspace.Open(profile, settings, default);
        Check(workspace.Profile!.AesKeys.Count == 1 && messages.Any(message => message.Contains("Using saved keys")), "Unavailable AES endpoint preserves saved keys");
        var previousProvider = workspace.Provider;
        var previousProfile = workspace.Profile;
        var invalid = GameProfiles.Clone(previousProfile!);
        invalid.MappingsPath = Path.Combine(directory, "Missing-" + Guid.NewGuid().ToString("N") + ".usmap");
        try { workspace.Open(invalid, settings, default); throw new InvalidOperationException("The missing mapping was accepted."); }
        catch (FileNotFoundException) { }
        Check(ReferenceEquals(previousProvider, workspace.Provider) && ReferenceEquals(previousProfile, workspace.Profile) &&
            workspace.Profile!.MappingsPath != invalid.MappingsPath, "Failed reload preserves the committed provider and profile");
    }

    private static void CheckLuaDecompiler()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(new byte[] { 0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 8, 4, 8, 0 });
            var source = "@smoke.lua\0"u8.ToArray();
            writer.Write((ulong)source.Length); writer.Write(source);
            writer.Write(0); writer.Write(0);
            writer.Write(new byte[] { 0, 0, 2, 2 });
            writer.Write(3); writer.Write(1u); writer.Write(0x0100001eu); writer.Write(0x0080001eu);
            writer.Write(1); writer.Write((byte)3); writer.Write(42.0);
            writer.Write(0); writer.Write(3); writer.Write(1); writer.Write(1); writer.Write(1); writer.Write(0); writer.Write(0);
        }
        var bytes = stream.ToArray();
        var text = MacLua.Read(bytes, CUE4Parse.UE4.Lua.unluac.EUnluacFlags.Decompile, null, default);
        Check(text.Contains("return 42"), "Original native Lua 5.1 decompilation");
        var assembly = MacLua.Read(bytes, CUE4Parse.UE4.Lua.unluac.EUnluacFlags.Disassemble, null, default);
        Check(assembly.Contains("loadk", StringComparison.OrdinalIgnoreCase), "Original native Lua disassembly");
    }

    private static async Task CheckSoundBank(byte[] wave, EGame version, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "TestBank.bnk");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("BKHD"u8); writer.Write(20);
            writer.Write(135u); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write(1u);
            writer.Write("DIDX"u8); writer.Write(12);
            writer.Write(123u); writer.Write(0u); writer.Write(wave.Length);
            writer.Write("DATA"u8); writer.Write(wave.Length); writer.Write(wave);
        }
        var settings = new DesktopSettings { OutputDirectory = Path.Combine(directory, "Exports") };
        using var workspace = new ArchiveWorkspace();
        workspace.Open(new GameProfile { Directory = directory, Version = version }, settings, default);
        var bank = workspace.Files.Single(x => x.Name == "TestBank.bnk");
        var preview = workspace.Preview(bank, settings, default);
        Check(preview.Sounds is { Count: 1 } && preview.Sounds[0].Data.AsSpan().SequenceEqual(wave), version + " Wwise bank media extraction");
        Check(workspace.Queue(bank, "Audio", default) == 1, version + " Wwise bank audio queue");
        var results = await workspace.Exports.RunAsync(settings.OutputDirectory, settings.ExportOptions());
        Check(results.Count == 1 && results[0].Success && results[0].DiskFilePaths!.All(File.Exists), version + " Wwise bank media export");
        settings.ConvertAudioOnBulkExport = true;
        workspace.Queue(bank, "Audio", default);
        var converted = await workspace.Exports.RunAsync(settings.OutputDirectory, settings.ExportOptions());
        Check(converted.Count == 1 && converted[0].Success && converted[0].DiskFilePaths!.All(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), version + " original bulk bank audio conversion");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
