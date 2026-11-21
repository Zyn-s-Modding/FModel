using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace FModel.Desktop;

public sealed class AssetGrid : UserControl
{
    private readonly ItemsRepeater _items;
    private readonly ScrollViewer _scroll;
    private object[] _entries = [];
    private readonly HashSet<object> _selected = [];
    private int _anchor;
    public Action<object>? OpenEntry;
    public Action<object?>? SelectionChanged;
    internal ThumbnailService? Thumbnails { get; set; }
    public IReadOnlyCollection<object> SelectedEntries => _selected;

    public AssetGrid()
    {
        Focusable = true;
        _items = new ItemsRepeater
        {
            Layout = new UniformGridLayout { MinItemWidth = 136, MinItemHeight = 200, MinColumnSpacing = 8, MinRowSpacing = 8,
                ItemsStretch = UniformGridLayoutItemsStretch.None, Orientation = Orientation.Horizontal },
            ItemTemplate = new FuncDataTemplate<object>((entry, _) => new Tile(this, entry!))
        };
        _scroll = new ScrollViewer { Content = _items, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = _scroll;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _selected.FirstOrDefault() is { } entry) { OpenEntry?.Invoke(entry); e.Handled = true; }
            if (e.Key == Key.A && (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
            { foreach (var item in _entries) _selected.Add(item); RefreshSelection(); e.Handled = true; }
            var columns = Math.Max(1, (int)(Bounds.Width / 144));
            var next = e.Key switch { Key.Left => _anchor - 1, Key.Right => _anchor + 1, Key.Up => _anchor - columns, Key.Down => _anchor + columns, Key.Home => 0, Key.End => _entries.Length - 1, _ => -1 };
            if (next >= 0 && next < _entries.Length)
            {
                Select(_entries[next], e.KeyModifiers);
                ScrollTo(next);
                e.Handled = true;
            }
        };
    }

    public void SetEntries(IEnumerable<object> entries)
    {
        _selected.Clear(); _anchor = 0;
        _entries = entries.ToArray();
        _items.ItemsSource = _entries;
    }

    public void SelectEntry(object entry)
    {
        var index = Array.IndexOf(_entries, entry);
        if (index < 0) return;
        Select(entry, KeyModifiers.None);
        ScrollTo(index);
    }

    private void ScrollTo(int index)
    {
        UpdateLayout();
        var columns = Math.Max(1, (int)((_scroll.Viewport.Width + 8) / 144));
        _scroll.Offset = new Vector(0, index / columns * 208);
        UpdateLayout();
    }

    internal void CheckSelectedEntryVisible()
    {
        if (_selected.FirstOrDefault() is not { } selected) throw new InvalidOperationException("No explorer package is selected.");
        var index = Array.IndexOf(_entries, selected);
        var element = _items.TryGetElement(index) ?? throw new InvalidOperationException($"The selected package has no visible explorer tile. Index {index}, offset {_scroll.Offset}, viewport {_scroll.Viewport}, extent {_scroll.Extent}, realized {string.Join(',', _items.GetVisualDescendants().OfType<Tile>().Select(tile => _items.GetElementIndex(tile)))}.");
        var position = element.TranslatePoint(default, _scroll) ?? throw new InvalidOperationException("The selected explorer tile is detached.");
        if (position.Y < -1 || position.Y + element.Bounds.Height > _scroll.Viewport.Height + 1)
            throw new InvalidOperationException($"The selected package is outside the explorer viewport ({position.Y}, {_scroll.Viewport.Height}).");
    }

    internal void CheckRealizedEntries()
    {
        foreach (var tile in _items.GetVisualDescendants().OfType<Tile>())
        {
            var index = _items.GetElementIndex(tile);
            if (index >= 0 && !ReferenceEquals(_entries[index], tile.Entry))
                throw new InvalidOperationException("Explorer tile content does not match its package index.");
        }
    }

    private void Select(object entry, KeyModifiers modifiers)
    {
        Focus();
        var index = Array.IndexOf(_entries, entry);
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if ((modifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) _selected.Clear();
            for (var i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(_entries[i]);
        }
        else
        {
            if ((modifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) _selected.Clear();
            if (!_selected.Add(entry)) _selected.Remove(entry);
            _anchor = index;
        }
        RefreshSelection();
        SelectionChanged?.Invoke(entry);
    }

    private void RefreshSelection()
    {
        foreach (var tile in _items.GetVisualDescendants().OfType<Tile>()) tile.UpdateSelection();
    }

    private sealed class Tile : Border
    {
        private readonly AssetGrid _grid;
        private object _entry;
        internal object Entry => _entry;
        private readonly Image _preview = new() { Stretch = Stretch.Uniform };
        private CancellationTokenSource? _loading;
        private Bitmap? _bitmap;
        private Avalonia.Controls.Shapes.Path? _fallbackIcon;
        private Border? _typeBar;
        private TextBlock? _typeLabel;
        internal Tile(AssetGrid grid, object entry)
        {
            _grid = grid; _entry = entry;
            Width = 128; Height = 192; BorderThickness = new Thickness(1.5); CornerRadius = new CornerRadius(0);
            Bind(entry);
            DataContextChanged += (_, _) =>
            {
                if (DataContext is AssetEntry or FolderEntry && !ReferenceEquals(DataContext, _entry)) Bind(DataContext);
            };
            PointerPressed += (_, e) =>
            {
                var properties = e.GetCurrentPoint(this).Properties;
                if (!properties.IsRightButtonPressed || !_grid._selected.Contains(_entry)) _grid.Select(_entry, e.KeyModifiers);
            };
            DoubleTapped += (_, _) => _grid.OpenEntry?.Invoke(_entry);
            AttachedToVisualTree += async (_, _) => { UpdateSelection(); await LoadPreview(); };
            DetachedFromVisualTree += (_, _) => ClearPreview(removeImage: false);
        }

        private void Bind(object entry)
        {
            ClearPreview();
            _entry = entry;
            _fallbackIcon = null; _typeBar = null; _typeLabel = null;
            if (entry is FolderEntry folder)
            {
                var folderContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Margin = new Thickness(5) };
                folderContent.Children.Add(AssetIcons.Folder(96));
                folderContent.Children.Add(new TextBlock { Text = folder.Name, FontSize = 13, FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 2) });
                folderContent.Children.Add(new TextBlock { Text = $"Subfolders: {folder.Children.Count}", FontSize = 11, Opacity = 0.8, TextAlignment = TextAlignment.Center });
                folderContent.Children.Add(new TextBlock { Text = $"Assets: {folder.Files.Count}", FontSize = 11, Opacity = 0.8, TextAlignment = TextAlignment.Center });
                Child = folderContent;
            }
            else if (entry is AssetEntry asset)
            {
                var panel = new Grid { RowDefinitions = new RowDefinitions("128,2,*,Auto"), ClipToBounds = true, Background = Brush.Parse("#323341") };
                var picture = new Grid { Background = Brush.Parse("#1F2029") };
                var icon = AssetIcons.For(asset); picture.Children.Add(icon); picture.Children.Add(_preview);
                _fallbackIcon = icon as Avalonia.Controls.Shapes.Path;
                panel.Children.Add(picture);
                var bar = new Border { Background = (icon as Avalonia.Controls.Shapes.Path)?.Fill ?? Brushes.LightGray };
                _typeBar = bar;
                Grid.SetRow(bar, 1); panel.Children.Add(bar);
                var name = new TextBlock { Text = Path.GetFileNameWithoutExtension(asset.Name), FontSize = 13, FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(5) };
                Grid.SetRow(name, 2); panel.Children.Add(name);
                var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(5, 0, 5, 5) };
                _typeLabel = new TextBlock { Text = asset.ResolvedAssetType ?? asset.Type, FontSize = 9, Foreground = Brush.Parse("#8E8E8E"), TextTrimming = TextTrimming.CharacterEllipsis };
                footer.Children.Add(_typeLabel);
                var size = new TextBlock { Text = asset.Size, FontSize = 9, Foreground = Brush.Parse("#8E8E8E") };
                Grid.SetColumn(size, 1); footer.Children.Add(size); Grid.SetRow(footer, 3); panel.Children.Add(footer);
                Child = panel;
            }
            ToolTip.SetTip(this, entry switch { AssetEntry file => file.Path, FolderEntry directory => directory.Path, _ => "" });
            UpdateSelection();
            if (this.GetVisualRoot() is not null) _ = LoadPreview();
        }

        private void ClearPreview(bool removeImage = true)
        {
            _loading?.Cancel(); _loading?.Dispose(); _loading = null;
            _preview.Source = null; _bitmap?.Dispose(); _bitmap = null;
            // A recycled tile can become a folder or another asset type.
            if (removeImage && _preview.Parent is Panel panel) panel.Children.Remove(_preview);
        }

        private async Task LoadPreview()
        {
            if (_entry is not AssetEntry item || _grid.Thumbnails is not { } service) return;
            _loading?.Cancel(); _loading?.Dispose();
            _loading = new CancellationTokenSource();
            var token = _loading.Token;
            try
            {
                var bytes = await service.Load(item, token);
                if (token.IsCancellationRequested || !ReferenceEquals(item, _entry)) return;
                if (_typeLabel is not null) _typeLabel.Text = item.ResolvedAssetType ?? item.Type;
                if (_fallbackIcon is not null && AssetIcons.For(item) is Avalonia.Controls.Shapes.Path resolved)
                {
                    _fallbackIcon.Data = resolved.Data; _fallbackIcon.Fill = resolved.Fill;
                    _fallbackIcon.IsVisible = bytes is null;
                    if (_typeBar is not null) _typeBar.Background = resolved.Fill;
                }
                if (bytes is null) return;
                _bitmap = new Bitmap(new MemoryStream(bytes));
                _preview.Source = _bitmap;
            }
            catch (OperationCanceledException) { }
        }

        internal void UpdateSelection()
        {
            Background = Brush.Parse(_grid._selected.Contains(_entry) ? "#3B3D4A" : "#32323F");
            BorderBrush = Brush.Parse(_grid._selected.Contains(_entry) ? "#206BD4" : "Transparent");
        }
    }
}
