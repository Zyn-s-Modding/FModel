using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Extensions;

namespace FModel.Desktop;

public partial class MainWindow
{
    private readonly ThumbnailService _thumbnails;
    private bool _packageSelection;
    private bool _changingFolder;

    private void InitializeExplorer()
    {
        LoadingMode.ItemsSource = new[] { "Multiple", "All", "All (New)", "All (Modified)", "All (Except Patched Assets)" };
        LoadingMode.SelectedItem = Settings.ArchiveLoadingMode;
        ExplorerView.SelectedIndex = Settings.ExplorerGrid ? 0 : 1;
        AssetCategory.ItemsSource = AssetCategoryExtensions.GetBaseCategories().ToArray();
        AssetCategory.SelectedIndex = 0;
        GridViewButton.Content = AssetIcons.Named("GridIcon"); ListViewButton.Content = AssetIcons.Named("ListIcon");
        BrowserToggle.Content = AssetIcons.Named("GridIcon"); TexturePreviewToggle.Content = AssetIcons.Named("TextureIconAlt");
        LocateFolderButton.Content = AssetIcons.Named("LocateMeIcon"); CollapseFolderButton.Content = AssetIcons.Named("FoldIcon");
        OutputFolderButton.Content = AssetIcons.Named("FolderIcon", 12); ClearLogsButton.Content = AssetIcons.Named("RemoveIcon", 12);
        foreach (var filter in new[] { FolderFilter, PackageFilter, ExplorerFilter })
        {
            filter.InnerLeftContent = AssetIcons.Named("SearchIcon");
            var clear = Ui.Button("", () => { filter.Text = ""; return Task.CompletedTask; });
            clear.Content = AssetIcons.Named("BackspaceIcon"); clear.Classes.Add("toolbar"); clear.Padding = new Avalonia.Thickness(4, 1);
            filter.InnerRightContent = clear;
        }
        TexturePreviewToggle.IsChecked = Settings.PreviewExplorerTextures;
        NewExplorer.IsChecked = Settings.FeaturePreviewNewAssetExplorer;
        FolderTree.DoubleTapped += (_, _) => { if (NewExplorer.IsChecked != true && _folder is not null) LeftTabs.SelectedIndex = 2; };
        Breadcrumb.PropertyChanged += (_, change) => { if (change.Property == TextBlock.TextProperty) RebuildBreadcrumb(); };
        RebuildBreadcrumb();
        TileExplorer.Thumbnails = _thumbnails;
        TileExplorer.OpenEntry = async entry => await OpenExplorerEntry(entry);
        TileExplorer.SelectionChanged = selected => { _packageSelection = false; UpdateAssetInformation(selected); };
        PackageList.GotFocus += (_, _) => _packageSelection = true;
        Explorer.GotFocus += (_, _) => _packageSelection = false;
        TileExplorer.GotFocus += (_, _) => _packageSelection = false;
        Explorer.ItemTemplate = new FuncDataTemplate<object>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,90,80"), Margin = new Avalonia.Thickness(3, 1) };
            row.Children.Add(entry is AssetEntry file ? AssetIcons.For(file, 16) : AssetIcons.Folder(16));
            var name = new TextBlock { Text = entry switch { AssetEntry asset => asset.Name, FolderEntry folder => folder.Name, _ => "" }, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var type = new TextBlock { Text = "Folder", Foreground = Brush.Parse("#AAAAAA") };
            if (entry is AssetEntry) type.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(AssetEntry.DisplayType)));
            Grid.SetColumn(type, 2); row.Children.Add(type);
            var size = new TextBlock { Text = (entry as AssetEntry)?.Size, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brush.Parse("#AAAAAA") };
            Grid.SetColumn(size, 3); row.Children.Add(size);
            return row;
        });
        TileExplorer.ContextMenu = CreateExplorerMenu(() => TileExplorer.SelectedEntries.ToArray());
        Explorer.ContextMenu = CreateExplorerMenu(() => Explorer.SelectedItems?.Cast<object>().ToArray() ?? []);
        PackageList.ContextMenu = CreateAssetMenu(SelectedAssets);
        FolderTree.ContextMenu = CreateFolderMenu(() => FolderTree.SelectedItem is FolderEntry folder ? [folder] : []);
        FolderTree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(FolderTree).Properties.IsRightButtonPressed) return;
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is FolderEntry folder)
                FolderTree.SelectedItem = folder;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PackageList.ContextRequested += (_, _) => _packageSelection = true;
        Explorer.ContextRequested += (_, _) => _packageSelection = false;
        TileExplorer.ContextRequested += (_, _) => _packageSelection = false;
        SelectRightClickedItem(PackageList);
        SelectRightClickedItem(Explorer);
        RefreshFavorites();
    }

    private void RevealFolder(FolderEntry folder)
    {
        FolderFilter.Text = "";
        var ancestors = new Stack<FolderEntry>();
        for (var node = folder; node is not null && node.Path.Length > 0; node = node.Parent) ancestors.Push(node);
        ItemsControl parent = FolderTree;
        while (ancestors.TryPop(out var next))
        {
            parent.UpdateLayout();
            if (parent.ContainerFromItem(next) is not TreeViewItem container) break;
            container.IsExpanded = true;
            container.UpdateLayout();
            container.BringIntoView();
            parent = container;
        }
        FolderTree.SelectedItem = folder;
    }

    private void PopulateExplorer(AssetEntry[] files)
    {
        if (AssetCategory?.SelectedItem is EAssetCategory category && category != EAssetCategory.All)
            files = files.Where(x => x.Category.IsOfCategory(category)).ToArray();
        var query = ExplorerFilter.Text ?? "";
        var folders = (_folder?.Children.AsEnumerable() ?? Workspace.Folders).Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        var combined = folders.Cast<object>().Concat(files).ToArray();
        Explorer.ItemsSource = combined;
        TileExplorer.SetEntries(combined);
    }

    private async Task OpenExplorerEntry(object? item)
    {
        if (item is AssetEntry file) await OpenAsset(file);
        else if (item is FolderEntry folder)
        {
            FolderTree.SelectedItem = folder;
            // A filtered tree may not contain this item; navigation still works.
            if (_folder != folder) DisplayFolder(folder);
            SetBrowserVisible(NewExplorer.IsChecked == true);
            RevealFolder(folder);
        }
    }

    private void SetBrowserVisible(bool visible)
    {
        if (ExplorerPanel is null || Documents is null) return;
        ExplorerPanel.IsVisible = visible;
        Documents.IsVisible = !visible;
        BrowserToggle.IsChecked = visible;
    }

    private void ToggleBrowser(object? sender, RoutedEventArgs e) => SetBrowserVisible(BrowserToggle.IsChecked == true);
    private void ExplorerViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TileExplorer is null || Explorer is null) return;
        Settings.ExplorerGrid = ExplorerView.SelectedIndex == 0;
        TileExplorer.IsVisible = Settings.ExplorerGrid;
        Explorer.IsVisible = !Settings.ExplorerGrid;
        if (GridViewButton is not null) GridViewButton.IsChecked = Settings.ExplorerGrid;
        if (ListViewButton is not null) ListViewButton.IsChecked = !Settings.ExplorerGrid;
    }

    private void ChooseGridView(object? sender, RoutedEventArgs e) => ExplorerView.SelectedIndex = 0;
    private void ChooseListView(object? sender, RoutedEventArgs e) => ExplorerView.SelectedIndex = 1;
    private async void AssetCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingFolder || _thumbnails is null || PackageList?.ItemsSource is not IEnumerable<AssetEntry> files) return;
        if (_folder is not null && AssetCategory.SelectedItem is EAssetCategory chosen) _folder.SelectedCategory = chosen;
        var entries = files.ToArray();
        if (AssetCategory.SelectedItem is EAssetCategory category && category != EAssetCategory.All)
            await Run("Resolving asset categories", token => Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    try { Workspace.ResolveAsset(entry, token); }
                    catch (Exception error) when (error is not OperationCanceledException) { AppendLog($"{entry.Path}: {error.Message}"); }
                }
            }, token));
        PopulateExplorer(entries);
    }
    private void NewExplorerChanged(object? sender, RoutedEventArgs e)
    {
        if (ExplorerTools is null) return;
        Settings.FeaturePreviewNewAssetExplorer = NewExplorer.IsChecked == true;
        ExplorerTools.IsVisible = NewExplorer.IsChecked == true;
        SetBrowserVisible(NewExplorer.IsChecked == true);
    }
    private async void TexturePreviewChanged(object? sender, RoutedEventArgs e)
    {
        if (_thumbnails is null) return;
        Settings.PreviewExplorerTextures = TexturePreviewToggle.IsChecked == true;
        await _thumbnails.ResetAsync(); _thumbnails.Resume();
        PopulateExplorer(PackageList.ItemsSource?.OfType<AssetEntry>().ToArray() ?? []);
    }
    private void RebuildBreadcrumb()
    {
        BreadcrumbBar.Children.Clear();
        var root = Ui.Button("/", () =>
        {
            _folder = null; FolderTree.SelectedItem = null; Breadcrumb.Text = "/"; DisplayFiles([]);
            return Task.CompletedTask;
        });
        root.Padding = new Avalonia.Thickness(4, 1); root.BorderThickness = new Avalonia.Thickness(0); root.Background = Brushes.Transparent;
        BreadcrumbBar.Children.Add(root);
        var path = "";
        foreach (var name in (Breadcrumb.Text ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? name : path + "/" + name;
            var destination = path;
            var item = Ui.Button(name, async () =>
            {
                if (Flatten(Workspace.Folders).FirstOrDefault(folder => folder.Path.Equals(destination, StringComparison.OrdinalIgnoreCase)) is { } folder) await OpenExplorerEntry(folder);
            });
            item.Padding = new Avalonia.Thickness(3, 1); item.BorderThickness = new Avalonia.Thickness(0); item.Background = Brushes.Transparent;
            BreadcrumbBar.Children.Add(item);
            BreadcrumbBar.Children.Add(new TextBlock { Text = "›", Margin = new Avalonia.Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#8E8E8E") });
        }
    }

    private async void ParentFolder(object? sender, RoutedEventArgs e)
    {
        if (_folder?.Parent is { Path.Length: > 0 } parent) await OpenExplorerEntry(parent);
        else
        {
            _folder = null;
            FolderTree.SelectedItem = null;
            Breadcrumb.Text = "/";
            DisplayFiles([]);
        }
    }

    private void UpdateAssetInformation(object? selected)
    {
        if (selected is not AssetEntry entry) return;
        var reader = (entry.File as VfsEntry)?.Vfs;
        AssetInfo.Text = $"Offset: 0x{(entry.File as VfsEntry)?.Offset ?? 0:X}\nSize: {entry.Size}\nCompression Method: {entry.File.CompressionMethod}\nIs Encrypted: {entry.File.IsEncrypted}\nIncluded In Archive: {reader?.Name ?? "Loose file"}";
    }

    private void UpdateFolderInformation(FolderEntry folder)
    {
        var reader = (folder.DescendantFiles().FirstOrDefault()?.File as VfsEntry)?.Vfs;
        FolderInfo.Text = $"Packages Count: {folder.Files.Count:N0}\nFolders Count: {folder.Children.Count:N0}\nIncluded In Archive: {reader?.Name ?? "None"}\nArchive Mount Point: {reader?.MountPoint ?? "/"}\nArchive Version: {Workspace.Profile?.Version}";
    }

    private void PopulateArchives()
    {
        var provider = Workspace.Provider!;
        var entries = provider.MountedVfs.Select(x => new ArchiveEntry(x, true))
            .Concat(provider.UnloadedVfs.Select(x => new ArchiveEntry(x, false))).ToList();
        var loose = provider.Files.Values.Count(x => x is not VfsEntry);
        if (loose > 0) entries.Add(new ArchiveEntry(null, true, loose));
        ArchiveList.ItemsSource = entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ArchiveSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ArchiveList.SelectedItem is not ArchiveEntry entry) return;
        ArchiveInfo.Text = entry.Reader is { } reader
            ? $"Mount Point: {reader.MountPoint}\nFile Count: {reader.FileCount:N0}\nIs Encrypted: {reader.IsEncrypted}\nGlobal Unique Identifier: {reader.EncryptionKeyGuid}\n{(entry.Loaded ? "Loaded" : reader.IsEncrypted ? "Requires an AES key" : "Metadata / auxiliary or unloaded")}" : $"{entry.LooseFiles:N0} loose files";
    }

    private async void LoadSelectedArchives(object? sender, RoutedEventArgs e)
    {
        if (Workspace.Provider is not { } provider) { await SelectGameAsync(); return; }
        var mode = LoadingMode.SelectedItem as string ?? "All";
        Dictionary<string, BackupEntry>? snapshot = null;
        if (mode is "All (New)" or "All (Modified)")
        {
            var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select an older backup", FileTypeFilter = [new FilePickerFileType("FModel backup") { Patterns = ["*.fbkp", "*.json"] }] });
            if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            try { snapshot = await Task.Run(() => ArchiveBackup.Read(path)); }
            catch (Exception error) { await Ui.Message(this, "Backup", error.Message); return; }
            if (snapshot is null) return;
        }
        var selected = ArchiveList.SelectedItems?.OfType<ArchiveEntry>().Where(x => x.Loaded).ToArray() ?? [];
        var readers = selected.Where(x => x.Reader is not null).Select(x => x.Reader!).ToHashSet();
        var loose = selected.Any(x => x.Reader is null);
        await Run("Filtering packages", async token =>
        {
            await _thumbnails.ResetAsync();
            try
            {
                await Task.Run(() => Workspace.Reindex(file =>
                {
                    token.ThrowIfCancellationRequested();
                    return mode switch
                    {
                        "Multiple" when selected.Length > 0 => file is VfsEntry vfs ? vfs.Vfs is IAesVfsReader reader && readers.Contains(reader) : loose,
                        "All (New)" => !snapshot!.ContainsKey(file.Path),
                        "All (Modified)" => snapshot!.TryGetValue(file.Path, out var before) && ArchiveBackup.Changed(before, file),
                        _ => true
                    };
                }), token);
                _folder = null;
                FolderTree.ItemsSource = Workspace.Folders;
                Breadcrumb.Text = "/";
                DisplayFiles([]);
                LeftTabs.SelectedIndex = 1;
                SetBrowserVisible(true);
                Settings.ArchiveLoadingMode = mode; Settings.Save();
            }
            finally { _thumbnails.Resume(); }
        });
    }

    private void ShowArchiveInfo(object? sender, RoutedEventArgs e)
    {
        if (Workspace.Provider is not { } provider) return;
        var rows = provider.MountedVfs.Concat(provider.UnloadedVfs).Select(x => new
        { x.Name, x.MountPoint, x.FileCount, x.Length, x.IsEncrypted, x.EncryptionKeyGuid, x.ReadOrder, Loaded = provider.MountedVfs.Contains(x) });
        ShowTextDocument("Archives Info", Newtonsoft.Json.JsonConvert.SerializeObject(rows, Newtonsoft.Json.Formatting.Indented));
    }

    private void CollapseFolders(object? sender, RoutedEventArgs e)
    {
        foreach (var item in FolderTree.GetVisualDescendants().OfType<TreeViewItem>().ToArray()) item.IsExpanded = false;
    }

    private void LocateFolder(object? sender, RoutedEventArgs e) { if (_folder is { } folder) RevealFolder(folder); }
    private void ClearLogs(object? sender, RoutedEventArgs e) { _messages.Clear(); LogView.Text = ""; }
    private void ToggleLog(object? sender, RoutedEventArgs e)
    {
        LogBody.IsVisible = !LogBody.IsVisible;
        PropertyAndConsole.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(LogBody.IsVisible ? 125 : 16);
        LogToggle.Content = LogBody.IsVisible ? "⌃" : "⌄";
    }

    private void FavoriteFolder(object? sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        if (!Settings.FavoriteFolders.Remove(_folder.Path)) Settings.FavoriteFolders.Add(_folder.Path);
        Settings.Save(); RefreshFavorites();
    }

    private void RefreshFavorites()
    {
        FavoritesMenu.Items.Clear();
        foreach (var path in Settings.FavoriteFolders)
        {
            var favorite = path;
            FavoritesMenu.Items.Add(Ui.Menu(path, async () =>
            {
                var folder = Flatten(Workspace.Folders).FirstOrDefault(x => x.Path.Equals(favorite, StringComparison.OrdinalIgnoreCase));
                if (folder is not null) await OpenExplorerEntry(folder);
                else await Ui.Message(this, "Favorite Directory", "This directory is not in the loaded packages.");
            }));
        }
    }

    private sealed record ArchiveEntry(IAesVfsReader? Reader, bool Loaded, int LooseFiles = 0)
    {
        public string Name => Reader?.Name ?? "Loose files";
        public string State => Loaded ? "●" : Reader?.IsEncrypted == true ? "◇" : "○";
        public string Size => Reader is null ? "" : Reader.Length >= 1024L * 1024 * 1024 ? $"{Reader.Length / (1024.0 * 1024 * 1024):N1} GiB" : $"{Reader.Length / (1024.0 * 1024):N1} MiB";
    }
}
