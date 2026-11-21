using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CUE4Parse.UE4.VirtualFileSystem;

namespace FModel.Desktop;

internal sealed class SearchWindow : Window
{
    private readonly TabControl _tabs = new();
    private readonly SearchPage _paths;
    private readonly SearchPage _references;
    private readonly TextBlock _referencePath = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly MainWindow _owner;
    private AssetEntry? _reference;

    public SearchWindow(MainWindow owner, AssetEntry[] files)
    {
        _owner = owner;
        Title = "Search For Packages";
        Width = 1000; Height = 640; MinWidth = 650; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _paths = new SearchPage(this, owner, files);
        _references = new SearchPage(this, owner, []);
        var referencePanel = new DockPanel();
        var referenceHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(5) };
        referenceHeader.Children.Add(_referencePath);
        var goTo = Ui.Button("Go To", () => { if (_reference is not null) Navigate(_reference); return Task.CompletedTask; });
        Grid.SetColumn(goTo, 1); referenceHeader.Children.Add(goTo);
        DockPanel.SetDock(referenceHeader, Dock.Top);
        referencePanel.Children.Add(referenceHeader);
        referencePanel.Children.Add(_references);
        _tabs.Items.Add(new TabItem { Header = "By Path", Content = _paths });
        _tabs.Items.Add(new TabItem { Header = "References", Content = referencePanel });
        _tabs.SelectedIndex = 0;
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs)) Current.FocusSearch(); };
        Content = new Border { Margin = new Thickness(5), BorderBrush = Brush.Parse("#272730"), BorderThickness = new Thickness(1), Child = _tabs };
        Closed += (_, _) => { _paths.Stop(); _references.Stop(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private SearchPage Current => _tabs.SelectedIndex == 1 ? _references : _paths;
    private void Navigate(AssetEntry asset)
    {
        WindowState = WindowState.Minimized;
        _owner.NavigateToAsset(asset);
    }
    private void ActivateMain()
    {
        WindowState = WindowState.Minimized;
        _owner.Activate();
    }
    public void FocusSearch() { _tabs.SelectedIndex = 0; Restore(); }
    public void FocusReferences() { _tabs.SelectedIndex = 1; Restore(); }
    public void ShowReferences(AssetEntry source, AssetEntry[] references)
    {
        _reference = source;
        _referencePath.Text = source.Path;
        ToolTip.SetTip(_referencePath, source.Path);
        _references.SetFiles(references);
        FocusReferences();
    }
    private void Restore()
    {
        WindowState = WindowState.Normal;
        Activate();
        Current.FocusSearch();
    }

    internal async Task CheckNavigation(AssetEntry asset)
    {
        FocusSearch();
        await _paths.CheckNavigation(asset);
        ShowReferences(asset, [new AssetEntry(asset.File)]);
        await _references.CheckNavigation(asset);
    }

    private sealed class SearchPage : DockPanel
    {
        private AssetEntry[] _files;
        private readonly TextBox _search = new() { Watermark = "Write your pattern and press enter...", MinWidth = 100 };
        private readonly ToggleButton _matchCase = new();
        private readonly ToggleButton _regex = new();
        private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0), FontStyle = FontStyle.Italic };
        private readonly ListBox _results;
        private CancellationTokenSource? _pending;
        private int _sizeSort;
        private bool _closed;

        public SearchPage(SearchWindow window, MainWindow owner, AssetEntry[] files)
        {
            _files = files;
            _results = new ListBox
            {
                SelectionMode = SelectionMode.Multiple,
                ItemTemplate = new FuncDataTemplate<AssetEntry>((asset, _) =>
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,180,90"), Margin = new Thickness(5, 1) };
                    row.Children.Add(new TextBlock { Text = asset?.Path, TextTrimming = TextTrimming.CharacterEllipsis });
                    var archive = new TextBlock { Text = (asset?.File as VfsEntry)?.Vfs.Name ?? "Loose file", TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(archive, 1); row.Children.Add(archive);
                    var size = new TextBlock { Text = asset?.Size, HorizontalAlignment = HorizontalAlignment.Right };
                    Grid.SetColumn(size, 2); row.Children.Add(size);
                    return row;
                })
            };
            MainWindow.SelectRightClickedItem(_results);
            _results.ContextMenu = owner.CreateAssetMenu(() => _results.SelectedItems?.OfType<AssetEntry>().ToArray() ?? [], window.ActivateMain, window.Navigate, includeEmbedded: false);
            _results.DoubleTapped += (_, _) => { if (_results.SelectedItem is AssetEntry asset) window.Navigate(asset); };
            _results.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || _results.SelectedItem is not AssetEntry asset) return;
                window.Navigate(asset); e.Handled = true;
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            _matchCase.Content = AssetIcons.Named("MatchCaseIcon"); ToolTip.SetTip(_matchCase, "Match Case");
            _regex.Content = AssetIcons.Named("RegexIcon"); ToolTip.SetTip(_regex, "Regex");
            var sort = Ui.Button("", async () => { _sizeSort = (_sizeSort + 1) % 3; await Refresh(); });
            sort.Content = AssetIcons.Named("SortIcon"); ToolTip.SetTip(sort, "Sort File Sizes");
            var clear = Ui.Button("", async () => { _search.Text = ""; await Refresh(); });
            clear.Content = AssetIcons.Named("BackspaceIcon"); ToolTip.SetTip(clear, "Clear Search Filter");
            controls.Children.Add(_matchCase); controls.Children.Add(_regex); controls.Children.Add(sort); controls.Children.Add(clear);
            _search.InnerLeftContent = AssetIcons.Named("SearchIcon");
            _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Refresh(); } };
            _matchCase.IsCheckedChanged += async (_, _) => await Refresh();
            _regex.IsCheckedChanged += async (_, _) =>
            {
                _search.Watermark = _regex.IsChecked == true ? "Write your regex pattern and press enter..." : "Write your pattern and press enter...";
                await Refresh();
            };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 5) };
            header.Children.Add(_search); Grid.SetColumn(controls, 1); header.Children.Add(controls);
            Grid.SetColumn(_status, 2); header.Children.Add(_status);
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,180,90"), Margin = new Thickness(5) };
            columns.Children.Add(new TextBlock { Text = "Path" });
            var archiveTitle = new TextBlock { Text = "Archive" }; Grid.SetColumn(archiveTitle, 1); columns.Children.Add(archiveTitle);
            var sizeTitle = new TextBlock { Text = "Size", HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(sizeTitle, 2); columns.Children.Add(sizeTitle);
            DockPanel.SetDock(header, Dock.Top); Children.Add(header);
            DockPanel.SetDock(columns, Dock.Top); Children.Add(columns);
            Children.Add(_results);
            AttachedToVisualTree += async (_, _) => { if (_results.ItemsSource is null) await Refresh(); };
        }

        public void FocusSearch() { _search.Focus(); _search.SelectAll(); }
        public void SetFiles(AssetEntry[] files) { _files = files; _search.Text = ""; _ = Refresh(); }
        public void Stop() { _closed = true; _pending?.Cancel(); }

        public async Task CheckNavigation(AssetEntry asset)
        {
            _search.Text = asset.Name;
            await Refresh();
            var result = _results.ItemsSource!.Cast<AssetEntry>().FirstOrDefault(x => x.Path == asset.Path)
                ?? throw new InvalidOperationException("Search failed to return the requested asset.");
            _results.SelectedItem = result;
            var menu = _results.ContextMenu!;
            menu.Open(_results);
            foreach (var label in new[] { "Go To", "Extract in New Tab", "Show Metadata", "Find References", "Export", "Copy" })
                if (!menu.Items.OfType<MenuItem>().Any(x => Equals(x.Header, label) && x.IsEnabled))
                    throw new InvalidOperationException("Missing search action " + label);
            var goTo = menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "Go To"));
            menu.Close();
            goTo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        }

        private async Task Refresh()
        {
            if (_closed) return;
            _pending?.Cancel();
            using var operation = new CancellationTokenSource();
            _pending = operation;
            var query = _search.Text ?? "";
            var caseSensitive = _matchCase.IsChecked == true;
            var useRegex = _regex.IsChecked == true;
            var sort = _sizeSort;
            var files = _files;
            _status.Text = "Searching…";
            try
            {
                var matches = await Task.Run(() =>
                {
                    var pattern = useRegex ? new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) : null;
                    var filtered = files.Where(file =>
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        return pattern?.IsMatch(file.Path) ?? file.Path.Contains(query, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    });
                    return (sort switch
                    {
                        1 => filtered.OrderBy(x => x.File.Size),
                        2 => filtered.OrderByDescending(x => x.File.Size),
                        _ => filtered.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    }).ToArray();
                }, operation.Token);
                if (_closed || operation.IsCancellationRequested) return;
                _results.ItemsSource = matches;
                _status.Text = $"{matches.Length:N0} Loaded Packages ({new[] { "None", "Ascending", "Descending" }[sort]})";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
            {
                if (!_closed && !operation.IsCancellationRequested) { _results.ItemsSource = Array.Empty<AssetEntry>(); _status.Text = error.Message; }
            }
            finally { if (ReferenceEquals(_pending, operation)) _pending = null; }
        }
    }
}
