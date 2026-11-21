using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using System.Text.Json;

namespace FModel.Desktop;

internal sealed class AesWindow : Window
{
    public AesWindow(GameProfile profile, CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider? provider)
    {
        Title = "AES Manager"; Width = 850; SizeToContent = SizeToContent.Height; MaxHeight = 750;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var lifetime = new CancellationTokenSource();
        Closed += (_, _) => lifetime.Cancel();
        var panel = new DockPanel();
        var instruction = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        instruction.Children.Add(new TextBlock { Text = "Instruction", FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        instruction.Children.Add(new TextBlock { Text = "In order to decipher archives' information, an AES key, in most cases, is needed. Here you can set the key for your static and dynamic archives. If you don't know what key to use for your set game, simply Google it. Keys must start with \"0x\" and contains 64 more characters.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        DockPanel.SetDock(instruction, Dock.Top); panel.Children.Add(instruction);
        var rows = new StackPanel { Margin = new Thickness(12) };
        var inputs = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        void Add(string guid, string label, string key)
        {
            guid = new FGuid(guid).ToString();
            if (inputs.TryGetValue(guid, out var existing)) { if (!string.IsNullOrWhiteSpace(key)) existing.Text = key; return; }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,10,*"), Margin = new Thickness(0, 3) };
            var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            ToolTip.SetTip(name, guid + "\n" + label); row.Children.Add(name);
            var input = new TextBox { Text = key, Watermark = "0x…", MinWidth = 450 };
            inputs.Add(guid, input); Grid.SetColumn(input, 2); row.Children.Add(input); rows.Children.Add(row);
        }
        const string mainGuid = "00000000000000000000000000000000";
        Add(mainGuid, "Main Key", profile.AesKeys.GetValueOrDefault(mainGuid, ""));
        if (provider is not null)
            foreach (var group in provider.MountedVfs.Concat(provider.UnloadedVfs).Where(x => x.IsEncrypted).GroupBy(x => x.EncryptionKeyGuid.ToString()))
            {
                if (group.Key == mainGuid) continue;
                Add(group.Key, string.Join(", ", group.Select(x => Path.GetFileNameWithoutExtension(x.Name))), profile.AesKeys.GetValueOrDefault(group.Key, ""));
            }
        foreach (var (guid, key) in profile.AesKeys) Add(guid, guid, key);
        var endpoint = profile.AesEndpoint.IsConfigured ? profile.AesEndpoint : RemoteEndpoint.Default(profile.Name, false);
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0) };
        var footer = new StackPanel(); footer.Children.Add(error);
        var refresh = Ui.Button("Refresh", async () =>
        {
            foreach (var (guid, key) in await RemoteEndpoints.FetchKeys(endpoint, lifetime.Token)) Add(guid, guid, key);
            profile.LastAesReload = DateTime.Today;
            error.Text = "";
        });
        refresh.IsVisible = endpoint.IsConfigured;
        var buttons = Ui.Row(Ui.Button("OK", () =>
        {
            try
            {
                var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (guid, input) in inputs)
                {
                    var key = input.Text?.Trim() ?? "";
                    if (key.Length == 0) continue;
                    _ = new FAesKey(key); keys.Add(guid, key);
                }
                profile.AesKeys = keys; profile.AesEndpoint = endpoint; Close(true);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException) { error.Text = exception.Message; }
            return Task.CompletedTask;
        }), refresh);
        buttons.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 400 }); Content = panel;
    }
}

