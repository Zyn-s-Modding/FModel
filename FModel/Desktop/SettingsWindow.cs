using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Lua.unluac;

namespace FModel.Desktop;

internal sealed class SettingsWindow : Window
{
    private readonly DesktopSettings _edited;
    private readonly HashSet<string> _fields = [];
    private readonly Dictionary<string, TextBox> _hotkeys = [];
    private readonly CUE4Parse.FileProvider.AbstractFileProvider? _provider;

    public SettingsWindow(DesktopSettings settings, GameProfile? profile = null, CUE4Parse.FileProvider.AbstractFileProvider? provider = null)
    {
        _provider = provider;
        Title = "Settings"; Width = 820; Height = 720; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _edited = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings, DesktopSettings.JsonOptions), DesktopSettings.JsonOptions)!;
        var editedProfile = profile is null ? null : JsonSerializer.Deserialize<GameProfile>(JsonSerializer.Serialize(profile, DesktopSettings.JsonOptions), DesktopSettings.JsonOptions)!;
        TextBox? criwareKeyInput = null;
        var tabs = new TabControl { Name = "SettingsPages" };
        var general = Page(tabs, "General");
        var output = Field(general, nameof(DesktopSettings.OutputDirectory), "Output Directory");
        general.Children.Add(Ui.Button("Browse…", async () =>
        {
            var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output Directory" });
            if (chosen.FirstOrDefault()?.TryGetLocalPath() is { } path) ((TextBox)output).Text = path;
        }));
        var customize = (CheckBox)Field(general, nameof(DesktopSettings.UseCustomOutputFolders), "Customize Output Folders");
        var directories = new StackPanel { IsVisible = _edited.UseCustomOutputFolders };
        foreach (var (name, label) in new[] { (nameof(DesktopSettings.RawDataDirectory), "Save Raw Data Directory"),
            (nameof(DesktopSettings.PropertiesDirectory), "Save Properties Directory"), (nameof(DesktopSettings.TextureDirectory), "Save Texture Directory"),
            (nameof(DesktopSettings.AudioDirectory), "Save Audio Directory") })
        {
            var path = (TextBox)Field(directories, name, label);
            path.Watermark = _edited.OutputDirectory;
            ((TextBox)output).TextChanged += (_, _) => path.Watermark = ((TextBox)output).Text;
            var row = (Grid)path.Parent!;
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var browse = Ui.Button("…", async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = label });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } chosen) path.Text = chosen;
            });
            Grid.SetColumn(browse, 2); row.Children.Add(browse);
        }
        general.Children.Add(directories);
        customize.IsCheckedChanged += (_, _) => directories.IsVisible = customize.IsChecked == true;
        Field(general, nameof(DesktopSettings.KeepDirectoryStructure), "Keep Directory Structure");
        Field(general, nameof(DesktopSettings.Language), "Packages Language (reload archives)");
        Field(general, nameof(DesktopSettings.DiscordRpc), "Discord Rich Presence");
        Field(general, nameof(DesktopSettings.AesReload), "Reload AES Keys");
        Field(general, nameof(DesktopSettings.CompressedAudioMode), "Compressed Audio Mode");
        Field(general, nameof(DesktopSettings.ConvertAudioOnBulkExport), "Convert Audio on Bulk Export");
        var mappings = new TextBox { Text = profile?.MappingsPath ?? "", IsEnabled = profile is not null };
        var localMappings = new CheckBox { IsChecked = profile?.UseRemoteMappings != true };
        if (profile is not null)
        {
            Ui.Field(general, "Use Local Mapping File", localMappings);
            Ui.Field(general, "Local Mapping File", mappings);
            general.Children.Add(Ui.Button("…", async () =>
            {
                var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Local Mapping File", FileTypeFilter = [new FilePickerFileType("Unreal mappings") { Patterns = ["*.usmap"] }] });
                if (chosen.FirstOrDefault()?.TryGetLocalPath() is { } path) { mappings.Text = path; localMappings.IsChecked = true; }
            }));
        }
        Field(general, nameof(DesktopSettings.ReadScriptData), "Serialize Script Bytecode (reload archives)");
        Field(general, nameof(DesktopSettings.ReadShaderMaps), "Serialize Inlined Shader Maps (reload archives)");
        Field(general, nameof(DesktopSettings.ShowDecompileOption), "Show Decompile Option");
        var decompileLua = (CheckBox)Field(general, nameof(DesktopSettings.DecompileLua), "Decompile Lua");
        Field(general, nameof(DesktopSettings.PreviewExplorerTextures), "Preview Textures in Asset Explorer");
        Field(general, nameof(DesktopSettings.ExplorerGrid), "Use Grid Asset Explorer");
        if (editedProfile is not null)
        {
            var criwareKey = new TextBox { Text = "0x" + editedProfile.CriwareDecryptionKey.ToString("X16") };
            criwareKeyInput = criwareKey;
            Ui.Field(general, "Criware Decryption Key", criwareKey);
            criwareKey.LostFocus += (_, _) =>
            {
                var value = criwareKey.Text?.Trim() ?? "";
                var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (ulong.TryParse(hex ? value[2..] : value, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var key))
                { editedProfile.CriwareDecryptionKey = key; criwareKey.ClearValue(ToolTip.TipProperty); }
                else ToolTip.SetTip(criwareKey, "Enter a 64-bit decimal key or a hexadecimal key beginning with 0x.");
            };
            var version = new ComboBox { ItemsSource = Enum.GetValues<EGame>(), SelectedItem = editedProfile.Version, HorizontalAlignment = HorizontalAlignment.Stretch };
            version.SelectionChanged += (_, _) => { if (version.SelectedItem is EGame game) editedProfile.Version = game; };
            Ui.Field(general, "UE Versions", version);
            general.Children.Add(Ui.Row(Ui.Button("Custom Versions", async () =>
            {
                var versions = editedProfile.CustomVersions.Select(x => new FCustomVersion(new FGuid(x.Key), x.Value)).ToList();
                var editor = new DictionaryEditorWindow<List<FCustomVersion>>("Custom Versions", versions, [new FCustomVersion(new FGuid(), 0)], values =>
                {
                    if (values.Select(x => x.Key.ToString()).Distinct().Count() != values.Count) throw new ArgumentException("Each version GUID must be unique.");
                });
                if (await editor.ShowDialog<bool>(this)) editedProfile.CustomVersions = editor.Value!.ToDictionary(x => x.Key.ToString(), x => x.Version);
            }), Ui.Button("Options", async () =>
            {
                var editor = new DictionaryEditorWindow<Dictionary<string, bool>>("Options", editedProfile.VersionOptions, []);
                if (await editor.ShowDialog<bool>(this)) editedProfile.VersionOptions = editor.Value!;
            }), Ui.Button("MapStructTypes", async () =>
            {
                var editor = new DictionaryEditorWindow<Dictionary<string, KeyValuePair<string, string>>>("MapStructTypes", editedProfile.MapStructTypes, []);
                if (await editor.ShowDialog<bool>(this)) editedProfile.MapStructTypes = editor.Value!;
            })));
            general.Children.Add(Ui.Row(new TextBlock { Text = "Endpoints", VerticalAlignment = VerticalAlignment.Center }, Ui.Button("AES", async () =>
            {
                var endpoint = editedProfile.AesEndpoint.IsConfigured ? editedProfile.AesEndpoint : RemoteEndpoint.Default(editedProfile.Name, false);
                var editor = new EndpointEditorWindow(endpoint, false);
                if (await editor.ShowDialog<bool>(this)) editedProfile.AesEndpoint = editor.Value!;
            }), Ui.Button("Mapping", async () =>
            {
                var endpoint = editedProfile.MappingEndpoint.IsConfigured ? editedProfile.MappingEndpoint : RemoteEndpoint.Default(editedProfile.Name, true);
                var editor = new EndpointEditorWindow(endpoint, true);
                if (await editor.ShowDialog<bool>(this)) editedProfile.MappingEndpoint = editor.Value!;
            })));
        }

        var preview = Page(tabs, "Preview");
        Field(preview, nameof(DesktopSettings.PreviewMaxTextureSize), "Preview Max Texture Size", 16, 16384);
        Field(preview, nameof(DesktopSettings.PreviewStaticMeshes), "Preview Static Meshes");
        Field(preview, nameof(DesktopSettings.PreviewSkeletalMeshes), "Preview Skeletal Meshes");
        Field(preview, nameof(DesktopSettings.PreviewAnimations), "Preview Animations");
        Field(preview, nameof(DesktopSettings.PreviewMaterials), "Preview Materials");
        Field(preview, nameof(DesktopSettings.PreviewWorlds), "Preview Worlds (.umap)");
        Field(preview, nameof(DesktopSettings.SaveSkeletonAsMesh), "Handle Skeletons as Empty Meshes");
        var creator = Page(tabs, "Creator");
        Field(creator, nameof(DesktopSettings.CosmeticStyle), "Cosmetic Icon Style");
        Field(creator, nameof(DesktopSettings.CosmeticDisplayAsset), "Cosmetic Display Assets");

        var exports = Page(tabs, "Export");
        Field(exports, nameof(DesktopSettings.ExportImmediately), "Export Immediately");
        Field(exports, nameof(DesktopSettings.MeshFormat), "Mesh Format");
        Field(exports, nameof(DesktopSettings.MeshQuality), "Mesh Quality");
        Field(exports, nameof(DesktopSettings.SocketFormat), "Socket Format");
        Field(exports, nameof(DesktopSettings.CompressionFormat), "Compression Format");
        Field(exports, nameof(DesktopSettings.NaniteFormat), "Nanite Format");
        Field(exports, nameof(DesktopSettings.IncludeDisabledMeshSections), "Include Disabled Sections");
        Field(exports, nameof(DesktopSettings.TextureFormat), "Texture Format");
        Field(exports, nameof(DesktopSettings.AnimatedTextureFormat), "Animated Texture Format");
        Field(exports, nameof(DesktopSettings.TextureQuality), "Texture Quality", 0, 100);
        Field(exports, nameof(DesktopSettings.PngCompressionLevel), "PNG Compression", 0, 9);
        Field(exports, nameof(DesktopSettings.TgaRleCompression), "TGA RLE Compression");
        Field(exports, nameof(DesktopSettings.ExportHdrTexturesAsHdr), "Save HDR as .hdr");
        Field(exports, nameof(DesktopSettings.ExportAllTextureMips), "Export All Mips");
        Field(exports, nameof(DesktopSettings.MaterialDepth), "Material Depth");
        Field(exports, nameof(DesktopSettings.ExportMaterials), "Auto-Export Attached Materials");
        Field(exports, nameof(DesktopSettings.ExportMorphTargets), "Export Morph Targets");

        var keys = Page(tabs, "Hotkeys");
        keys.Children.Add(new TextBlock { Text = "Use Meta for the Command key. Leave a shortcut empty to disable it.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 14) });
        foreach (var (command, gesture) in _edited.Hotkeys)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(4) };
            row.Children.Add(new TextBlock { Text = command, VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { Text = gesture };
            _hotkeys.Add(command, input); Grid.SetColumn(input, 1); row.Children.Add(input); keys.Children.Add(row);
        }
        var themes = Page(tabs, "Themes");
        Field(themes, nameof(DesktopSettings.JsonHighlightTheme), "JSON Highlight Theme");
        var lua = Page(tabs, "Unluac");
        var mode = new ComboBox { ItemsSource = Enum.GetValues<EUnluacMode>(), SelectedItem = _edited.UnluacFlags.HasFlag(EUnluacFlags.Disassemble) ? EUnluacMode.Disassemble : EUnluacMode.Decompile };
        Ui.Field(lua, "Mode", mode);
        mode.SelectionChanged += (_, _) =>
        {
            _edited.UnluacFlags &= ~(EUnluacFlags.Decompile | EUnluacFlags.Disassemble);
            _edited.UnluacFlags |= mode.SelectedItem is EUnluacMode.Disassemble ? EUnluacFlags.Disassemble : EUnluacFlags.Decompile;
        };
        foreach (var (flag, label) in new[] { (EUnluacFlags.RawString, "Raw Strings"), (EUnluacFlags.Luaj, "LuaJ"),
            (EUnluacFlags.NoDebug, "No Debug"), (EUnluacFlags.OpCodeMapPatch, "Opcode Map Patch") })
        {
            var checkbox = new CheckBox { Content = label, IsChecked = _edited.UnluacFlags.HasFlag(flag) };
            checkbox.IsCheckedChanged += (_, _) => _edited.UnluacFlags = checkbox.IsChecked == true ? _edited.UnluacFlags | flag : _edited.UnluacFlags & ~flag;
            lua.Children.Add(checkbox);
        }
        _fields.Add(nameof(DesktopSettings.UnluacFlags));
        TextBox? opcodeMap = null;
        if (editedProfile is not null)
        {
            opcodeMap = new TextBox { Text = editedProfile.UnluacOpCodeMap };
            Ui.Field(lua, "Opcode Map", opcodeMap);
            lua.Children.Add(Ui.Button("…", async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Opcode Map" });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) opcodeMap.Text = path;
            }));
        }
        var pages = tabs.Items.OfType<TabItem>().ToArray();
        tabs.Items.Clear();
        foreach (var name in new[] { "General", "Creator", "Export", "Preview", "Hotkeys", "Unluac", "Themes" })
            tabs.Items.Add(pages.Single(page => Equals(page.Header, name)));
        tabs.SelectedIndex = 0;
        var luaTab = tabs.Items.OfType<TabItem>().Single(page => Equals(page.Header, "Unluac"));
        luaTab.IsVisible = _edited.DecompileLua;
        decompileLua.IsCheckedChanged += (_, _) => luaTab.IsVisible = decompileLua.IsChecked == true;
        var panel = new DockPanel();
        var footer = Ui.Row(Ui.Button("OK", () =>
        {
            if (editedProfile is not null && criwareKeyInput is not null)
            {
                var value = criwareKeyInput.Text?.Trim() ?? "";
                var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (!ulong.TryParse(hex ? value[2..] : value, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var key)) throw new ArgumentException("Enter a valid 64-bit Criware decryption key.");
                editedProfile.CriwareDecryptionKey = key;
            }
            if (string.IsNullOrWhiteSpace(_edited.OutputDirectory)) throw new ArgumentException("Choose an output directory.");
            var directory = _edited.OutputDirectory;
            if (directory == "~" || directory.StartsWith("~/")) directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), directory.Length > 2 ? directory[2..] : "");
            _edited.OutputDirectory = Path.GetFullPath(directory);
            foreach (var mode in new[] { "Raw", "Properties", "Texture", "Audio" }) _ = _edited.ExportDirectory(mode);
            var gestures = new HashSet<(Key, KeyModifiers)>();
            foreach (var (command, input) in _hotkeys)
            {
                var value = input.Text?.Trim() ?? "";
                if (value.Length > 0)
                {
                    var parsed = KeyGesture.Parse(value);
                    if (!gestures.Add((parsed.Key, parsed.KeyModifiers))) throw new ArgumentException("Two actions use the same shortcut: " + value);
                }
                _edited.Hotkeys[command] = value;
            }
            foreach (var name in _fields) Property(name).SetValue(settings, Property(name).GetValue(_edited));
            settings.Hotkeys = _edited.Hotkeys;
            if (profile is not null && editedProfile is not null)
            {
                profile.MappingsPath = mappings.Text?.Trim() ?? "";
                profile.UseRemoteMappings = localMappings.IsChecked != true;
                profile.Version = editedProfile.Version;
                profile.CustomVersions = editedProfile.CustomVersions;
                profile.VersionOptions = editedProfile.VersionOptions;
                profile.MapStructTypes = editedProfile.MapStructTypes;
                profile.CriwareDecryptionKey = editedProfile.CriwareDecryptionKey;
                profile.AesEndpoint = editedProfile.AesEndpoint;
                profile.MappingEndpoint = editedProfile.MappingEndpoint;
                profile.UnluacOpCodeMap = opcodeMap?.Text?.Trim() ?? "";
            }
            Close(true); return Task.CompletedTask;
        }), Ui.Button("Cancel", () => { Close(false); return Task.CompletedTask; }));
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(tabs); Content = panel;
    }

    private static PropertyInfo Property(string name) => typeof(DesktopSettings).GetProperty(name)!;

    private Control Field(Panel panel, string name, string label, int min = 0, int max = 16384)
    {
        _fields.Add(name);
        return EditField(panel, _edited, name, label, min, max, _provider);
    }

    internal static Control EditField(Panel panel, DesktopSettings settings, string name, string label, int min = 0, int max = 16384, CUE4Parse.FileProvider.AbstractFileProvider? provider = null)
    {
        var property = Property(name);
        var value = property.GetValue(settings);
        Control control;
        if (name == nameof(DesktopSettings.Language) && provider is not null)
        {
            var languages = Enum.GetValues<ELanguage>();
            var language = languages.FirstOrDefault(x => provider.GetLanguageCode(x) == settings.Language);
            var combo = new ComboBox { ItemsSource = languages, SelectedItem = language, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ELanguage selected) settings.Language = provider.GetLanguageCode(selected); };
            control = combo;
        }
        else if (property.PropertyType == typeof(bool))
        {
            var checkbox = new CheckBox { IsChecked = (bool)value! };
            checkbox.IsCheckedChanged += (_, _) => property.SetValue(settings, checkbox.IsChecked == true);
            control = checkbox;
        }
        else if (property.PropertyType.IsEnum)
        {
            var combo = new ComboBox { ItemsSource = Enum.GetValues(property.PropertyType), SelectedItem = value, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is not null) property.SetValue(settings, combo.SelectedItem); };
            control = combo;
        }
        else if (property.PropertyType == typeof(int))
        {
            var number = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp((int)value!, min, max), FormatString = "0" };
            number.ValueChanged += (_, _) => property.SetValue(settings, checked((int)(number.Value ?? min)));
            control = number;
        }
        else
        {
            var text = new TextBox { Text = value?.ToString() };
            text.TextChanged += (_, _) => property.SetValue(settings, text.Text ?? "");
            control = text;
        }
        control.Tag = name;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), Margin = new Thickness(4) };
        row.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
        return control;
    }

    private static StackPanel Page(TabControl tabs, string title)
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 3 };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel } });
        return panel;
    }
}
