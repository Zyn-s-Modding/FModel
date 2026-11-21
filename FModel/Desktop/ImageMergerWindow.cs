using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace FModel.Desktop;

internal sealed class ImageMergerWindow : Window
{
    private readonly List<(string Name, SKBitmap Bitmap)> _images = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly NumericUpDown _columns = new() { Minimum = 1, Maximum = 20, Value = 2, Width = 90 };
    private readonly NumericUpDown _margin = new() { Minimum = 0, Maximum = 50, Value = 0, Width = 90 };
    private readonly TextBlock _dimensions = new();
    private Bitmap? _previewBitmap;
    private byte[]? _png;

    public ImageMergerWindow()
    {
        Title = "Image Merger"; Width = 1050; Height = 680; MinWidth = 800; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(12) };
        var left = new DockPanel();
        var commands = new StackPanel { Spacing = 8 };
        commands.Children.Add(Ui.Row(new TextBlock { Text = "Images Per Row", VerticalAlignment = VerticalAlignment.Center }, _columns));
        commands.Children.Add(Ui.Row(new TextBlock { Text = "Margin Between Images", VerticalAlignment = VerticalAlignment.Center }, _margin));
        commands.Children.Add(Ui.Row(Ui.Button("Add", AddFiles),
            Ui.Button("↑", () => Move(-1)), Ui.Button("↓", () => Move(1)),
            Ui.Button("Remove", () =>
            {
                foreach (var index in _list.SelectedItems?.Cast<string>().Select(x => Array.IndexOf(_list.ItemsSource!.Cast<string>().ToArray(), x)).OrderDescending().ToArray() ?? [])
                {
                    _images[index].Bitmap.Dispose();
                    _images.RemoveAt(index);
                }
                Refresh(); return Task.CompletedTask;
            }), Ui.Button("Clear", () => { Clear(); Refresh(); return Task.CompletedTask; })));
        commands.Children.Add(Ui.Row(Ui.Button("Open Image", () =>
        {
            if (_png is null) return Task.CompletedTask;
            var image = new PreviewImage("Merged", _png, (int)(_previewBitmap?.Size.Width ?? 0), (int)(_previewBitmap?.Size.Height ?? 0));
            var document = new ImageDocument(image, compact: false);
            var window = new Window { Title = "Merged (Image)", Width = 900, Height = 700, Content = document };
            window.Closed += (_, _) => document.Dispose(); window.Show(this); return Task.CompletedTask;
        }), Ui.Button("Copy", () => { if (_png is { } png) ImageDocument.CopyPng(png); return Task.CompletedTask; }),
        Ui.Button("Save Image", Save)));
        DockPanel.SetDock(commands, Dock.Bottom); left.Children.Add(commands); left.Children.Add(_list);
        var right = new DockPanel(); DockPanel.SetDock(_dimensions, Dock.Bottom); right.Children.Add(_dimensions);
        right.Children.Add(new Border { BorderBrush = Brush.Parse("#525463"), BorderThickness = new Thickness(1), Background = Brush.Parse("#272833"), Child = _preview });
        Grid.SetColumn(right, 2); layout.Children.Add(left); layout.Children.Add(right); Content = layout;
        _columns.ValueChanged += (_, _) => DrawPreview(); _margin.ValueChanged += (_, _) => DrawPreview();
        Closed += (_, _) => { Clear(); _previewBitmap?.Dispose(); };
    }

    public void AddImage(string name, byte[] png)
    {
        var bitmap = SKBitmap.Decode(png) ?? throw new IOException($"Could not decode {name}");
        _images.Add((name, bitmap));
        Refresh();
    }

    private async Task AddFiles()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        foreach (var file in files)
        {
            await using var input = await file.OpenReadAsync(); using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
            AddImage(file.Name, buffer.ToArray());
        }
    }

    private Task Move(int direction)
    {
        var index = _list.SelectedIndex;
        var next = index + direction;
        if (index < 0 || next < 0 || next >= _images.Count) return Task.CompletedTask;
        (_images[index], _images[next]) = (_images[next], _images[index]);
        Refresh(); _list.SelectedIndex = next;
        return Task.CompletedTask;
    }

    private void Clear()
    {
        foreach (var image in _images) image.Bitmap.Dispose();
        _images.Clear();
    }

    private void Refresh()
    {
        _list.ItemsSource = _images.Select((x, i) => $"{i + 1}. {x.Name} — {x.Bitmap.Width} × {x.Bitmap.Height}").ToArray();
        DrawPreview();
    }

    private void DrawPreview()
    {
        _preview.Source = null; _previewBitmap?.Dispose(); _previewBitmap = null; _png = null;
        if (_images.Count == 0) { _dimensions.Text = ""; return; }
        try
        {
            _png = Compose(_images.Select(x => x.Bitmap).ToArray(), (int)(_columns.Value ?? 1), (int)(_margin.Value ?? 0));
            _previewBitmap = new Bitmap(new MemoryStream(_png)); _preview.Source = _previewBitmap;
            _dimensions.Text = $"{_previewBitmap.PixelSize.Width} × {_previewBitmap.PixelSize.Height}";
        }
        catch (InvalidOperationException error) { _dimensions.Text = error.Message; }
    }

    internal static byte[] Compose(IReadOnlyList<SKBitmap> images, int columns, int margin)
    {
        if (images.Count == 0) throw new InvalidOperationException("Add an image first.");
        columns = Math.Clamp(columns, 1, images.Count); margin = Math.Max(0, margin);
        var positions = new List<SKPointI>();
        var x = 0; var y = 0; var rowHeight = 0; var width = 0;
        for (var i = 0; i < images.Count; i++)
        {
            if (i > 0 && i % columns == 0) { y = checked(y + rowHeight + margin); x = 0; rowHeight = 0; }
            positions.Add(new SKPointI(x, y));
            x = checked(x + images[i].Width + margin);
            width = Math.Max(width, x - margin); rowHeight = Math.Max(rowHeight, images[i].Height);
        }
        var height = checked(y + rowHeight);
        if ((long)width * height > 64L * 1024 * 1024) throw new InvalidOperationException("The merged image exceeds 64 megapixels. Remove images or reduce the number of images per row.");
        using var bitmap = new SKBitmap(width, height); using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        for (var i = 0; i < images.Count; i++) canvas.DrawBitmap(images[i], positions[i].X, positions[i].Y);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private async Task Save()
    {
        if (_png is not { } png) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = "Merged.png", DefaultExtension = "png" });
        if (file is null) return;
        await using var output = await file.OpenWriteAsync(); output.SetLength(0); await output.WriteAsync(png);
    }
}
