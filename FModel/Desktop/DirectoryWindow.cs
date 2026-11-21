using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CUE4Parse.UE4.Versions;

namespace FModel.Desktop;

internal sealed class DirectoryWindow : Window
{
    public DirectoryWindow(DesktopSettings settings)
    {
        Title = "Directory Selector"; Width = 700; SizeToContent = SizeToContent.Height;
        MaxHeight = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel();
        var body = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Instruction", FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(new TextBlock { Text = "We like to make things as simple as possible. Choose between the detected games or manually add your own. FModel will use this information to automatically find archives to load and decrypt if needed. Make sure to not skip this step!", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var liveGames = new[]
        {
            new GameProfile { Name = "Fortnite [LIVE]", Directory = "fortnite-live.manifest", LiveGame = "FortniteLive", Version = EGame.GAME_UE6_0,
                AesEndpoint = RemoteEndpoint.Default("Fortnite", false), MappingEndpoint = RemoteEndpoint.Default("Fortnite", true), UseRemoteMappings = true },
            new GameProfile { Name = "VALORANT [LIVE]", Directory = "valorant-live.manifest", LiveGame = "ValorantLive", Version = EGame.GAME_Valorant,
                AesEndpoint = RemoteEndpoint.Default("VALORANT", false), MappingEndpoint = RemoteEndpoint.Default("VALORANT", true), UseRemoteMappings = true }
        };
        var profiles = new ObservableCollection<GameProfile>(settings.Games.Concat(liveGames).Concat(GameDirectoryResolver.DiscoverInstalled()).DistinctBy(x => x.Directory));
        var games = new ComboBox { ItemsSource = profiles, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<GameProfile>((profile, _) => new TextBlock { Text = profile?.Name }) };
        var versions = Enum.GetValues<EGame>().Distinct().ToArray();
        AutoCompleteBox VersionBox() => new() { ItemsSource = versions, FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0, IsTextCompletionEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        var version = VersionBox();
        var directory = new TextBox();
        var form = new StackPanel { Spacing = 5, Margin = new Thickness(5) };
        void Row(Panel parent, string label, Control control, Control? button = null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("105,*,30"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1); row.Children.Add(control);
            if (button is not null) { Grid.SetColumn(button, 2); button.Margin = new Thickness(5, 0, 0, 0); row.Children.Add(button); }
            else Grid.SetColumnSpan(control, 2);
            parent.Children.Add(row);
        }
        async Task Browse(TextBox destination)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select game directory" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) destination.Text = path;
        }
        Row(form, "Detected Game", games);
        Row(form, "UE Versions", version);
        var browseDirectory = Ui.Button("…", () => Browse(directory));
        Row(form, "Directory", directory, browseDirectory);
        games.SelectionChanged += (_, _) =>
        {
            if (games.SelectedItem is not GameProfile profile) return;
            version.SelectedItem = profile.Version; version.Text = profile.Version.ToString(); directory.Text = profile.Directory;
            directory.IsEnabled = browseDirectory.IsEnabled = profile.LiveGame.Length == 0;
        };
        form.Children.Add(new SectionHeader { Text = "ADD UNDETECTED GAME" });
        form.Children.Add(new TextBlock { Text = "Drag & drop folder to quickly configure new game", HorizontalAlignment = HorizontalAlignment.Center });
        var manual = new StackPanel { Spacing = 5 };
        var name = new TextBox(); var manualDirectory = new TextBox(); var manualVersion = VersionBox(); manualVersion.Text = EGame.GAME_UE4_LATEST.ToString();
        Row(manual, "Name", name); Row(manual, "UE Versions", manualVersion);
        Row(manual, "Directory", manualDirectory, Ui.Button("…", () => Browse(manualDirectory)));
        manual.Children.Add(Ui.Button("Add", () =>
        {
            if (!System.IO.Directory.Exists(manualDirectory.Text)) throw new DirectoryNotFoundException("Select a game directory first.");
            var profile = GameDirectoryResolver.Detect(manualDirectory.Text!);
            if (!string.IsNullOrWhiteSpace(name.Text)) profile.Name = name.Text;
            if (Enum.TryParse<EGame>(manualVersion.Text, out var selectedVersion) && selectedVersion != EGame.GAME_UE4_LATEST) profile.Version = selectedVersion;
            var existing = profiles.FirstOrDefault(x => x.Directory == profile.Directory);
            if (existing is null) profiles.Add(profile);
            games.SelectedItem = existing ?? profile;
            return Task.CompletedTask;
        }));
        var expander = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, Content = manual };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
#pragma warning disable CS0618
            var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
#pragma warning restore CS0618
            if (path is null || !System.IO.Directory.Exists(path)) return;
            var detected = GameDirectoryResolver.Detect(path);
            name.Text = detected.Name; manualDirectory.Text = path; manualVersion.Text = detected.Version.ToString();
            expander.IsExpanded = true; e.Handled = true;
        });
        form.Children.Add(expander);
        body.Children.Add(new Border { Background = Brush.Parse("#32323F"), BorderBrush = Brush.Parse("#272730"), BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = form });
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Background = Brush.Parse("#3D3D4C"), Margin = new Thickness(0, 10, 0, 0) };
        var clear = Ui.Button("Clear", () =>
        {
            foreach (var missing in profiles.Where(x => x.LiveGame.Length == 0 && !System.IO.Directory.Exists(x.Directory)).ToArray()) { profiles.Remove(missing); settings.Games.Remove(missing); }
            return Task.CompletedTask;
        });
        clear.Margin = new Thickness(20, 12); footer.Children.Add(clear);
        var ok = Ui.Button("OK", () =>
        {
            if (games.SelectedItem is not GameProfile profile) throw new InvalidOperationException("Select a game or add an undetected game.");
            if (profile.LiveGame.Length == 0 && !System.IO.Directory.Exists(directory.Text)) throw new DirectoryNotFoundException("Select an existing game directory.");
            if (!Enum.TryParse<EGame>(version.Text, out var selectedVersion)) throw new InvalidOperationException("Select a UE version.");
            if (profile.LiveGame.Length == 0) profile.Directory = Path.GetFullPath(directory.Text!);
            profile.Version = selectedVersion; Close(profile); return Task.CompletedTask;
        });
        ok.MinWidth = 78; ok.Margin = new Thickness(6, 12); Grid.SetColumn(ok, 2); footer.Children.Add(ok);
        var cancel = Ui.Button("Cancel", () => { Close(); return Task.CompletedTask; }); cancel.MinWidth = 78; cancel.Margin = new Thickness(6, 12); Grid.SetColumn(cancel, 3); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(new ScrollViewer { Content = body }); Content = root;
        games.SelectedItem = profiles.FirstOrDefault(x => x.Directory == settings.LastDirectory) ?? profiles.FirstOrDefault();
    }
}
