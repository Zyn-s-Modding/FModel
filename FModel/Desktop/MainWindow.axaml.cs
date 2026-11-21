using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using Newtonsoft.Json;
using Serilog;
using System.Diagnostics;

namespace FModel.Desktop;

public partial class MainWindow : Window
{
    internal readonly DesktopSettings Settings = DesktopSettings.Load();
    internal readonly ArchiveWorkspace Workspace;
    private CancellationTokenSource? _operation;
    private FolderEntry? _folder;
    private readonly Dictionary<TabItem, List<IDisposable>> _documentResources = [];
    private readonly List<string> _messages = [];
    private AudioWindow? _audio;
    private ExportWindow? _exportWindow;
    private ImageMergerWindow? _merger;
    private SearchWindow? _searchWindow;
    private FModel.Services.DiscordHandler? _discord;
    private readonly DesktopSnooperHost _snooper;
    private bool _closing;

    public MainWindow()
    {
        Workspace = new ArchiveWorkspace(FilterStreamingLevels);
        CodeDocument.JsonTheme = Settings.JsonHighlightTheme;
        InitializeComponent();
        Workspace.Exports.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ExportSession.TotalQueued)) return;
            Dispatcher.UIThread.Post(() =>
            {
                ExportQueueButton.Content = $"Queued Items: {Workspace.Exports.TotalQueued:N0}";
                ExportQueueButton.IsVisible = Workspace.Exports.HasQueuedItems;
            });
        };
        _snooper = new DesktopSnooperHost(this, ShowExports);
        _thumbnails = new ThumbnailService(Workspace, Settings);
        InitializeExplorer();
        Width = Math.Clamp(Settings.WindowWidth, 900, 3840);
        Height = Math.Clamp(Settings.WindowHeight, 600, 2160);
        Workspace.Log = AppendLog;
        KeyDown += async (_, e) => await HandleShortcut(e);
        AddHandler(DragDrop.DropEvent, Dropped);
        Opened += async (_, _) =>
        {
            var args = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Args ?? [];
            if (args.Contains("--ui-smoke-test") || args.Contains("--ui-snooper-smoke-test")) return;
            UpdateDiscordPresence();
            if (args.FirstOrDefault() == "--profile" && args.ElementAtOrDefault(1) is { } profilePath)
            {
                try { await LoadProfile(GameProfiles.Load(profilePath)); }
                catch (Exception error) { await Ui.Message(this, "Game profile", error.Message); }
                return;
            }
            var directory = args.FirstOrDefault(x => Directory.Exists(x));
            if (directory is not null)
            {
                var detected = GameDirectoryResolver.Detect(directory);
                var profile = Settings.Games.FirstOrDefault(x => x.Directory == detected.Directory) ?? detected;
                await LoadProfile(profile);
            }
            else if (Settings.Games.FirstOrDefault(x => x.Directory == Settings.LastDirectory && (x.LiveGame.Length > 0 || Directory.Exists(x.Directory))) is { } last)
                await LoadProfile(last);
            else await SelectGameAsync();
        };
        Closing += (_, e) =>
        {
            if (_closing) return;
            if (_operation is not null)
            {
                // Let in-flight native reads unwind before disposing the provider.
                e.Cancel = true;
                _closing = true;
                _operation.Cancel();
                return;
            }
            SaveAndDispose();
        };
        AppendLog("FModel — Unreal Engine Archives Explorer");
    }

    private void SaveAndDispose()
    {
        Settings.WindowWidth = Width;
        Settings.WindowHeight = Height;
        try { Settings.Save(); } catch (Exception ex) { Log.Error(ex, "Could not save settings"); }
        _audio?.Close();
        _exportWindow?.Close();
        _merger?.Close();
        _searchWindow?.Close();
        _discord?.Dispose();
        foreach (var window in _propertyWindows.Values.ToArray()) window.Close();
        _snooper.Dispose();
        _thumbnails.ResetAsync().GetAwaiter().GetResult();
        foreach (var tab in _documentResources.Keys.ToArray()) CloseDocument(tab);
        Workspace.Dispose();
    }

    internal void AppendLog(string message)
    {
        Log.Information("{Message}", message);
        Dispatcher.UIThread.Post(() =>
        {
            _messages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_messages.Count > 500) _messages.RemoveRange(0, _messages.Count - 500);
            LogView.Text = string.Join(Environment.NewLine, _messages);
            LogView.CaretIndex = LogView.Text.Length;
        });
    }

    internal async Task Run(string label, Func<CancellationToken, Task> work)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        MainContent.IsEnabled = MainMenu.IsEnabled = false;
        BusyProgress.IsVisible = CancelButton.IsVisible = true;
        Status.Text = label;
        var previousOperation = ArchiveWorkspace.CurrentOperation.Value;
        ArchiveWorkspace.CurrentOperation.Value = cancellation.Token;
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { AppendLog("Operation canceled."); }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation} failed", label);
            AppendLog($"{label}: {ex.Message}");
            if (!_closing) await Ui.Message(this, label, ex.Message);
        }
        finally
        {
            ArchiveWorkspace.CurrentOperation.Value = previousOperation;
            _operation = null;
            MainContent.IsEnabled = MainMenu.IsEnabled = true;
            BusyProgress.IsVisible = CancelButton.IsVisible = false;
            Status.Text = $"Ready · {Workspace.Files.Length:N0} files · {Workspace.Exports.TotalQueued:N0} queued exports";
            if (_closing) { SaveAndDispose(); Close(); }
        }
    }

    private async Task LoadProfile(GameProfile profile)
    {
        var previousDirectory = profile.Directory;
        await Run("Loading archives", async token =>
        {
            _searchWindow?.Close();
            foreach (var window in _propertyWindows.Values.ToArray()) window.Close();
            _snooper.Reset();
            await _thumbnails.ResetAsync();
            try { await Task.Run(() => Workspace.Open(profile, Settings, token), token); }
            finally { _thumbnails.Resume(); }
            foreach (var tab in Documents.Items.OfType<TabItem>().ToArray()) CloseDocument(tab);
            profile = Workspace.Profile!;
            var savedIndex = Settings.Games.FindIndex(game => game.LiveGame == profile.LiveGame &&
                (game.Directory == previousDirectory || game.Directory == profile.Directory));
            if (savedIndex < 0) Settings.Games.Add(profile);
            else Settings.Games[savedIndex] = profile;
            Settings.LastDirectory = profile.Directory;
            Settings.Save();
            Title = $"FModel — {profile.Name} ({profile.Version})";
            GameTitle.Text = $"{profile.Name} ({profile.Version})";
            PopulateArchives();
            ArchiveInfo.Text = $"{Workspace.Provider!.MountedVfs.Count:N0} loaded archives\n{Workspace.Files.Length:N0} packages and files\n{profile.Directory}";
            FolderTree.ItemsSource = Workspace.Folders;
            _folder = null;
            DisplayFiles([]);
            Breadcrumb.Text = "/";
            LeftTabs.SelectedIndex = 1;
        });
        UpdateDiscordPresence();
    }

    private void UpdateDiscordPresence()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime &&
            (lifetime.Args?.Contains("--ui-smoke-test") == true || lifetime.Args?.Contains("--ui-snooper-smoke-test") == true)) return;
        if (Settings.DiscordRpc == EDiscordRpc.Never)
        {
            _discord?.Dispose(); _discord = null;
            return;
        }
        try
        {
            _discord ??= new FModel.Services.DiscordHandler();
            if (!_discord.IsInitialized) _discord.Initialize(Workspace.Profile?.Name ?? "FModel");
            if (Workspace.Provider is { } provider)
                _discord.UpdatePresence($"{provider.GameDisplayName ?? provider.ProjectName} - {provider.MountedVfs.Count}/{provider.MountedVfs.Count + provider.UnloadedVfs.Count} Packages",
                    $"Mode: {Settings.ArchiveLoadingMode} - {Workspace.Files.Length:N0} Loaded Assets");
        }
        catch (Exception error) { Serilog.Log.Warning(error, "Discord Rich Presence is unavailable"); }
    }

    private async Task SelectGameAsync()
    {
        if (_operation is not null) return;
        var profile = await new DirectoryWindow(Settings).ShowDialog<GameProfile?>(this);
        if (profile is not null) await LoadProfile(profile);
    }

    private void DisplayFiles(IEnumerable<AssetEntry> files)
    {
        var items = files.ToArray();
        PackageList.ItemsSource = items;
        PackageCount.Text = items.Length.ToString("N0") + " Packages";
        PopulateExplorer(items);
    }

    private AssetEntry[] SelectedAssets()
    {
        if (_packageSelection) return PackageList.SelectedItems?.OfType<AssetEntry>().ToArray() ?? [];
        var selected = TileExplorer.IsVisible ? TileExplorer.SelectedEntries.OfType<AssetEntry>().ToArray()
            : Explorer.SelectedItems?.OfType<AssetEntry>().ToArray() ?? [];
        return selected.Length > 0 ? selected : PackageList.SelectedItems?.OfType<AssetEntry>().ToArray() ?? [];
    }

    private async Task OpenAsset(AssetEntry entry, bool addNewTab = false, string? objectName = null)
    {
        await Run($"Reading {entry.Name}", async token =>
        {
            var result = await Task.Run(() => Workspace.Preview(entry, Settings, token, objectName), token);
            if (result.Exports.Any(_snooper.TryDeliver)) return;
            ShowDocument(entry, result, addNewTab);
            var preview = result.Exports.FirstOrDefault(ShouldPreview);
            if (preview is not null)
            {
                var actor = await Task.Run(() => SnooperScene.Create(preview, Settings, Workspace.Profile!, token), token);
                if (actor.Components.Count > 0 || actor.Children.Count > 0) _snooper.Show(actor);
            }
        });
    }

    private void ShowDocument(AssetEntry entry, PreviewResult preview, bool addNewTab)
    {
        SetBrowserVisible(false);
        var selected = Documents.SelectedItem as TabItem;
        var reuse = !addNewTab || Documents.ItemCount >= 25 || (selected?.Tag is null && selected?.Content is CodeDocument { Editor.Text.Length: 0 });
        List<IDisposable> resources = [];
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions(preview.Images.Count > 0 ? "*,4,240" : "*") };
        var document = new CodeDocument(preview.Text, preview.Syntax ?? (entry.File.IsUePackage || preview.Text.TrimStart().StartsWith('{') || preview.Text.TrimStart().StartsWith('[') ? "Json" : entry.Type.ToLowerInvariant() switch
        { "json" or "ebd" => "Json", "ini" => "Ini", "xml" => "Xml", "lua" or "luac" => "Lua", "verse" => "Verse", "cpp" or "h" => "Cpp", _ => "Ini" }), OpenReference);
        resources.Add(document);
        content.Children.Add(document);
        content.Tag = document;
        var images = new List<Control>();
        foreach (var image in preview.Images)
        {
            try
            {
                var documentImage = new ImageDocument(image,
                    image.Texture is { } decoded ? () => _snooper.InspectTexture(decoded, image.Name, false) : null, compact: true);
                resources.Add(documentImage);
                images.Add(documentImage);
            }
            catch (Exception ex) { AppendLog($"Image preview: {ex.Message}"); }
        }
        if (images.Count > 0)
        {
            var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#262630") };
            Grid.SetColumn(splitter, 1); content.Children.Add(splitter);
            Control pane = images.Count == 1 ? images[0] : new TabControl { ItemsSource = images.Select((image, i) => new TabItem { Header = (i + 1).ToString(), Content = image }).ToArray() };
            Grid.SetColumn(pane, 2); content.Children.Add(pane);
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center });
        if (preview.TitleExtra is not null) header.Children.Add(new TextBlock { Text = preview.TitleExtra, Foreground = Avalonia.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        var tab = new TabItem { Header = header, Content = content, Tag = entry };
        ConfigureDocumentTab(tab, entry);
        var close = Ui.Button("×", () => { CloseDocument(tab); return Task.CompletedTask; }); close.Classes.Add("close"); header.Children.Add(close);
        _documentResources.Add(tab, resources);
        if (reuse && selected is not null)
        {
            Documents.Items.Insert(Documents.Items.IndexOf(selected), tab);
            CloseDocument(selected);
        }
        else Documents.Items.Add(tab);
        Documents.SelectedItem = tab;
        if (preview.Sounds is { Count: > 0 })
            foreach (var sound in preview.Sounds) _ = PlayAudio(sound.Path.TrimStart('/'), sound.Data, sound.Format);
        else if (preview.Audio is not null) _ = PlayAudio(entry.Path, preview.Audio, preview.AudioFormat ?? "wav");
        foreach (var text in preview.TextDocuments ?? []) ShowTextDocument(text.Name, text.Text, entry, text.Syntax);
    }

    private void CloseDocument(TabItem tab)
    {
        Documents.Items.Remove(tab);
        if (_documentResources.Remove(tab, out var resources)) foreach (var resource in resources) resource.Dispose();
        if (Documents.ItemCount == 0) SetBrowserVisible(true);
    }

    private void AddAssetTab(object? sender, RoutedEventArgs e)
    {
        if (Documents.ItemCount < 25) ShowTextDocument("New Tab", "");
    }

    private async Task Queue(IEnumerable<AssetEntry> assets, string mode)
    {
        var entries = assets.ToArray();
        if (entries.Length == 0) return;
        var direct = mode is "Properties" or "Code" or "Audio";
        await Run(direct ? "Exporting" : "Queuing exports", token => Task.Run(async () =>
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var immediate = direct || mode == "Texture" ? new ExportSession() : null;
                    Workspace.Queue(entry, mode, token, direct ? immediate : Workspace.Exports, mode == "Texture" ? immediate : null);
                    if (immediate?.HasQueuedItems == true)
                    {
                        var results = await immediate.RunAsync(Settings.ExportDirectory(mode), Settings.ExportOptions(Workspace.Profile!.TexturePlatform), ct: token);
                        foreach (var result in results)
                            AppendLog(result.Success ? $"Exported {result.ObjectPath}" : $"{result.ObjectPath}: {result.Error?.Message}");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { AppendLog($"{entry.Path}: {error.Message}"); }
            }
            if (!direct) AppendLog($"{Workspace.Exports.TotalQueued:N0} exports in queue.");
        }, token));
        if (Settings.ExportImmediately && Workspace.Exports.HasQueuedItems)
            await Run("Exporting", async token =>
            {
                Workspace.ConfigureQueuedExports(Settings);
                var results = await Task.Run(() => Workspace.Exports.RunAsync(Settings.OutputDirectory, Settings.ExportOptions(Workspace.Profile!.TexturePlatform), ct: token), token);
                foreach (var result in results)
                    AppendLog(result.Success ? $"Exported {result.ObjectPath}" : $"{result.ObjectPath}: {result.Error?.Message}");
            });
    }

    private async Task QueueFolder(string mode)
    {
        if (FolderTree.SelectedItem is FolderEntry folder) await Queue(folder.DescendantFiles(), mode);
    }

    private async Task ShowExports()
    {
        if (_exportWindow is { IsVisible: true } existing) { existing.Activate(); return; }
        var export = new ExportWindow(this);
        _exportWindow = export;
        try { await export.ShowDialog(this); }
        finally { _exportWindow = null; }
    }

    private SearchWindow GetSearchWindow()
    {
        if (_searchWindow is not null) return _searchWindow;
        var search = new SearchWindow(this, Workspace.Files);
        _searchWindow = search;
        search.Closed += (_, _) => _searchWindow = null;
        search.Show(this);
        return search;
    }

    private Task SearchAsync()
    {
        if (_operation is null) GetSearchWindow().FocusSearch();
        return Task.CompletedTask;
    }

    private async Task PlayAudio(string name, byte[] data, string format)
    {
        var window = GetAudioWindow();
        if (!window.IsVisible) window.Show(this);
        window.Activate();
        try { await window.LoadAsync(name, data, format); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog($"Audio: {ex.Message}"); if (window.IsVisible) await Ui.Message(window, "Audio Player", ex.Message); }
    }

    private async void SelectDirectory(object? sender, RoutedEventArgs e) => await SelectGameAsync();
    private async void Reload(object? sender, RoutedEventArgs e) { if (Workspace.Profile is { } profile) await LoadProfile(profile); }
    private async void OpenAes(object? sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (Workspace.Profile is null) { await SelectGameAsync(); return; }
        var profile = GameProfiles.Clone(Workspace.Profile);
        if (await new AesWindow(profile, Workspace.Provider).ShowDialog<bool>(this)) await LoadProfile(profile);
    }
    private async void ChooseMappings(object? sender, RoutedEventArgs e)
    {
        if (Workspace.Profile is not { } profile) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select mappings", FileTypeFilter = [new FilePickerFileType("Unreal mappings") { Patterns = ["*.usmap"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { profile.MappingsPath = path; profile.UseRemoteMappings = false; Settings.Save(); await LoadProfile(profile); }
    }
    private void FolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderEntry folder) return;
        DisplayFolder(folder);
    }
    private void DisplayFolder(FolderEntry folder)
    {
        _folder = folder;
        _changingFolder = true;
        try
        {
            PackageFilter.Text = ExplorerFilter.Text = folder.SearchText;
            AssetCategory.SelectedItem = folder.SelectedCategory;
        }
        finally { _changingFolder = false; }
        Breadcrumb.Text = "/" + folder.Path;
        FolderSummary.Text = $"'{folder.Name}' has {folder.Children.Count:N0} folders and {folder.Files.Count:N0} packages";
        UpdateFolderInformation(folder);
        SetBrowserVisible(NewExplorer.IsChecked == true);
        DisplayFiles(folder.Files.Where(x => x.Name.Contains(folder.SearchText, StringComparison.OrdinalIgnoreCase)));
    }
    private void FilterAssets(object? sender, TextChangedEventArgs e)
    {
        if (_changingFolder) return;
        var query = (sender as TextBox)?.Text ?? "";
        if (_folder is not null) _folder.SearchText = query;
        _changingFolder = true;
        try { PackageFilter.Text = ExplorerFilter.Text = query; }
        finally { _changingFolder = false; }
        DisplayFiles((_folder?.Files.AsEnumerable() ?? []).Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private void FilterFolders(object? sender, TextChangedEventArgs e)
    {
        var query = FolderFilter.Text ?? "";
        FolderTree.ItemsSource = query.Length == 0 ? Workspace.Folders : Flatten(Workspace.Folders).Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(500).ToArray();
    }
    private static IEnumerable<FolderEntry> Flatten(IEnumerable<FolderEntry> folders) => folders.SelectMany(x => new[] { x }.Concat(Flatten(x.Children)));
    private async void PackageOpened(object? sender, TappedEventArgs e) { if (PackageList.SelectedItem is AssetEntry asset) await OpenAsset(asset); }
    private async void ExplorerOpened(object? sender, TappedEventArgs e) => await OpenExplorerEntry(Explorer.SelectedItem);
    private void AssetSelected(object? sender, SelectionChangedEventArgs e)
    {
        _packageSelection = ReferenceEquals(sender, PackageList);
        UpdateAssetInformation((sender as ListBox)?.SelectedItem);
    }
    private async void ShowProperties(object? sender, RoutedEventArgs e) { if (SelectedAssets().FirstOrDefault() is { } asset) await OpenAsset(asset); }
    private async void QueueSelection(object? sender, RoutedEventArgs e) => await Queue(SelectedAssets(), "Auto");
    private async void QueueProperties(object? sender, RoutedEventArgs e) => await Queue(SelectedAssets(), "Properties");
    private async void QueueRaw(object? sender, RoutedEventArgs e) => await Queue(SelectedAssets(), "Raw");
    private async void CopyPath(object? sender, RoutedEventArgs e) { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(string.Join('\n', SelectedAssets().Select(x => x.Path))); }
    private async void ShowMetadata(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets().FirstOrDefault() is { } entry) await ShowAssetMetadata(entry);
    }
    private async void Decompile(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets().FirstOrDefault() is { } entry) await DecompileAsset(entry);
    }
    private async void FindReferences(object? sender, RoutedEventArgs e) => await FindReferencesAsync();
    private async Task FindReferencesAsync()
    {
        if (SelectedOrDocument().FirstOrDefault() is { } entry) await FindReferencesAsync(entry);
        else if (_operation is null) GetSearchWindow().FocusReferences();
    }
    private async Task FindReferencesAsync(AssetEntry entry)
    {
        if (_operation is not null) return;
        AssetEntry[] references = [];
        await Run("Scanning references", async token =>
        {
            references = await Task.Run(() => Workspace.Provider!.ScanForPackageRefs(entry.File).Select(x => new AssetEntry(x)).ToArray(), token);
            token.ThrowIfCancellationRequested();
            GetSearchWindow().ShowReferences(entry, references);
        });
    }
    private void ShowTextDocument(string title, string text, AssetEntry? entry = null, string? syntax = null)
    {
        SetBrowserVisible(false);
        var editor = new CodeDocument(text, syntax ?? (title.Contains("Decompiled") ? "Cpp" : "Json"), OpenReference);
        var tab = new TabItem { Content = editor, Tag = entry };
        ConfigureDocumentTab(tab, entry);
        tab.Header = Ui.Row(new TextBlock { Text = title }, Ui.Button("×", () => { CloseDocument(tab); return Task.CompletedTask; }));
        var selected = Documents.SelectedItem as TabItem;
        if (selected is not null && (Documents.ItemCount >= 25 || (selected.Tag is null && selected.Content is CodeDocument { Editor.Text.Length: 0 })))
        {
            Documents.Items.Insert(Documents.Items.IndexOf(selected), tab);
            CloseDocument(selected);
        }
        else Documents.Items.Add(tab);
        Documents.SelectedItem = tab;
        _documentResources[tab] = [editor];
    }

    private async Task OpenReference(string reference)
    {
        var provider = Workspace.Provider ?? throw new InvalidOperationException("No game directory is open.");
        var package = reference;
        var dot = package.LastIndexOf('.');
        var name = dot > package.LastIndexOf('/') ? package[(dot + 1)..] : "";
        if (dot > package.LastIndexOf('/')) package = package[..dot];
        if (!provider.TryGetGameFile(package + ".uasset", out var file) && !provider.TryGetGameFile(package + ".umap", out file))
            throw new FileNotFoundException($"Package not found: {reference}");
        await OpenAsset(new AssetEntry(file), objectName: name);
        if (Documents.SelectedItem is TabItem { Content: Grid { Tag: CodeDocument code } } && name.Length > 0)
            code.NavigateTo(name);
    }
    private async void OpenSearch(object? sender, RoutedEventArgs e) => await SearchAsync();
    private async void OpenExports(object? sender, RoutedEventArgs e) => await ShowExports();
    private async void OpenSnooper(object? sender, RoutedEventArgs e) => await Run("Opening 3D Viewer", _ => { _snooper.Show(); return Task.CompletedTask; });
    internal void SetViewerRequest(string? text)
    {
        ViewerRequest.Text = text is null ? "" : text + " — open an asset to send it to Snooper";
        ViewerRequestPanel.IsVisible = text is not null;
    }
    private void CancelViewerRequest(object? sender, RoutedEventArgs e) => Snooper.Hosting.Bridge.CancelRequest();
    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var editedProfile = Workspace.Profile is null ? null : GameProfiles.Clone(Workspace.Profile);
        var oldProfile = System.Text.Json.JsonSerializer.Serialize(Workspace.Profile, DesktopSettings.JsonOptions);
        var oldLanguage = Settings.Language;
        var oldReadOptions = (Settings.ReadScriptData, Settings.ReadShaderMaps);
        if (!await new SettingsWindow(Settings, editedProfile, Workspace.Provider).ShowDialog<bool>(this)) return;
        Settings.Save();
        UpdateDiscordPresence();
        CodeDocument.JsonTheme = Settings.JsonHighlightTheme;
        foreach (var code in _documentResources.Values.SelectMany(x => x).OfType<CodeDocument>()) code.ApplyTheme(Settings.JsonHighlightTheme);
        if (editedProfile is { } profile && (System.Text.Json.JsonSerializer.Serialize(profile, DesktopSettings.JsonOptions) != oldProfile || Settings.Language != oldLanguage || oldReadOptions != (Settings.ReadScriptData, Settings.ReadShaderMaps)))
        { await LoadProfile(profile); return; }
        ExplorerView.SelectedIndex = Settings.ExplorerGrid ? 0 : 1;
        await _thumbnails.ResetAsync(); _thumbnails.Resume();
        PopulateExplorer(PackageList.ItemsSource?.OfType<AssetEntry>().ToArray() ?? []);
    }
    private async void OpenBackups(object? sender, RoutedEventArgs e) => await new BackupWindow(this).ShowDialog(this);
    private AudioWindow GetAudioWindow()
    {
        if (_audio is not null) return _audio;
        _audio = new AudioWindow(Settings);
        _audio.Closed += (_, _) => _audio = null;
        return _audio;
    }
    private void OpenAudio(object? sender, RoutedEventArgs e)
    {
        var window = GetAudioWindow();
        if (!window.IsVisible) window.Show(this);
        window.Activate();
    }
    private ImageMergerWindow ShowMerger()
    {
        if (_merger is null)
        {
            _merger = new ImageMergerWindow();
            _merger.Closed += (_, _) => _merger = null;
            _merger.Show(this);
        }
        _merger.Activate();
        return _merger;
    }
    private void OpenImageMerger(object? sender, RoutedEventArgs e) => ShowMerger();
    private void Cancel(object? sender, RoutedEventArgs e) => _operation?.Cancel();
    private void OpenOutput(object? sender, RoutedEventArgs e) { Directory.CreateDirectory(Settings.OutputDirectory); OpenUrl(Settings.OutputDirectory); }
    internal static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void Donate(object? sender, RoutedEventArgs e) => OpenUrl("https://fmodel.app/donate");
    private void Releases(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/4sval/FModel/releases");
    private void Bugs(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/4sval/FModel/discussions/categories/q-a");
    private void Discord(object? sender, RoutedEventArgs e) => OpenUrl("https://fmodel.app/discord");
    private async void About(object? sender, RoutedEventArgs e) => await Ui.Message(this, "About FModel", "FModel 4.4.4\nUnreal Engine Archives Explorer\n\nCUE4Parse · Snooper · Avalonia\nLicensed under GPL-3.0\n\nNative desktop port: archive browsing, properties, textures, audio and export.\nSee docs/macos.md for feature compatibility and build instructions.");
    private async void Dropped(object? sender, DragEventArgs e)
    {
        if (_operation is not null) return;
#pragma warning disable CS0618
        var item = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
#pragma warning restore CS0618
        if (item is null) return;
        var directory = Directory.Exists(item) ? item : Path.GetDirectoryName(item)!;
        var detected = GameDirectoryResolver.Detect(directory);
        var profile = Settings.Games.FirstOrDefault(x => x.Directory == detected.Directory) ?? detected;
        await LoadProfile(profile);
    }

    internal async Task PopulateSmokeTest(string location)
    {
        var profile = File.Exists(location) ? GameProfiles.Load(location) : new GameProfile { Name = "UE6.0 Fixture", Directory = location, Version = CUE4Parse.UE4.Versions.EGame.GAME_UE6_0 };
        await LoadProfile(profile);
        var texture = Workspace.Files.First(x => x.Name is "T_BC7.uasset" or "T_Banners-Icons-S9-Completion.uasset");
        var folder = Flatten(Workspace.Folders).Single(x => x.Path == texture.File.Directory.TrimEnd('/'));
        await OpenExplorerEntry(folder);
        PackageFilter.Text = "does-not-match";
        ExplorerFilter.Text = "does-not-match";
        FolderFilter.Text = "does-not-match";
        AssetCategory.SelectedIndex = 2;
        while (_operation is not null) await Task.Delay(10);
        await GetSearchWindow().CheckNavigation(texture);
        if (_folder != folder || PackageList.SelectedItem is not AssetEntry selected || selected.Path != texture.Path ||
            TileExplorer.SelectedEntries.OfType<AssetEntry>().SingleOrDefault()?.Path != texture.Path ||
            PackageFilter.Text != "" || ExplorerFilter.Text != "" || FolderFilter.Text != "" || AssetCategory.SelectedIndex != 0)
            throw new InvalidOperationException("Search/reference Go To failed to clear filters and select the package.");
        _searchWindow?.Close();
        Console.WriteLine("PASS: Search and reference Go To clear filters, reveal the folder, and select the package with full asset context actions.");
        await Task.Delay(300);
        var thumbnailDeadline = Stopwatch.StartNew();
        while (_thumbnails.LoadedCount == 0 && thumbnailDeadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        if (_thumbnails.LoadedCount == 0) throw new InvalidOperationException("Thumbnail preview did not load: " + _thumbnails.LastError);
        TileExplorer.CheckRealizedEntries();
        TileExplorer.CheckSelectedEntryVisible();
        SaveWindowCapture("explorer-smoke-test.png");
        await OpenAsset(texture);
        await Task.Delay(150);
        SaveWindowCapture("image-smoke-test.png");
        var tabCount = Documents.ItemCount;
        await OpenAsset(texture, addNewTab: true);
        if (Documents.ItemCount != tabCount + 1) throw new InvalidOperationException("Extract in New Tab reused an existing asset tab.");
        await OpenAsset(texture);
        if (Documents.ItemCount != tabCount + 1) throw new InvalidOperationException("Ordinary extraction did not reuse the selected tab.");
        AddAssetTab(this, new RoutedEventArgs());
        await OpenAsset(texture, addNewTab: true);
        if (Documents.ItemCount != tabCount + 2 || Documents.SelectedItem is not TabItem { Tag: AssetEntry opened } || opened.Path != texture.Path)
            throw new InvalidOperationException("Extraction did not reuse the selected blank tab.");
        Console.WriteLine("PASS: Original extraction replaces the current tab, opens a new tab on request, and reuses a blank tab.");
        if (Workspace.Files.FirstOrDefault(x => x.Name is "SW_Inline.uasset" or "VR_click1.uasset") is { } soundEntry)
        {
            var sound = Workspace.Preview(soundEntry, Settings, CancellationToken.None);
            var audioSettings = new DesktopSettings { AudioPlayerVolume = 0, OutputDirectory = Path.Combine(DesktopSettings.DataDirectory, "AudioSmoke") };
            var audio = new AudioWindow(audioSettings);
            audio.Show(this);
            try
            {
                await audio.CheckPlaylist(sound.Audio!, sound.AudioFormat!);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)audio.Bounds.Width, (int)audio.Bounds.Height));
                bitmap.Render(audio); bitmap.Save(Path.Combine(DesktopSettings.DataDirectory, "audio-smoke-test.png"));
            }
            finally { audio.Close(); }
            Console.WriteLine("PASS: Native audio playlist add, next, previous, seeking, decoded save, and removal.");
        }
        var settingsWindow = new SettingsWindow(Settings, Workspace.Profile, Workspace.Provider);
        settingsWindow.Show(this);
        await Task.Delay(100);
        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)settingsWindow.Bounds.Width, (int)settingsWindow.Bounds.Height)))
        { bitmap.Render(settingsWindow); bitmap.Save(Path.Combine(DesktopSettings.DataDirectory, "settings-smoke-test.png")); }
        settingsWindow.Close();
        byte[] AnimationFrame(SkiaSharp.SKColor color)
        {
            using var pixels = new SkiaSharp.SKBitmap(2, 2);
            pixels.Erase(color);
            using var encoded = pixels.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        var red = AnimationFrame(SkiaSharp.SKColors.Red);
        var blue = AnimationFrame(SkiaSharp.SKColors.Blue);
        using (var animation = new ImageDocument(new PreviewImage("Animation", red, 2, 2, Frames:
            [new PreviewFrame(red, TimeSpan.FromMilliseconds(100)), new PreviewFrame(blue, TimeSpan.FromMilliseconds(100))])))
        {
            var animationWindow = new Window { Width = 300, Height = 300, Content = animation };
            animationWindow.Show(this);
            try { await animation.CheckAnimation(); }
            finally { animationWindow.Close(); }
            if (animation.IsAnimationPlaying) throw new InvalidOperationException("The image animation continued after closing its window.");
        }
        Console.WriteLine("PASS: Animated image frames advance and stop when their window closes.");
        await CheckDirectExports(texture, Workspace.Files.FirstOrDefault(entry => entry.Name is "SW_Inline.uasset" or "VR_click1.uasset"));
        var originalOutput = Settings.OutputDirectory;
        var originalFormat = Settings.TextureFormat;
        Settings.OutputDirectory = Path.Combine(DesktopSettings.DataDirectory, "ExportSmoke", Guid.NewGuid().ToString("N"));
        Settings.TextureFormat = ETextureFormat.Png;
        Workspace.Exports.Clear();
        Workspace.Queue(texture, "Texture", default);
        var exportWindow = new ExportWindow(this);
        exportWindow.Show(this);
        try
        {
            await Task.Delay(100);
            await exportWindow.CheckSession(this, texture);
            Console.WriteLine("PASS: Original export-session options, queue removal/re-add and isolated WebP export.");
        }
        finally
        {
            exportWindow.Close(); Settings.OutputDirectory = originalOutput; Settings.TextureFormat = originalFormat;
        }
        if (ArchiveList.ItemCount == 0 || FolderTree.ItemCount == 0 || Documents.ItemCount == 0)
            throw new InvalidOperationException("Archive, tree or document controls did not populate.");
    }

    private void SaveWindowCapture(string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        bitmap.Render(this); bitmap.Save(Path.Combine(DesktopSettings.DataDirectory, name));
    }

    private async Task CheckDirectExports(AssetEntry texture, AssetEntry? audio)
    {
        var names = new[] { nameof(DesktopSettings.OutputDirectory), nameof(DesktopSettings.UseCustomOutputFolders),
            nameof(DesktopSettings.RawDataDirectory), nameof(DesktopSettings.PropertiesDirectory), nameof(DesktopSettings.TextureDirectory),
            nameof(DesktopSettings.AudioDirectory), nameof(DesktopSettings.KeepDirectoryStructure), nameof(DesktopSettings.ExportImmediately),
            nameof(DesktopSettings.ConvertAudioOnBulkExport), nameof(DesktopSettings.TextureFormat) };
        var previous = names.Select(name => typeof(DesktopSettings).GetProperty(name)!).ToDictionary(property => property, property => property.GetValue(Settings));
        try
        {
            var root = Path.Combine(DesktopSettings.DataDirectory, "DirectExportSmoke", Guid.NewGuid().ToString("N"));
            Settings.OutputDirectory = root;
            Settings.UseCustomOutputFolders = true;
            Settings.RawDataDirectory = Path.Combine(root, "Raw");
            Settings.PropertiesDirectory = Path.Combine(root, "Properties");
            Settings.TextureDirectory = Path.Combine(root, "Textures");
            Settings.AudioDirectory = Path.Combine(root, "Audio");
            Settings.KeepDirectoryStructure = false;
            Settings.ExportImmediately = false;
            Settings.ConvertAudioOnBulkExport = true;
            Settings.TextureFormat = ETextureFormat.Png;
            Workspace.Exports.Clear();
            Workspace.Queue(texture, "Texture", default);
            var queuedCount = Workspace.Exports.TotalQueued;
            await Queue([texture], "Properties");
            var jsonPath = Path.Combine(Settings.PropertiesDirectory, texture.File.NameWithoutExtension + ".json");
            using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)))
                if (json.RootElement.GetArrayLength() != Workspace.Provider!.LoadPackage(texture.File).GetExports().Count())
                    throw new InvalidOperationException("Properties export omitted package exports.");
            if (Workspace.Exports.TotalQueued != queuedCount) throw new InvalidOperationException("Direct properties export changed the queued model/texture session.");
            if (audio is not null)
            {
                await Queue([audio], "Audio");
                if (!Directory.EnumerateFiles(Settings.AudioDirectory, "*.wav").Any()) throw new InvalidOperationException("Direct audio export did not convert into the custom audio folder.");
            }
            if (Workspace.Files.FirstOrDefault(entry => entry.Name == "CID_001_Athena_Commando_F_Default.uasset") is { } cosmetic)
            {
                await Queue([cosmetic], "Texture");
                if (!Directory.EnumerateFiles(Settings.TextureDirectory, "*.png").Any() || Workspace.Exports.TotalQueued != queuedCount)
                    throw new InvalidOperationException("Original cosmetic image export did not save directly into its custom folder.");
            }
            Workspace.Exports.Clear();
            await Queue([texture], "Raw");
            Workspace.ConfigureQueuedExports(Settings);
            var raw = await Workspace.Exports.RunAsync(root, Settings.ExportOptions());
            if (raw.Count == 0 || raw.Any(result => !result.Success || result.DiskFilePaths!.Any(path => Path.GetDirectoryName(path) != Settings.RawDataDirectory)))
                throw new InvalidOperationException("Raw exports did not honor the custom folder and flattened structure.");
            Workspace.Queue(texture, "Raw", default);
            Workspace.ConfigureQueuedExports(Settings, overrideDirectory: true);
            var overridden = Path.Combine(root, "SessionOverride");
            var results = await Workspace.Exports.RunAsync(overridden, Settings.ExportOptions());
            if (results.Any(result => !result.Success || result.DiskFilePaths!.Any(path => Path.GetDirectoryName(path) != overridden)))
                throw new InvalidOperationException("Session output-directory override did not override the raw-data folder.");
            Console.WriteLine("PASS: Original immediate package JSON/audio/image exports, custom folders, flattened output and isolated queued exports.");
        }
        finally
        {
            Workspace.Exports.Clear();
            foreach (var (property, value) in previous) property.SetValue(Settings, value);
        }
    }

    internal async Task PopulateSnooperSmoke(string profilePath, string assetName, string output)
    {
        await LoadProfile(GameProfiles.Load(profilePath));
        var entry = Workspace.Files.Single(x => x.Name == assetName);
        var asset = Workspace.Provider!.LoadPackage(entry.File).GetExports().FirstOrDefault(SnooperScene.CanView);
        var texture = asset is null ? Workspace.Preview(entry, Settings, CancellationToken.None).Images.FirstOrDefault(x => x.Texture is not null) : null;
        if (asset is null && texture?.Texture is null) throw new InvalidOperationException("The validation asset has no scene or decoded texture.");
        Directory.CreateDirectory(output);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var actor = asset is null ? null : await Task.Run(() => SnooperScene.Create(asset, Settings, Workspace.Profile!, CancellationToken.None));
            var editor = _snooper.Show(actor);
            if (texture?.Texture is { } decoded) _snooper.InspectTexture(decoded, texture.Name, false);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = 0;
            var iteration = cycle;
            editor.RenderFrame += _ =>
            {
                if (++frames < 16 || Snooper.Rendering.Cache.TextureCache.IsLoading || done.Task.IsCompleted) return;
                try { SnooperSmoke.Capture(editor, Path.Combine(output, $"snooper-{iteration}.png")); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
            };
            await done.Task.WaitAsync(TimeSpan.FromSeconds(120));
            _snooper.Reset();
        }
        Console.WriteLine("PASS: three complete Snooper context lifecycles, texture caches and native windows.");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        bitmap.Render(this);
        bitmap.Save(Path.Combine(output, "browser.png"));
    }
}
