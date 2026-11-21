using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace FModel.Desktop;

public sealed class DesktopSettings
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("FMODEL_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel");
    public static string SettingsPath => Path.Combine(DataDirectory, "desktop-settings.json");
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public List<GameProfile> Games { get; set; } = [];
    public string? LastDirectory { get; set; }
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FModel", "Output");
    public bool UseCustomOutputFolders { get; set; }
    public string RawDataDirectory { get; set; } = "";
    public string PropertiesDirectory { get; set; } = "";
    public string TextureDirectory { get; set; } = "";
    public string AudioDirectory { get; set; } = "";
    public bool KeepDirectoryStructure { get; set; } = true;
    public EMeshFormat MeshFormat { get; set; } = EMeshFormat.UEFormat;
    public ETextureFormat TextureFormat { get; set; } = ETextureFormat.Png;
    public EMeshQuality MeshQuality { get; set; } = EMeshQuality.Highest;
    public ENaniteMeshFormat NaniteFormat { get; set; } = ENaniteMeshFormat.NaniteOnly;
    public bool ExportMaterials { get; set; } = true;
    public bool ExportMorphTargets { get; set; } = true;
    public bool ExportAllTextureMips { get; set; }
    public ESocketFormat SocketFormat { get; set; } = ESocketFormat.Bone;
    public EFileCompressionFormat CompressionFormat { get; set; } = EFileCompressionFormat.None;
    public EAnimatedTextureFormat AnimatedTextureFormat { get; set; } = EAnimatedTextureFormat.Auto;
    public EMaterialDepth MaterialDepth { get; set; } = EMaterialDepth.TopLayerOnly;
    public int TextureQuality { get; set; } = 100;
    public int PngCompressionLevel { get; set; } = 3;
    public bool TgaRleCompression { get; set; } = true;
    public bool ExportHdrTexturesAsHdr { get; set; } = true;
    public bool IncludeDisabledMeshSections { get; set; }
    public bool ExportImmediately { get; set; }
    public int PreviewMaxTextureSize { get; set; } = 1024;
    public bool PreviewStaticMeshes { get; set; } = true;
    public bool PreviewSkeletalMeshes { get; set; } = true;
    public bool PreviewAnimations { get; set; } = true;
    public bool PreviewMaterials { get; set; } = true;
    public bool PreviewWorlds { get; set; } = true;
    public bool SaveSkeletonAsMesh { get; set; }
    public bool ReadScriptData { get; set; }
    public bool ReadShaderMaps { get; set; }
    public bool ShowDecompileOption { get; set; }
    public string Language { get; set; } = "en";
    public EDiscordRpc DiscordRpc { get; set; } = EDiscordRpc.Always;
    public EAesReload AesReload { get; set; } = EAesReload.Always;
    public bool ExplorerGrid { get; set; } = true;
    public bool FeaturePreviewNewAssetExplorer { get; set; } = true;
    public bool PreviewExplorerTextures { get; set; } = true;
    public EIconStyle CosmeticStyle { get; set; } = EIconStyle.Default;
    public bool CosmeticDisplayAsset { get; set; } = true;
    public int AudioPlayerVolume { get; set; } = 80;
    public string AudioDeviceId { get; set; } = "";
    public ECompressedAudio CompressedAudioMode { get; set; } = ECompressedAudio.PlayDecompressed;
    public bool ConvertAudioOnBulkExport { get; set; }
    public FModel.Extensions.Themes.EJsonHighlightTheme JsonHighlightTheme { get; set; }
    public bool DecompileLua { get; set; }
    public CUE4Parse.UE4.Lua.unluac.EUnluacFlags UnluacFlags { get; set; } = CUE4Parse.UE4.Lua.unluac.EUnluacFlags.Decompile;
    public string ArchiveLoadingMode { get; set; } = "All";
    public List<string> FavoriteFolders { get; set; } = [];
    public Dictionary<string, string> Hotkeys { get; set; } = new()
    {
        ["Select Directory"] = "Meta+O", ["Search Packages"] = "Meta+Shift+F", ["Find References"] = "Meta+Shift+R",
        ["Toggle Asset Explorer"] = "Z", ["Add Asset Tab"] = "Meta+T", ["Close Asset Tab"] = "Meta+W",
        ["Export Raw Data"] = "Meta+Shift+S", ["Export Properties"] = "Meta+Alt+S",
        ["Copy Package Path"] = "Meta+Shift+C", ["Export Session"] = "Meta+Shift+E", ["Reload Archives"] = "Meta+R",
        ["Previous Directory Tab"] = "A", ["Next Directory Tab"] = "D", ["Previous Asset Tab"] = "Q", ["Next Asset Tab"] = "E",
        ["Add Audio"] = "Meta+N", ["Remove Audio"] = "X", ["Play / Pause Audio"] = "K", ["Previous Audio"] = "J", ["Next Audio"] = "L",
        ["Export Embedded Files"] = "", ["Export Textures"] = "", ["Export Models"] = "", ["Export Worlds"] = "",
        ["Export Animations"] = "", ["Export Audio"] = "", ["Export Decompiled Blueprints"] = ""
    };
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    public string ExportDirectory(string mode)
    {
        var path = UseCustomOutputFolders ? mode switch
        {
            "Raw" => RawDataDirectory, "Properties" or "Code" => PropertiesDirectory,
            "Texture" => TextureDirectory, "Audio" => AudioDirectory, _ => OutputDirectory
        } : OutputDirectory;
        if (string.IsNullOrWhiteSpace(path)) path = OutputDirectory;
        if (path == "~" || path.StartsWith("~/")) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return Path.GetFullPath(path);
    }

    public ExportOptions ExportOptions(ETexturePlatform platform = ETexturePlatform.DesktopMobile) => new(
        meshFormat: MeshFormat, textureFormat: TextureFormat, meshQuality: MeshQuality,
        naniteMeshFormat: NaniteFormat, texturePlatform: platform, exportMaterials: ExportMaterials,
        exportMorphTargets: ExportMorphTargets, exportAllTextureMips: ExportAllTextureMips,
        socketFormat: SocketFormat, compressionFormat: CompressionFormat, animatedTextureFormat: AnimatedTextureFormat,
        materialDepth: MaterialDepth, textureQuality: TextureQuality, pngCompressionLevel: PngCompressionLevel,
        tgaRleCompression: TgaRleCompression, exportHdrTexturesAsHdr: ExportHdrTexturesAsHdr,
        includeDisabledMeshSections: IncludeDisabledMeshSections);

    public static DesktopSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        // A corrupt settings file is retained for diagnosis instead of being overwritten.
        try
        {
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new();
            settings.Hotkeys.Remove("Export Assets");
            foreach (var (action, gesture) in new DesktopSettings().Hotkeys) settings.Hotkeys.TryAdd(action, gesture);
            return settings;
        }
        catch (JsonException)
        {
            File.Copy(SettingsPath, SettingsPath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}", true);
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }
}

public sealed class GameProfile
{
    public string LiveGame { get; set; } = "";
    public string Name { get; set; } = "Custom Directory";
    public string Directory { get; set; } = "";
    public EGame Version { get; set; } = EGame.GAME_UE4_LATEST;
    public ETexturePlatform TexturePlatform { get; set; } = ETexturePlatform.DesktopMobile;
    public string MappingsPath { get; set; } = "";
    public Dictionary<string, string> AesKeys { get; set; } = [];
    public Dictionary<string, bool> VersionOptions { get; set; } = [];
    public Dictionary<string, int> CustomVersions { get; set; } = [];
    public Dictionary<string, KeyValuePair<string, string>> MapStructTypes { get; set; } = [];
    public ulong CriwareDecryptionKey { get; set; }
    public RemoteEndpoint AesEndpoint { get; set; } = new();
    public RemoteEndpoint MappingEndpoint { get; set; } = new();
    public bool UseRemoteMappings { get; set; }
    public DateTime LastAesReload { get; set; }
    public string UnluacOpCodeMap { get; set; } = "";
}
