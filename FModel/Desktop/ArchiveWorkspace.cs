using System.Collections.ObjectModel;
using System.Text;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.HonorOfKings.FileProvider;
using CUE4Parse.GameTypes.LordOfMysteries.FileProvider;
using CUE4Parse.GameTypes.MarchOfGiants.FileProvider;
using CUE4Parse.GameTypes.Theia.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Sound.Node;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject.Editor;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using FModel.Extensions;
using Formatting = Newtonsoft.Json.Formatting;

namespace FModel.Desktop;

public sealed partial class ArchiveWorkspace(Action<StreamingLevelFilterArgs, CancellationToken>? streamingLevelFilter = null) : IDisposable
{
    public AbstractVfsFileProvider? Provider { get; private set; }
    public GameProfile? Profile { get; private set; }
    public AssetEntry[] Files { get; private set; } = [];
    public FolderEntry[] Folders { get; private set; } = [];
    public ExportSession Exports { get; } = new(streamingLevelFilter);
    public Action<string>? Log { get; set; }
    private DesktopSettings? _settings;
    private IDisposable? _liveContext;
    private readonly HashSet<string> _refreshedKeys = new(StringComparer.Ordinal);
    internal static readonly AsyncLocal<CancellationToken> CurrentOperation = new();

    // The caller serializes operations: providers must never be disposed during a preview/export.
    public void Open(GameProfile profile, DesktopSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        profile = GameProfiles.Clone(profile);
        var versions = new VersionContainer(profile.Version, platform: profile.TexturePlatform, optionOverrides: profile.VersionOptions,
            customVersions: new FCustomVersionContainer(profile.CustomVersions.Select(x => new FCustomVersion(new FGuid(x.Key), x.Value))),
            mapStructTypesOverrides: profile.MapStructTypes);
        AbstractVfsFileProvider next = profile.LiveGame.Length > 0 ? new StreamedFileProvider(profile.LiveGame, versions, StringComparer.OrdinalIgnoreCase) : profile.Version switch
        {
            EGame.GAME_AshEchoes => new AEDefaultFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase),
            EGame.GAME_HonorofKingsWorld => new HoKWDefaultFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase),
            EGame.GAME_LordOfMysteries => new LoMDefaultFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase),
            EGame.GAME_ArcRaiders or EGame.GAME_Highguard or EGame.GAME_MARVELTokonFightingSouls => new TheiaFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase),
            EGame.GAME_MarchofGiants => new MogFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase),
            _ => new DefaultFileProvider(profile.Directory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase)
        };
        IDisposable? nextLiveContext = null;
        try
        {
            var endpointKey = profile.Directory + "\n" + profile.AesEndpoint.Url + "\n" + profile.AesEndpoint.Path;
            var keysRefreshed = false;
            var refreshKeys = (settings.AesReload == EAesReload.Always && !_refreshedKeys.Contains(endpointKey)) ||
                (settings.AesReload == EAesReload.OncePerDay && profile.LastAesReload.Date != DateTime.Today);
            if (refreshKeys && profile.AesEndpoint.IsConfigured)
            {
                try
                {
                    foreach (var key in RemoteEndpoints.FetchKeys(profile.AesEndpoint, token).GetAwaiter().GetResult()) profile.AesKeys[key.Key] = key.Value;
                    profile.LastAesReload = DateTime.Today;
                    keysRefreshed = true;
                }
                catch (Exception error) when (error is not OperationCanceledException && profile.AesKeys.Count > 0)
                { Log?.Invoke($"AES endpoint: {error.Message}. Using saved keys."); }
            }
            if ((string.IsNullOrWhiteSpace(profile.MappingsPath) || profile.UseRemoteMappings) && profile.MappingEndpoint.IsConfigured)
            {
                try
                {
                    profile.MappingsPath = RemoteEndpoints.FetchMappings(profile.MappingEndpoint, token).GetAwaiter().GetResult();
                    profile.UseRemoteMappings = true;
                }
                catch (Exception error) when (error is not OperationCanceledException && File.Exists(profile.MappingsPath))
                { Log?.Invoke($"Mapping endpoint: {error.Message}. Using the saved mapping file."); }
            }
            next.ReadScriptData = settings.ReadScriptData;
            next.ReadShaderMaps = settings.ReadShaderMaps;
            next.ReadNaniteData = true;
            if (!string.IsNullOrWhiteSpace(profile.MappingsPath))
                next.MappingsContainer = new FileUsmapTypeMappingsProvider(profile.MappingsPath);
            next.Initialize();
            var chunks = new DirectoryInfo(LiveArchives.ChunkDirectory);
            chunks.Create();
            next.OnDemandOptions = new CUE4Parse.UE4.IO.IoStoreOnDemandOptions
            {
                ChunkHostUri = new Uri("https://egdownload.fastly-edge.com/"),
                ChunkCacheDirectory = chunks,
                DownloaderClient = LiveArchives.Client
            };
            if (next is StreamedFileProvider streamed)
            {
                Log?.Invoke("Loading live manifests...");
                nextLiveContext = LiveArchives.Register(streamed, token).GetAwaiter().GetResult();
            }
            token.ThrowIfCancellationRequested();
            next.Mount();
            foreach (var (guid, key) in profile.AesKeys)
            {
                token.ThrowIfCancellationRequested();
                next.SubmitKey(new FGuid(guid), new FAesKey(key));
            }
            Log?.Invoke("Reading archive metadata...");
            next.PostMount();
            Log?.Invoke("Loading virtual paths...");
            next.LoadVirtualPaths();
            next.TryChangeCulture(settings.Language);
            Log?.Invoke("Building package index...");
            var (files, folders) = BuildIndex(next);
            token.ThrowIfCancellationRequested();
            // Commit only after initialization succeeds; a failed reload keeps the current workspace usable.
            Provider?.Dispose();
            _liveContext?.Dispose();
            _liveContext = nextLiveContext;
            Provider = next;
            Profile = profile;
            if (keysRefreshed) _refreshedKeys.Add(endpointKey);
            _wwise = null; _fmod = null; _criware = null;
            _settings = settings;
            Exports.Clear();
            Files = files;
            Folders = folders;
            Log?.Invoke($"Mounted {Provider.MountedVfs.Count:N0} archives, {Files.Length:N0} files. {Provider.UnloadedVfs.Count:N0} archives remain unloaded.");
        }
        catch { next.Dispose(); nextLiveContext?.Dispose(); throw; }
    }

    public void Reindex(Func<GameFile, bool>? include = null)
    {
        (Files, Folders) = BuildIndex(Provider!, include);
    }

    internal void ResolveAsset(AssetEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entry.ResolvedAssetType is not null || !entry.File.IsUePackage) return;
        var package = Provider!.LoadPackage(entry.File);
        var index = package.GetExportIndex(entry.File.NameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = package.GetExportIndex(entry.File.NameWithoutExtension + "_C", StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = 0;
        var pointer = new FPackageIndex(package, index + 1).ResolvedObject;
        if (pointer?.Object is null) return;
        var dummy = ((CUE4Parse.UE4.Assets.AbstractUePackage)package).ConstructObject(pointer.Class, package);
        (entry.Category, entry.Actions) = AssetClassification.Classify(dummy, Profile!.Version);
        if (CUE4Parse_Conversion.Dto.EmbeddedFilesDto.Supports(dummy) &&
            new CUE4Parse_Conversion.Dto.EmbeddedFilesDto(pointer.Object.Value).Files.Count > 0)
            entry.Actions |= EBulkType.EmbeddedFiles;
        entry.ResolvedAssetType = dummy.ExportType;
    }

    private static (AssetEntry[], FolderEntry[]) BuildIndex(AbstractVfsFileProvider provider, Func<GameFile, bool>? include = null)
    {
        var files = provider.Files.Values.Where(x => !x.IsUePackagePayload && (include?.Invoke(x) ?? true)).Select(x => new AssetEntry(x)).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var root = new FolderEntry("/", "");
        var folders = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var file in files)
        {
            var parent = root;
            var path = "";
            foreach (var component in file.File.Directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? component : path + "/" + component;
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new FolderEntry(component, path, parent);
                    folders[path] = folder;
                    parent.Children.Add(folder);
                }
                parent = folder;
            }
            parent.Files.Add(file);
        }
        return (files, root.Children.ToArray());
    }

    public PreviewResult Preview(AssetEntry entry, DesktopSettings settings, CancellationToken token, string? objectName = null)
    {
        var provider = Provider ?? throw new InvalidOperationException("Select a game directory first.");
        token.ThrowIfCancellationRequested();
        if (entry.File.IsUePackage)
        {
            var package = provider.GetLoadPackageResult(entry.File, objectName, settings.PreviewWorlds);
            var exports = package.Package.GetExports(package.InclusiveStart, package.PageSize).ToArray();
            token.ThrowIfCancellationRequested();
            var json = JsonConvert.SerializeObject(package.GetDisplayData(), Formatting.Indented);
            var documents = new List<PreviewText>();
            if (exports.OfType<CUE4Parse.UE4.Assets.Exports.Verse.UVerseDigest>().FirstOrDefault()?.ReadableCode is { } verse)
                documents.Add(new PreviewText(exports.OfType<CUE4Parse.UE4.Assets.Exports.Verse.UVerseDigest>().First().Name + ".verse", Encoding.UTF8.GetString(verse), "Verse"));
            else if (exports.OfType<CUE4Parse.GameTypes.ACE8.Assets.Exports.ULuaScript>().FirstOrDefault()?.Code is { } lua)
                documents.Add(new PreviewText(exports.OfType<CUE4Parse.GameTypes.ACE8.Assets.Exports.ULuaScript>().First().Name + ".lua", lua, "Lua"));
            List<PreviewImage> images = [];
            var sounds = new List<PreviewSound>();
            foreach (var export in exports)
            {
                try { images.AddRange(PreviewImages(export, settings, token)); }
                catch (Exception error) when (error is not OperationCanceledException) { Log?.Invoke($"Image {export.Name}: {error.Message}"); }
                try { sounds.AddRange(ExtractSounds(export, token)); }
                catch (Exception error) when (error is not OperationCanceledException) { Log?.Invoke($"Audio {export.Name}: {error.Message}"); }
                if (SnooperScene.ShouldPreview(export, settings)) break;
            }
            return new(json, exports, images, sounds.FirstOrDefault()?.Data, sounds.FirstOrDefault()?.Format, sounds, TitleExtra: package.TabTitleExtra, TextDocuments: documents);
        }
        if (entry.Type.ToLowerInvariant() is "bnk" or "pck" or "bank" or "fsb" or "awb" or "acb")
            return PreviewAudioBank(entry, token)!;
        switch (entry.File.Extension.ToLowerInvariant())
        {
            case "ini" when entry.Name.Contains("BinaryConfig", StringComparison.OrdinalIgnoreCase):
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.BinaryConfig.FConfigCacheIni(archive), Formatting.Indented), [], []);
            case "ustbin" when Profile!.Version == EGame.GAME_DeltaForce:
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.GameTypes.DFHO.Assets.Objects.FDeltaStringTable(archive), Formatting.Indented), [], []);
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.Assets.Exports.Material.FGlobalShaderCache(archive), Formatting.Indented), [], []);
            case "bin" when entry.Name.Contains("GameFeatureVersePaths", StringComparison.OrdinalIgnoreCase):
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.GameFeatures.FGameFeatureVersePathLookup(archive), Formatting.Indented), [], []);
            case "ushaderbytecode" or "ushadercode":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.Shaders.FShaderCodeArchive(archive), Formatting.Indented), [], []);
            case "upipelinecache":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.Shaders.FPipelineCacheFile(archive), Formatting.Indented), [], []);
            case "stinfo":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.Shaders.FShaderTypeHashes(archive), Formatting.Indented), [], []);
            case "udic":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new CUE4Parse.UE4.Oodle.Objects.FOodleDictionaryArchive(archive).Header, Formatting.Indented), [], []);
            case "locres":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new FTextLocalizationResource(archive), Formatting.Indented), [], []);
            case "locmeta":
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new FTextLocalizationMetaDataResource(archive), Formatting.Indented), [], []);
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
                using (var archive = entry.File.CreateReader()) return new(JsonConvert.SerializeObject(new FAssetRegistryState(archive), Formatting.Indented), [], []);
        }
        var raw = provider.SaveAsset(entry.File);
        if (entry.Type.Equals("json", StringComparison.OrdinalIgnoreCase) || (entry.Type == "ebd" && Profile!.Version == EGame.GAME_ArcRaiders))
        {
            using var text = new StreamReader(new MemoryStream(raw));
            return new(Newtonsoft.Json.Linq.JToken.Parse(text.ReadToEnd()).ToString(Formatting.Indented), [], []);
        }
        if (entry.Type.Equals("json", StringComparison.OrdinalIgnoreCase))
            return new(JsonConvert.SerializeObject(JsonConvert.DeserializeObject(Encoding.UTF8.GetString(raw)), Formatting.Indented), [], []);
        if (entry.Type.ToLowerInvariant() is "lua" or "luac")
            return new(settings.DecompileLua ? MacLua.Read(raw, settings.UnluacFlags, Profile!.UnluacOpCodeMap, token) : Encoding.UTF8.GetString(raw), [], []);
        if (entry.Type.Equals("svg", StringComparison.OrdinalIgnoreCase)) return new("", [], [SvgPreview(entry.Name, raw)]);
        if (new[] { "png", "jpg", "jpeg", "bmp", "webp", "gif" }.Contains(entry.File.Extension.ToLowerInvariant()))
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(raw) ?? throw new InvalidDataException("The image could not be decoded.");
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return new($"{entry.Path}\n{raw.Length:N0} bytes", [], [new(entry.Name, png.ToArray(), bitmap.Width, bitmap.Height)]);
        }
        if (entry.Actions.HasFlag(EBulkType.Audio))
            return new($"{entry.Path}\n{raw.Length:N0} bytes", [], [], raw, entry.File.Extension);
        if (raw.AsSpan(0, Math.Min(raw.Length, 4096)).Contains((byte)0))
            return new(HexDump(raw), [], []);
        return new(Encoding.UTF8.GetString(raw), [], []);
    }

    public int Queue(AssetEntry entry, string mode, CancellationToken token, ExportSession? destination = null, ExportSession? imageDestination = null)
    {
        var session = destination ?? Exports;
        token.ThrowIfCancellationRequested();
        if (mode == "Properties" && entry.File.IsUePackage)
        {
            var exports = Provider!.LoadPackage(entry.File).GetExports().ToArray();
            token.ThrowIfCancellationRequested();
            session.Add(new PackagePropertiesExporter(entry.File, JsonConvert.SerializeObject(exports, Formatting.Indented), _settings!.KeepDirectoryStructure));
            return 1;
        }
        if (mode == "Code")
        {
            if (!entry.File.IsUePackage) return 0;
            var package = Provider!.LoadPackage(entry.File);
            if (!package.GetExports().OfType<UClass>().Any()) return 0;
            session.Add(new BlueprintCodeExporter(entry.File, Decompile(entry, token), _settings!.KeepDirectoryStructure));
            return 1;
        }
        if (!entry.File.IsUePackage)
        {
            if (mode is "Audio" or "Auto" && entry.Type.ToLowerInvariant() is "bnk" or "pck" or "bank" or "fsb" or "awb" or "acb")
            {
                var sounds = PreviewAudioBank(entry, token)?.Sounds ?? [];
                if (sounds.Count == 0) return 0;
                session.Add(new ExtractedSoundExporter(entry.File, sounds, _settings!.ConvertAudioOnBulkExport, Profile!.CriwareDecryptionKey, _settings.KeepDirectoryStructure));
                return sounds.Count;
            }
            var capability = mode switch { "Texture" => EBulkType.Textures, "Audio" => EBulkType.Audio, _ => EBulkType.None };
            if (mode is not ("Raw" or "Auto") && (capability == EBulkType.None || !entry.Actions.HasFlag(capability))) return 0;
            if (mode == "Texture")
            {
                var images = Preview(entry, _settings!, token).Images;
                if (images.Count == 0) return 0;
                (imageDestination ?? session).Add(new CosmeticImageExporter(entry.File, images, _settings!.KeepDirectoryStructure));
                return 1;
            }
            if (mode is "Audio" or "Auto" && entry.Actions.HasFlag(EBulkType.Audio))
            {
                session.Add(new ExtractedSoundExporter(entry.File, [new PreviewSound(entry.File.PathWithoutExtension, entry.Type, Provider!.SaveAsset(entry.File))],
                    _settings!.ConvertAudioOnBulkExport, Profile!.CriwareDecryptionKey, _settings.KeepDirectoryStructure));
                return 1;
            }
            session.Add(new ArchiveRawExporter(entry.File, Provider!, _settings!.KeepDirectoryStructure));
            return 1;
        }
        if (mode == "Raw")
        {
            session.Add(new ArchiveRawExporter(entry.File, Provider!, _settings!.KeepDirectoryStructure));
            return 1;
        }
        var count = 0;
        foreach (var obj in Provider!.LoadPackage(entry.File).GetExports())
        {
            token.ThrowIfCancellationRequested();
            if (mode is "Audio" or "Auto")
            {
                var sounds = ExtractSounds(obj, token);
                if (sounds.Count > 0)
                {
                    session.Add(new ExtractedSoundExporter(obj, sounds, _settings!.ConvertAudioOnBulkExport, Profile!.CriwareDecryptionKey, _settings.KeepDirectoryStructure));
                    count++;
                    continue;
                }
                if (mode == "Audio") continue;
            }
            if (mode is "Auto" or "Texture")
            {
                var images = obj is CUE4Parse.GameTypes.KRD.Assets.Exports.USvgAsset or CUE4Parse.GameTypes.Dawnwalker.Assets.Exports.UBiomesMaskTextureData
                    ? PreviewImages(obj, _settings!, token)
                    : CreatorPlatform.Render(Provider, _settings!, _settings!.ExportOptions(Profile!.TexturePlatform), obj, token);
                if (images.Count > 0)
                {
                    (imageDestination ?? session).Add(new CosmeticImageExporter(obj, images, _settings!.KeepDirectoryStructure));
                    count++;
                    continue;
                }
            }
            var accepts = mode switch
            {
                "Embedded" => CUE4Parse_Conversion.Dto.EmbeddedFilesDto.Supports(obj),
                "Texture" => obj is UTexture or CUE4Parse.UE4.Assets.Exports.Paper2D.UPaperSprite or CUE4Parse.UE4.Assets.Exports.Paper2D.UPaperFlipbook or CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface,
                "Model" => obj is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh or CUE4Parse.GameTypes.Nascar.Assets.Exports.UIRMesh or CUE4Parse.UE4.Assets.Exports.Engine.USkinnedAsset
                    or CUE4Parse.UE4.Assets.Exports.Houdini.UHoudiniStaticMesh or CUE4Parse.UE4.Assets.Exports.GeometryCollection.UGeometryCollection or CUE4Parse.UE4.Assets.Exports.Animation.USkeleton,
                "World" => obj is CUE4Parse.UE4.Objects.Engine.UWorld or CUE4Parse.UE4.Objects.Engine.UBlueprintGeneratedClass or CUE4Parse.UE4.Assets.Exports.Paper2D.UPaperTileMap,
                "Animation" => obj is CUE4Parse.UE4.Assets.Exports.Animation.UAnimationAsset,
                "Audio" => obj is USoundWave or USoundNodeWave,
                _ => true
            };
            if (!accepts) continue;
            try
            {
                if (mode == "Embedded") session.Add(new EmbeddedFilesExporter(obj));
                else if (mode == "Properties") session.Add(new JsonPropertiesExporter(obj));
                else if (obj is USoundWave or USoundNodeWave) session.Add(new AudioAssetExporter(obj));
                else session.Add(obj);
                count++;
            }
            catch (NotSupportedException)
            {
                if (mode == "Auto") { session.Add(new JsonPropertiesExporter(obj)); count++; }
            }
        }
        return count;
    }

    public string Metadata(AssetEntry entry) => JsonConvert.SerializeObject(Provider!.LoadPackage(entry.File), Formatting.Indented);

    public string Decompile(AssetEntry entry, CancellationToken token)
    {
        var package = Provider!.LoadPackage(entry.File);
        UClassCookedMetaData? metadata = null;
        try { metadata = package.GetExportOrNull<UClassCookedMetaData>("CookedClassMetaData"); } catch { }
        List<string> fragments = [];
        foreach (var blueprint in package.GetExports().OfType<UClass>())
        {
            token.ThrowIfCancellationRequested();
            fragments.Add(blueprint.DecompileBlueprintToPseudo(metadata));
        }
        if (fragments.Count == 0) throw new NotSupportedException("This package has no Blueprint class to decompile.");
        var text = string.Join("\n\n", fragments);
        if (entry.Path.Contains("_Verse.uasset")) text = Regex.Replace(text, "__verse_0x[a-fA-F0-9]{8}_", "");
        text = Regex.Replace(text, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");
        return Regex.Replace(text, @"K2Node_(?:DynamicCast_)?([A-Za-z0-9_]+)", "$1");
    }

    public void ConfigureQueuedExports(DesktopSettings settings, bool overrideDirectory = false)
    {
        foreach (var exporter in Exports.QueuedItems.OfType<ArchiveRawExporter>())
        {
            exporter.OutputDirectory = !overrideDirectory && settings.UseCustomOutputFolders ? settings.ExportDirectory("Raw") : null;
            exporter.KeepDirectories = settings.KeepDirectoryStructure;
        }
    }

    internal static (string, string) ExportFilePath(string root, string archivePath, bool keepDirectories)
    {
        var path = OutputPath(root, archivePath);
        if (!keepDirectories) path = OutputPath(root, Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return (Path.GetFileName(path), path);
    }

    public static string OutputPath(string root, string archivePath)
    {
        root = Path.GetFullPath(root);
        var normalized = archivePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) throw new IOException("An archive path must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The archive path escapes the output directory.");
        return path;
    }

    private static string HexDump(byte[] data)
    {
        var text = new StringBuilder();
        var count = Math.Min(data.Length, 1024 * 1024);
        for (var i = 0; i < count; i += 16)
        {
            var row = data.AsSpan(i, Math.Min(16, count - i));
            text.Append(i.ToString("X8")).Append("  ");
            foreach (var b in row) text.Append(b.ToString("X2")).Append(' ');
            text.Append(' ', (16 - row.Length) * 3).Append("  ");
            foreach (var b in row) text.Append(b is >= 32 and < 127 ? (char)b : '.');
            text.AppendLine();
        }
        if (count < data.Length) text.AppendLine($"Preview limited to {count:N0} of {data.Length:N0} bytes. Export raw data to read the full file.");
        return text.ToString();
    }

    public void Dispose() { Provider?.Dispose(); _liveContext?.Dispose(); }
}