internal sealed class ExportWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private readonly DesktopSettings _options;
    private static readonly string[] OptionNames =
    [
        nameof(DesktopSettings.OutputDirectory),
        nameof(DesktopSettings.MeshFormat), nameof(DesktopSettings.MeshQuality), nameof(DesktopSettings.NaniteFormat),
        nameof(DesktopSettings.SocketFormat), nameof(DesktopSettings.CompressionFormat), nameof(DesktopSettings.IncludeDisabledMeshSections),
        nameof(DesktopSettings.TextureFormat), nameof(DesktopSettings.AnimatedTextureFormat), nameof(DesktopSettings.TextureQuality),
        nameof(DesktopSettings.PngCompressionLevel), nameof(DesktopSettings.TgaRleCompression), nameof(DesktopSettings.ExportHdrTexturesAsHdr),
        nameof(DesktopSettings.ExportAllTextureMips), nameof(DesktopSettings.MaterialDepth), nameof(DesktopSettings.ExportMaterials), nameof(DesktopSettings.ExportMorphTargets)
    ];

    internal async Task CheckSession(MainWindow main, AssetEntry texture)
    {
        var tabs = this.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = 1;
        await Task.Delay(50);
        var enable = this.GetVisualDescendants().OfType<CheckBox>().Single(item => Equals(item.Content, "Override default export options for this session"));
        enable.IsChecked = true;
        var format = this.GetVisualDescendants().OfType<ComboBox>().Single(item => Equals(item.Tag, nameof(DesktopSettings.TextureFormat)));
        format.SelectedItem = ETextureFormat.Webp;
        var destination = Path.Combine(main.Settings.OutputDirectory, "SessionOverride");
        var directory = this.GetVisualDescendants().OfType<TextBox>().Single(item => Equals(item.Tag, nameof(DesktopSettings.OutputDirectory)));
        directory.Text = destination;
        if (_options.TextureFormat != ETextureFormat.Webp || main.Settings.TextureFormat != ETextureFormat.Png)
            throw new InvalidOperationException("Session export options did not stay isolated from the default settings.");
        await Task.Delay(50);
        Capture("export-options-smoke-test.png");
        tabs.SelectedIndex = 0;
        await Task.Delay(50);
        var queue = this.GetVisualDescendants().OfType<ListBox>().Single(list => list.ItemsSource is IReadOnlyList<CUE4Parse_Conversion.Exporters.IExporter>);
        queue.SelectedIndex = 0;
        ((MenuItem)queue.ContextMenu!.Items[0]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        if (main.Workspace.Exports.TotalQueued != 0) throw new InvalidOperationException("Removing the queued asset failed.");
        main.Workspace.Queue(texture, "Texture", default);
        var export = this.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Export"));
        export.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The session export did not finish.");
        } while (_cancellation is not null || main.Workspace.Exports.IsRunning);
        if (main.Workspace.Exports.TotalQueued != 0 || !Directory.Exists(destination) || !Directory.EnumerateFiles(destination, "*.webp", SearchOption.AllDirectories).Any() || main.Settings.OutputDirectory == destination)
            throw new InvalidOperationException("The session did not export with its selected texture format.");
        Capture("export-log-smoke-test.png");

        void Capture(string name)
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
            bitmap.Render(this); bitmap.Save(Path.Combine(DesktopSettings.DataDirectory, name));
        }
    }

    public ExportWindow(MainWindow main)
    {
        Title = "Export Session"; Width = 800; Height = 650; MinWidth = 620; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _options = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(main.Settings, DesktopSettings.JsonOptions), DesktopSettings.JsonOptions)!;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var count = new TextBlock { Margin = new Thickness(4) };
        var queue = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<CUE4Parse_Conversion.Exporters.IExporter>((item, _) =>
                new TextBlock { Text = item?.ObjectName + " — " + item?.PackageDirectory, Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap })
        };
        var logs = new System.Collections.ObjectModel.ObservableCollection<string>();
        var logList = new ListBox { ItemsSource = logs, FontFamily = DesktopFonts.Code };
        var remove = Ui.Menu("Remove from Queue", () =>
        {
            if (_cancellation is null && queue.SelectedItem is CUE4Parse_Conversion.Exporters.IExporter item)
                main.Workspace.Exports.Remove(item.ObjectPath);
            queue.IsVisible = true; logList.IsVisible = false;
            RefreshQueue(); return Task.CompletedTask;
        });
        queue.ContextMenu = new ContextMenu { Items = { remove } };
        queue.ContextMenu.Opening += (_, _) => remove.IsEnabled = _cancellation is null && queue.SelectedItem is not null;
        var logPanel = new Grid(); logPanel.Children.Add(queue); logPanel.Children.Add(logList); logList.IsVisible = false;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Export Logs", Content = logPanel });
        var optionPanel = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
        var useOptions = new CheckBox { Content = "Override default export options for this session" };
        var fields = new StackPanel { IsEnabled = false };
        void PopulateOptions()
        {
            fields.Children.Clear();
            foreach (var name in OptionNames)
            {
                var label = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])([A-Z])", " $1");
                var field = SettingsWindow.EditField(fields, _options, name, label, max: name == nameof(DesktopSettings.TextureQuality) ? 100 : name == nameof(DesktopSettings.PngCompressionLevel) ? 9 : 16384);
                if (name == nameof(DesktopSettings.OutputDirectory) && field is TextBox directory && field.Parent is Grid row)
                {
                    var browse = Ui.Button("…", async () =>
                    {
                        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output Directory" });
                        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) directory.Text = path;
                    });
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    Grid.SetColumn(browse, 2); row.Children.Add(browse);
                }
            }
        }
        PopulateOptions();
        useOptions.IsCheckedChanged += (_, _) => fields.IsEnabled = useOptions.IsChecked == true;
        var makeDefault = Ui.Button("Make Default", () =>
        {
            foreach (var name in OptionNames)
            {
                var property = typeof(DesktopSettings).GetProperty(name)!;
                property.SetValue(main.Settings, property.GetValue(_options));
            }
            main.Settings.Save(); return Task.CompletedTask;
        });
        makeDefault.IsEnabled = false;
        useOptions.IsCheckedChanged += (_, _) => makeDefault.IsEnabled = useOptions.IsChecked == true;
        optionPanel.Children.Add(Ui.Row(useOptions, makeDefault));
        optionPanel.Children.Add(fields);
        tabs.Items.Add(new TabItem { Header = "Export Options", Content = new ScrollViewer { Content = optionPanel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } });
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5 };
        var start = Ui.Button("Export", async () =>
        {
            if (!main.Workspace.Exports.HasQueuedItems || _cancellation is not null) return;
            await main.Run("Exporting", async operationToken =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                _cancellation = cancellation;
                queue.IsVisible = false; logList.IsVisible = true; logs.Clear(); tabs.SelectedIndex = 0;
                try
                {
                    var reporter = new Progress<ExportProgress>(value => { count.Text = value.DisplayText; progress.Value = value.Percentage * 100; });
                    var settings = useOptions.IsChecked == true ? _options : main.Settings;
                    main.Workspace.ConfigureQueuedExports(main.Settings, overrideDirectory: useOptions.IsChecked == true);
                    var results = await Task.Run(() => main.Workspace.Exports.RunAsync(settings.OutputDirectory,
                        settings.ExportOptions(main.Workspace.Profile?.TexturePlatform ?? CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform.DesktopMobile), reporter, cancellation.Token));
                    foreach (var result in results)
                        logs.Add($"{(result.Success ? "✓" : "✗")} {result.ObjectPath}{(result.Error is null ? "" : " — " + result.Error.Message)}");
                    count.Text = $"{results.Count(x => x.Success):N0} succeeded · {results.Count(x => !x.Success):N0} failed";
                    main.AppendLog(count.Text);
                }
                catch (OperationCanceledException) { count.Text = "Export canceled"; }
                finally { _cancellation = null; }
            });
        });
        var footer = Ui.Row(Ui.Button("Clear Queue", () =>
        {
            if (_cancellation is null) { main.Workspace.Exports.Clear(); queue.IsVisible = true; logList.IsVisible = false; RefreshQueue(); }
            return Task.CompletedTask;
        }), Ui.Button("Cancel", () => { _cancellation?.Cancel(); return Task.CompletedTask; }), start,
        Ui.Button("OK", () => { if (_cancellation is null) Close(); return Task.CompletedTask; }));
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(count, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom); DockPanel.SetDock(progress, Dock.Bottom);
        panel.Children.Add(count); panel.Children.Add(footer); panel.Children.Add(progress); panel.Children.Add(tabs); Content = panel;
        void RefreshQueue()
        {
            queue.ItemsSource = main.Workspace.Exports.QueuedItems;
            if (!logList.IsVisible) count.Text = $"{main.Workspace.Exports.TotalQueued:N0} in queue";
        }
        void OnLog(Serilog.Events.LogEvent entry) => Dispatcher.UIThread.Post(() => logs.Add($"[{entry.Timestamp:HH:mm:ss.fff}] {entry.RenderMessage()}"));
        void OnQueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ExportSession.TotalQueued)) Dispatcher.UIThread.Post(RefreshQueue);
        }
        main.Workspace.Exports.PropertyChanged += OnQueueChanged;
        Editor.ImGuiSink.Instance.OnExporterLogEvent += OnLog;
        RefreshQueue();
        Closing += (_, e) => { if (_cancellation is not null) { e.Cancel = true; _cancellation.Cancel(); } };
        Closed += (_, _) =>
        {
            main.Workspace.Exports.PropertyChanged -= OnQueueChanged;
            Editor.ImGuiSink.Instance.OnExporterLogEvent -= OnLog;
        };
    }
}

