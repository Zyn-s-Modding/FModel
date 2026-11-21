using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace FModel.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, Window> _propertyWindows = [];

    private void OpenPropertiesPopout(TabItem tab, AssetEntry asset)
    {
        if (_propertyWindows.TryGetValue(asset.Path, out var existing)) { existing.Activate(); return; }
        var source = tab.Content as CodeDocument ?? (tab.Content as Control)?.Tag as CodeDocument;
        if (source is null) return;
        var document = new CodeDocument(source.Editor.Text, source.Syntax, OpenReference);
        var window = Ui.Dialog(asset.Name + " (Properties)", 1000, 700);
        window.Content = document;
        _propertyWindows.Add(asset.Path, window);
        window.Closed += (_, _) => { _propertyWindows.Remove(asset.Path); document.Dispose(); };
        window.Show(this);
    }

    private void ConfigureDocumentTab(TabItem tab, AssetEntry? asset)
    {
        var menu = CreateAssetMenu(() => asset is null ? [] : [asset], includeEmbedded: false);
        {
            menu.Items.Remove(menu.Items.OfType<MenuItem>().First());
            var copyIndex = menu.Items.IndexOf(menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "Copy")));
            menu.Items.Insert(copyIndex, Ui.Menu("Open Properties", () => { if (asset is not null) OpenPropertiesPopout(tab, asset); return Task.CompletedTask; }));
            menu.Items.Insert(copyIndex + 1, Ui.Menu("Jump to Package Folder", () => { if (asset is not null) NavigateToAsset(asset); return Task.CompletedTask; }));
            menu.Items.Insert(copyIndex + 2, new Separator());
        }
        menu.Items.Insert(0, Ui.Menu("Close", () => { CloseDocument(tab); return Task.CompletedTask; }));
        menu.Items.Insert(1, Ui.Menu("Close All Tabs", () =>
        {
            foreach (var item in Documents.Items.OfType<TabItem>().ToArray()) CloseDocument(item);
            return Task.CompletedTask;
        }));
        menu.Items.Insert(2, Ui.Menu("Close Other Tabs", () =>
        {
            foreach (var item in Documents.Items.OfType<TabItem>().Where(x => x != tab).ToArray()) CloseDocument(item);
            return Task.CompletedTask;
        }));
        menu.Items.Insert(3, new Separator());
        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.Take(3).OfType<MenuItem>()) item.IsEnabled = true;
        };
        tab.ContextMenu = menu;
        tab.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed) return;
            CloseDocument(tab); e.Handled = true;
        };
    }

    private ContextMenu CreateExplorerMenu(Func<object[]> selection)
    {
        var menu = CreateAssetMenu(() => selection().OfType<AssetEntry>().ToArray());
        var assetItems = menu.Items.OfType<Control>().ToArray();
        var folders = CreateFolderMenu(() => selection().OfType<FolderEntry>().ToArray());
        var folderItems = folders.Items.OfType<Control>().ToArray();
        folders.Items.Clear();
        foreach (var item in folderItems) menu.Items.Add(item);
        menu.Opening += (_, _) =>
        {
            var folderSelected = selection().OfType<FolderEntry>().Any();
            foreach (var item in assetItems) item.IsVisible = !folderSelected;
            foreach (var item in folderItems) { item.IsVisible = folderSelected; item.IsEnabled = folderSelected; }
            if (!folderSelected)
                foreach (var item in assetItems.OfType<MenuItem>().Where(x => Equals(x.Header, "Decompile Blueprint")))
                    item.IsVisible = Settings.ShowDecompileOption;
        };
        return menu;
    }

    private ContextMenu CreateFolderMenu(Func<FolderEntry[]> selection)
    {
        var export = new MenuItem { Header = "Export Folder" };
        foreach (var (label, mode) in new[] { ("Raw Data (.uasset)", "Raw"), ("Embedded Files", "Embedded"),
            ("Properties (.json)", "Properties"), ("Textures", "Texture"), ("Decompiled Blueprints", "Code"),
            ("Models", "Model"), ("Worlds", "World"), ("Animations", "Animation"), ("Audio", "Audio") })
            export.Items.Add(Ui.Menu(label, () => Queue(selection().SelectMany(x => x.DescendantFiles()).DistinctBy(x => x.Path), mode)));
        var favorite = Ui.Menu("Favorite Directory", () =>
        {
            foreach (var folder in selection())
                if (!Settings.FavoriteFolders.Contains(folder.Path)) Settings.FavoriteFolders.Add(folder.Path);
            Settings.Save(); RefreshFavorites();
            return Task.CompletedTask;
        });
        var copy = Ui.Menu("Copy Directory Path", async () =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(string.Join('\n', selection().Select(x => x.Path)));
        });
        return new ContextMenu { Items = { export, new Separator(), favorite, copy } };
    }

    internal static void SelectRightClickedItem(ListBox list)
    {
        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
            var container = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (container is null || list.ItemFromContainer(container) is not { } item) return;
            if (list.SelectedItems?.Contains(item) != true) list.SelectedItem = item;
            list.Focus();
        }, RoutingStrategies.Tunnel);
    }

    internal ContextMenu CreateAssetMenu(Func<AssetEntry[]> selection, Action? beforeOpen = null, Action<AssetEntry>? navigate = null, bool includeEmbedded = true)
    {
        async Task Extract()
        {
            var entries = selection();
            beforeOpen?.Invoke();
            foreach (var entry in entries) await OpenAsset(entry, addNewTab: true);
            Activate();
        }
        async Task Copy(Func<AssetEntry, string> format)
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join('\n', selection().Select(format)));
        }
        var export = new MenuItem { Header = "Export" };
        var raw = Ui.Menu("Raw Data", () => Queue(selection(), "Raw"));
        export.Items.Add(raw);
        var embedded = Ui.Menu("Embedded Files", () => Queue(selection(), "Embedded"));
        if (includeEmbedded) export.Items.Add(embedded);
        var exportActions = new Dictionary<MenuItem, EBulkType>();
        foreach (var (label, mode) in new[] { ("Properties (.json)", "Properties"), ("Texture", "Texture"),
            ("Model", "Model"), ("World", "World"), ("Animation", "Animation"), ("Audio", "Audio") })
        {
            var item = Ui.Menu(label, () => Queue(selection(), mode));
            export.Items.Add(item);
            exportActions[item] = mode switch
            {
                "Texture" => EBulkType.Textures, "Model" => EBulkType.Meshes, "World" => EBulkType.Worlds,
                "Animation" => EBulkType.Animations, "Audio" => EBulkType.Audio, _ => EBulkType.None
            };
        }
        var copy = new MenuItem { Header = "Copy" };
        copy.Items.Add(Ui.Menu("Package Path", () => Copy(x => x.Path)));
        copy.Items.Add(Ui.Menu("Package Name", () => Copy(x => x.Name)));
        copy.Items.Add(Ui.Menu("Directory Path", () => Copy(x => x.File.Directory)));
        copy.Items.Add(Ui.Menu("Package Path w/o Extension", () => Copy(x => Path.ChangeExtension(x.Path, null))));
        copy.Items.Add(Ui.Menu("Package Name w/o Extension", () => Copy(x => Path.GetFileNameWithoutExtension(x.Name))));
        var decompile = Ui.Menu("Decompile Blueprint", async () =>
        {
            var entries = selection().Where(x => x.File.IsUePackage && x.Actions.HasFlag(EBulkType.Code)).ToArray();
            beforeOpen?.Invoke();
            foreach (var entry in entries) await DecompileAsset(entry);
        });
        var menu = new ContextMenu();
        if (navigate is not null)
            menu.Items.Add(Ui.Menu("Go To", () =>
            {
                if (selection().FirstOrDefault() is { } entry) navigate(entry);
                return Task.CompletedTask;
            }));
        menu.Items.Add(Ui.Menu("Extract in New Tab", Extract));
        menu.Items.Add(Ui.Menu("Show Metadata", async () =>
        {
            var entries = selection().Where(x => x.File.IsUePackage).ToArray();
            beforeOpen?.Invoke();
            foreach (var entry in entries) await ShowAssetMetadata(entry);
        }));
        menu.Items.Add(Ui.Menu("Find References", async () =>
        {
            if (selection().FirstOrDefault(x => x.File.IsUePackage && x.File is CUE4Parse.UE4.IO.Objects.FIoStoreEntry) is { } entry)
                await FindReferencesAsync(entry);
        }));
        menu.Items.Add(decompile);
        menu.Items.Add(new Separator());
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        void UpdateActions(AssetEntry[] entries)
        {
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = entries.Length > 0;
            raw.Header = $"Raw Data (.{entries.FirstOrDefault()?.Type ?? "uasset"})";
            decompile.IsVisible = Settings.ShowDecompileOption;
            decompile.IsEnabled = entries.Any(x => x.File.IsUePackage && x.Actions.HasFlag(EBulkType.Code));
            foreach (var item in menu.Items.OfType<MenuItem>().Where(x => Equals(x.Header, "Show Metadata") || Equals(x.Header, "Find References")))
                item.IsEnabled = entries.Any(x => x.File.IsUePackage && (!Equals(item.Header, "Find References") || x.File is CUE4Parse.UE4.IO.Objects.FIoStoreEntry));
            embedded.IsVisible = entries.Any(x => x.Actions.HasFlag(EBulkType.EmbeddedFiles));
            foreach (var (item, capability) in exportActions)
                item.IsEnabled = capability == EBulkType.None ? entries.Any(x => x.File.IsUePackage) : entries.Any(x => x.Actions.HasFlag(capability));
        }
        menu.Opening += async (_, _) =>
        {
            var entries = selection();
            UpdateActions(entries);
            try
            {
                foreach (var entry in entries) await _thumbnails.Load(entry, CancellationToken.None);
                if (menu.IsOpen) UpdateActions(entries);
            }
            catch (OperationCanceledException) { }
        };
        return menu;
    }

    internal void NavigateToAsset(AssetEntry entry)
    {
        var folder = Flatten(Workspace.Folders).FirstOrDefault(x =>
            x.Path.Equals(entry.File.Directory.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (folder is null) return;
        folder.SearchText = "";
        folder.SelectedCategory = EAssetCategory.All;
        RevealFolder(folder);
        DisplayFolder(folder);
        var target = folder.Files.FirstOrDefault(x => x.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        PackageList.SelectedItem = Explorer.SelectedItem = target;
        PackageList.ScrollIntoView(target);
        Explorer.ScrollIntoView(target);
        SetBrowserVisible(NewExplorer.IsChecked == true);
        TileExplorer.SelectEntry(target);
        _packageSelection = false;
        LeftTabs.SelectedIndex = NewExplorer.IsChecked == true ? 1 : 2;
        UpdateAssetInformation(target);
        WindowState = WindowState.Normal;
        Activate();
    }

    private Task ShowAssetMetadata(AssetEntry entry) => Run("Reading metadata", async token =>
    {
        var text = await Task.Run(() => Workspace.Metadata(entry), token);
        ShowTextDocument(entry.Name + " — Metadata", text, entry);
    });

    private Task DecompileAsset(AssetEntry entry) => Run("Decompiling Blueprint", async token =>
    {
        var text = await Task.Run(() => Workspace.Decompile(entry, token), token);
        ShowTextDocument(entry.Name + " — Decompiled", text, entry);
    });
}
