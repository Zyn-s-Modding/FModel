using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FModel.Desktop;

public sealed class SectionHeader : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<SectionHeader, string>(nameof(Text), "");
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public SectionHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(0, 8) };
        var text = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        foreach (var column in new[] { 0, 2 })
        {
            var line = new Border { Height = 1, Background = Brush.Parse("#4A4A5E"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, column); grid.Children.Add(line);
        }
        Content = grid;
    }
}

public sealed class InformationPanel : UserControl
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            var rows = new StackPanel { Spacing = 2 };
            foreach (var line in value.Split('\n'))
            {
                var delimiter = line.IndexOf(": ", StringComparison.Ordinal);
                if (delimiter < 0) { rows.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap }); continue; }
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock { Text = line[(delimiter + 2)..], TextTrimming = TextTrimming.CharacterEllipsis });
                var label = new TextBlock { Text = line[..delimiter], Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(label, 1); row.Children.Add(label); rows.Children.Add(row);
            }
            Content = rows;
        }
    }
}
