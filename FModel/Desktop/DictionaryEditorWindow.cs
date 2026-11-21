using Avalonia.Controls;
using Avalonia.Layout;
using Newtonsoft.Json;

namespace FModel.Desktop;

internal sealed class DictionaryEditorWindow<T> : Window where T : class
{
    internal T? Value { get; private set; }

    internal DictionaryEditorWindow(string title, T current, T defaults, Action<T>? validate = null)
    {
        Title = "Versioning Configuration (" + title + ")";
        Width = 700; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var document = new CodeDocument(JsonConvert.SerializeObject(current, Formatting.Indented));
        document.Editor.IsReadOnly = false;
        var error = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var footer = Ui.Row(error, Ui.Button("OK", () =>
        {
            try
            {
                var result = JsonConvert.DeserializeObject<T>(document.Editor.Text) ?? throw new JsonSerializationException("Enter a JSON value.");
                validate?.Invoke(result);
                Value = result; Close(true);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
            { error.Text = exception.Message; }
            return Task.CompletedTask;
        }), Ui.Button("Reset", () =>
        {
            document.Editor.Text = JsonConvert.SerializeObject(defaults, Formatting.Indented);
            error.Text = ""; return Task.CompletedTask;
        }), Ui.Button("Cancel", () => { Close(false); return Task.CompletedTask; }));
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(document);
        Content = panel;
        Closed += (_, _) => document.Dispose();
    }
}