internal sealed class BackupWindow : Window
{
    public BackupWindow(MainWindow main)
    {
        Title = "Backup Manager"; Width = 900; Height = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var list = new ListBox(); var output = new TextBox { IsReadOnly = true, AcceptsReturn = true };
        var backupDirectory = Path.Combine(main.Settings.OutputDirectory, "Backups");
        Directory.CreateDirectory(backupDirectory);
        void Refresh() => list.ItemsSource = Directory.GetFiles(backupDirectory).Where(x => Path.GetExtension(x) is ".json" or ".fbkp").OrderDescending().ToArray();
        var bar = Ui.Row(Ui.Button("Create Backup", async () =>
        {
            var path = Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".fbkp");
            await main.Run("Creating backup", token => Task.Run(() => ArchiveBackup.Write(path, main.Workspace.Provider?.Files.Values ?? [], token), token)); Refresh();
        }), Ui.Button("Compare", async () =>
        {
            if (list.SelectedItem is not string file) return;
            var before = await Task.Run(() => ArchiveBackup.Read(file));
            var now = main.Workspace.Files.ToDictionary(x => x.Path, x => x.File, StringComparer.OrdinalIgnoreCase);
            output.Text = string.Join('\n', now.Where(x => !before.ContainsKey(x.Key)).Select(x => "+ " + x.Key)
                .Concat(before.Where(x => !now.ContainsKey(x.Key)).Select(x => "- " + x.Key))
                .Concat(now.Where(x => before.TryGetValue(x.Key, out var entry) && ArchiveBackup.Changed(entry, x.Value)).Select(x => "~ " + x.Key)));
            if (output.Text.Length == 0) output.Text = "No added, removed, or modified files.";
        }), Ui.Button("Import Backup", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [new FilePickerFileType("FModel backup") { Patterns = ["*.fbkp", "*.json"] }] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } source) return;
            await Task.Run(() => ArchiveBackup.Read(source));
            var destination = Path.Combine(backupDirectory, Path.GetFileName(source));
            if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.Ordinal)) File.Copy(source, destination, false);
            Refresh();
        }), Ui.Button("Open Backup Folder", () => { MainWindow.OpenUrl(backupDirectory); return Task.CompletedTask; }));
        DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*") }; Grid.SetColumn(output, 1); grid.Children.Add(list); grid.Children.Add(output); panel.Children.Add(grid);
        Content = panel; Refresh();
    }
}
