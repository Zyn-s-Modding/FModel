"""Use FModel's original cosmetic renderers with the macOS application services."""
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1])
destination = pathlib.Path(sys.argv[2])
for path in (source / "Creator").rglob("*.cs"):
    text = path.read_text()
    text = re.sub(r"^using (System.Windows|FModel.Services|FModel.Settings|FModel.ViewModels);\n", "", text, flags=re.M)
    text = text.replace("    private static ApplicationViewModel _applicationView => ApplicationService.ApplicationView;\n", "")
    text = text.replace("    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;\n", "")
    text = text.replace("_applicationView.CUE4Parse.Provider", "CreatorPlatform.Provider")
    text = text.replace("public static Typefaces Typefaces;", "public static Typefaces Typefaces => CreatorPlatform.Typefaces;")
    text = text.replace("UserSettings.GetExportOptions()", "CreatorPlatform.Options")
    text = text.replace("UserSettings.Default.AssetLanguage", "CreatorPlatform.Language")
    text = text.replace("UserSettings.Default.CosmeticDisplayAsset", "CreatorPlatform.DisplayAssets")
    text = re.sub(r'Application.GetResourceStream\(new Uri\("pack://application:,,,/Resources/([^"\n]+)"\)\)\?\.Stream', r'CreatorPlatform.OpenResource("\1")', text)
    text = text.replace("Application.GetResourceStream(_BURBANK_BIG_CONDENSED_BOLD)?.Stream", "CreatorPlatform.OpenResource(_BURBANK_BIG_CONDENSED_BOLD)")
    text = text.replace('private readonly Uri _BURBANK_BIG_CONDENSED_BOLD = new("pack://application:,,,/Resources/BurbankBigCondensed-Bold.ttf");', 'private const string _BURBANK_BIG_CONDENSED_BOLD = "BurbankBigCondensed-Bold.ttf";')
    text = text.replace("private readonly CUE4ParseViewModel _viewModel;", "private readonly CUE4Parse.FileProvider.IFileProvider _provider;")
    text = text.replace("Typefaces(CUE4ParseViewModel viewModel)", "Typefaces(CUE4Parse.FileProvider.IFileProvider provider)")
    text = text.replace("_viewModel = viewModel;", "_provider = provider;")
    text = text.replace("_viewModel.Provider", "_provider").replace("viewModel.Provider", "_provider")
    text = text.replace("_apiEndpointView.FortniteApi.GetPlaylist", "CreatorPlatform.GetPlaylist")
    text = text.replace("_apiEndpointView.FortniteApi.TryGetBytes", "CreatorPlatform.TryGetBytes")
    text = text.replace("ApplicationService.ApiEndpointView.FModelApi.GetDesign", "CreatorPlatform.GetDesign")
    target = destination / path.relative_to(source / "Creator")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("#nullable disable\nusing FModel.Desktop;\n" + text)

# These shared source files use the Windows project's disabled nullable context.
for name in ["Enums.cs", "Extensions/EnumExtensions.cs", "Extensions/AssetCategoryExtensions.cs",
             "Framework/CustomSKShaper.cs", "ViewModels/ApiEndpoints/Models/PlaylistResponse.cs",
             "ViewModels/ApiEndpoints/Models/FModelResponse.cs", "Extensions/StringExtensions.cs",
             "Extensions/Themes/JsonHighlightThemes.cs"]:
    target = destination / "Shared" / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("#nullable disable\n" + (source / name).read_text().replace("ICSharpCode.AvalonEdit", "AvaloniaEdit"))

color_source = (source / "Extensions/AvalonExtensions.cs").read_text()
color_record = color_source[color_source.index("public sealed record HighlightColor"):color_source.index("public static class AvalonExtensions")]
(destination / "Shared/HighlightColor.cs").write_text("namespace FModel.Extensions;\n" + color_record)
theme_source = (source / "Extensions/Themes/HighlightingThemeExtensions.cs").read_text()
theme_source = theme_source.replace("using System.Windows;\n", "").replace("System.Windows.Media", "Avalonia.Media")
theme_source = theme_source.replace("ICSharpCode.AvalonEdit", "AvaloniaEdit").replace("using static FModel.Extensions.AvalonExtensions;\n", "")
theme_source = theme_source.replace("(Color) ColorConverter.ConvertFromString(color.Foreground)", "Color.Parse(color.Foreground)")
theme_source = theme_source.replace("FontWeights.Bold", "FontWeight.Bold").replace("FontStyles.Italic", "FontStyle.Italic")
(destination / "Shared/HighlightingThemeExtensions.cs").write_text("#nullable disable\n" + theme_source)

discord = (source / "Services/DiscordService.cs").read_text()
for namespace in ["FModel.Extensions", "FModel.Settings", "FModel.ViewModels"]:
    discord = discord.replace(f"using {namespace};\n", "")
start = discord.index("        public void UpdatePresence(CUE4ParseViewModel viewModel)")
end = discord.index("        public void UpdatePresence(string details, string state)", start)
discord = discord[:start] + discord[end:]
constants = (source / "Constants.cs").read_text()
for name in ["DISCORD_LINK", "DONATE_LINK"]:
    value = re.search(r"public const string " + name + r' = ("[^"]+");', constants)[1]
    discord = discord.replace("Constants." + name, value)
discord = discord.replace("Constants.APP_VERSION", "typeof(DiscordHandler).Assembly.GetName().Version")
discord = discord.replace("Constants.APP_SHORT_COMMIT_ID", '"macOS"')
discord = discord.replace("        public void Initialize(string gameName)", "        public bool IsInitialized => _client.IsInitialized;\n\n        public void Initialize(string gameName)")
(destination / "Shared/DiscordService.cs").write_text("#nullable disable\n" + discord)

loading = (source / "Extensions/CUE4ParseExtensions.cs").read_text()
loading = loading.replace("using FModel.Settings;\n", "")
loading = loading.replace("GameFile file, string objectName = null)", "GameFile file, string objectName = null, bool previewWorlds = true)")
loading = loading.replace("UserSettings.Default.PreviewWorlds", "previewWorlds")
(destination / "Shared/CUE4ParseExtensions.cs").write_text("#nullable disable\n" + loading)