public sealed class AssetEntry(GameFile file) : System.ComponentModel.INotifyPropertyChanged
{
    public EAssetCategory Category { get; set; } = AssetClassification.Extension(file.Extension).Category;
    public EBulkType Actions { get; set; } = AssetClassification.Extension(file.Extension).Actions;
    public GameFile File { get; } = file;
    private string? _resolvedAssetType;
    public string? ResolvedAssetType
    {
        get => _resolvedAssetType;
        set
        {
            if (_resolvedAssetType == value) return;
            _resolvedAssetType = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayType)));
        }
    }
    public string DisplayType => ResolvedAssetType ?? Type;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Name => File.Name;
    public string Path => File.Path;
    public string Type => File.Extension;
    public string Size => File.Size < 1024 ? $"{File.Size} B" : $"{File.Size / 1024.0:N1} KiB";
    public override string ToString() => Name;
}

public sealed class FolderEntry(string name, string path, FolderEntry? parent = null)
{
    public string SearchText { get; set; } = "";
    public EAssetCategory SelectedCategory { get; set; } = EAssetCategory.All;
    public FolderEntry? Parent { get; } = parent;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public ObservableCollection<FolderEntry> Children { get; } = [];
    public List<AssetEntry> Files { get; } = [];
    public IEnumerable<AssetEntry> DescendantFiles() => Files.Concat(Children.SelectMany(x => x.DescendantFiles()));
    public override string ToString() => Name;
}

public sealed record PreviewImage(string Name, byte[] Data, int Width, int Height, CTexture? Texture = null, IReadOnlyList<PreviewFrame>? Frames = null, byte[]? EncodedData = null, string EncodedFormat = "png");
public sealed record PreviewFrame(byte[] Data, TimeSpan Duration);
public sealed record PreviewText(string Name, string Text, string Syntax);
public sealed record PreviewResult(string Text, UObject[] Exports, List<PreviewImage> Images, byte[]? Audio = null, string? AudioFormat = null, List<PreviewSound>? Sounds = null, string? Syntax = null, string? TitleExtra = null, List<PreviewText>? TextDocuments = null);
