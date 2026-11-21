using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;

namespace FModel.Desktop;

internal sealed class ImageDocument : UserControl, IDisposable
{
    private Bitmap _bitmap;
    private readonly Image _picture;
    private readonly ScrollViewer _scroll;
    private Bitmap? _opaque;
    private DispatcherTimer? _animation;
    private byte[] _currentData;
    private bool _ignoreAlpha;
    internal bool IsAnimationPlaying => _animation?.IsEnabled == true;

    internal async Task CheckAnimation()
    {
        var initial = _currentData;
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (ReferenceEquals(initial, _currentData) && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        if (!IsAnimationPlaying || ReferenceEquals(initial, _currentData)) throw new InvalidOperationException("The animated image did not advance its frame.");
    }

    public ImageDocument(PreviewImage image, Action? inspect = null, bool compact = true)
    {
        _currentData = image.Data;
        _bitmap = new Bitmap(new MemoryStream(image.Data));
        _picture = new Image { Source = _bitmap, Stretch = Stretch.Uniform };
        var frame = new Border { BorderBrush = Brush.Parse("#272730"), BorderThickness = new Thickness(1), Margin = new Thickness(8), Child = _picture,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _scroll = new ScrollViewer { Content = frame, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var panel = new DockPanel { Background = Brush.Parse("#262630") };
        if (compact)
        {
            var header = new StackPanel { Margin = new Thickness(10, 6, 8, 8), Spacing = 1 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = image.Name, FontWeight = FontWeight.Bold, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            if (inspect is not null)
            {
                var button = Ui.Button("", () => { inspect(); return Task.CompletedTask; });
                button.Classes.Add("icon"); button.Classes.Add("toolbar"); button.Content = AssetIcons.Named("TextureInspectorIcon");
                ToolTip.SetTip(button, "Open in Texture Inspector"); Grid.SetColumn(button, 1); row.Children.Add(button);
            }
            header.Children.Add(row);
            header.Children.Add(new TextBlock { Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height}px", FontSize = 11, Foreground = Brush.Parse("#8E8E8E") });
            DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        }
        var menu = new ContextMenu();
        if (inspect is not null) menu.Items.Add(Ui.Menu("Open in Texture Inspector", () => { inspect(); return Task.CompletedTask; }));
        menu.Items.Add(Ui.Menu("Open in New Window", () => { OpenImage(); return Task.CompletedTask; }));
        menu.Items.Add(Ui.Menu("Copy Image", () => { CopyPng(_currentData); return Task.CompletedTask; }));
        menu.Items.Add(Ui.Menu("Export Image", async () =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { SuggestedFileName = Path.GetFileNameWithoutExtension(image.Name) + "." + image.EncodedFormat, DefaultExtension = image.EncodedFormat });
            if (file is null) return;
            await using var output = await file.OpenWriteAsync(); output.SetLength(0); await output.WriteAsync(image.EncodedData ?? image.Data);
        }));
        menu.Items.Add(new Separator());
        var alpha = new MenuItem { Header = "Ignore Alpha in Preview", ToggleType = MenuItemToggleType.CheckBox };
        alpha.Click += (_, _) => { _ignoreAlpha = alpha.IsChecked; ApplyAlpha(); };
        menu.Items.Add(alpha); ContextMenu = menu;
        if (compact) _picture.DoubleTapped += (_, _) => OpenImage();
        panel.Children.Add(_scroll); Content = panel;
        _scroll.SizeChanged += (_, _) =>
        {
            var scale = Math.Min(1, Math.Min(Math.Max(1, _scroll.Bounds.Width - 18) / _bitmap.Size.Width,
                Math.Max(1, _scroll.Bounds.Height - 18) / _bitmap.Size.Height));
            _picture.Width = _bitmap.Size.Width * scale; _picture.Height = _bitmap.Size.Height * scale;
        };
        if (!compact) AddMagnifier(panel);
        if (image.Frames is { Count: > 1 } frames)
        {
            var index = 0;
            _animation = new DispatcherTimer { Interval = frames[0].Duration };
            _animation.Tick += (_, _) =>
            {
                index = (index + 1) % frames.Count;
                _currentData = frames[index].Data;
                var previous = _bitmap; _bitmap = new Bitmap(new MemoryStream(_currentData));
                _opaque?.Dispose(); _opaque = null; ApplyAlpha(); previous.Dispose();
                _animation.Interval = frames[index].Duration;
            };
            AttachedToVisualTree += (_, _) => _animation.Start();
            DetachedFromVisualTree += (_, _) => _animation.Stop();
        }

        void OpenImage()
        {
            var document = new ImageDocument(image, inspect, compact: false);
            var window = Ui.Dialog(image.Name, Math.Clamp(_bitmap.PixelSize.Width + 50, 450, 1300), Math.Clamp(_bitmap.PixelSize.Height + 80, 350, 900));
            window.Content = document; window.Closed += (_, _) => document.Dispose();
            if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        }
    }

    private void ApplyAlpha()
    {
        if (_ignoreAlpha && _opaque is null)
        {
            using var source = SKBitmap.Decode(_currentData);
            using var opaque = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++) opaque.SetPixel(x, y, source.GetPixel(x, y).WithAlpha(255));
            using var data = opaque.Encode(SKEncodedImageFormat.Png, 100);
            _opaque = new Bitmap(new MemoryStream(data.ToArray()));
        }
        _picture.Source = _ignoreAlpha ? _opaque : _bitmap;
    }

    private void AddMagnifier(Control content)
    {
        var root = new Grid();
        var overlay = new Canvas { IsHitTestVisible = false };
        var lens = new Magnifier(() => _bitmap) { Width = 300, Height = 300, IsVisible = false };
        overlay.Children.Add(lens);
        Content = null; root.Children.Add(content); root.Children.Add(overlay); Content = root;
        void Position(PointerEventArgs e)
        {
            var location = e.GetPosition(_picture);
            var center = e.GetPosition(root);
            lens.Center = new Point(location.X / Math.Max(1, _picture.Bounds.Width) * _bitmap.PixelSize.Width,
                location.Y / Math.Max(1, _picture.Bounds.Height) * _bitmap.PixelSize.Height);
            lens.Scale = _bitmap.PixelSize.Width / Math.Max(1, _picture.Bounds.Width);
            Canvas.SetLeft(lens, center.X - 150); Canvas.SetTop(lens, center.Y - 150); lens.InvalidateVisual();
        }
        _picture.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_picture).Properties.IsLeftButtonPressed) return;
            lens.IsVisible = true; Position(e); e.Pointer.Capture(_picture);
        };
        _picture.PointerMoved += (_, e) => { if (lens.IsVisible) Position(e); };
        _picture.PointerReleased += (_, e) => { lens.IsVisible = false; e.Pointer.Capture(null); };
        _picture.PointerCaptureLost += (_, _) => lens.IsVisible = false;
        _picture.PointerWheelChanged += (_, e) =>
        {
            if (!lens.IsVisible) return;
            lens.Zoom = Math.Clamp(lens.Zoom * Math.Pow(1.2, -e.Delta.Y), 0.02, 1.5); Position(e); e.Handled = true;
        };
    }

    private sealed class Magnifier(Func<Bitmap> bitmap) : Control
    {
        public Point Center;
        public double Scale = 1;
        public double Zoom = 0.7;
        public override void Render(DrawingContext context)
        {
            var destination = new Rect(Bounds.Size);
            var size = destination.Size * (Zoom * Scale);
            using (context.PushGeometryClip(new EllipseGeometry(destination)))
            {
                context.FillRectangle(Brushes.Black, destination);
                context.DrawImage(bitmap(), new Rect(Center.X - size.Width / 2, Center.Y - size.Height / 2, size.Width, size.Height), destination);
            }
            context.DrawEllipse(null, new Pen(Brush.Parse("#585871"), 1), destination.Center, destination.Width / 2, destination.Height / 2);
        }
    }

    [DllImport("FModelAudio")] private static extern int fmodel_copy_png(byte[] data, int length);
    internal static void CopyPng(byte[] data)
    {
        if (fmodel_copy_png(data, data.Length) == 0) throw new IOException("macOS could not copy the image.");
    }
    public void Dispose() { _animation?.Stop(); _picture.Source = null; _opaque?.Dispose(); _bitmap.Dispose(); }
}
