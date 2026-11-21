using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace FModel.Desktop;

internal static class Ui
{
    public static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4) };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    public static Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Serilog.Log.Error(e, "{Action} failed", label);
                if (TopLevel.GetTopLevel(button) is Window owner) await Message(owner, label, e.Message);
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    public static MenuItem Menu(string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Serilog.Log.Error(error, "{Action} failed", label);
                var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (owner is not null) await Message(owner, label, error.Message);
            }
        };
        return item;
    }

    public static Window Dialog(string title, double width = 620, double height = 480) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = 380, MinHeight = 250,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    public static async Task Message(Window owner, string title, string message)
    {
        var dialog = Dialog(title, 580, 280);
        var panel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var close = Button("OK", () => { dialog.Close(); return Task.CompletedTask; });
        close.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
    }

    public static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4) };
    public static T Field<T>(Panel panel, string label, T control) where T : Control
    {
        panel.Children.Add(Label(label)); panel.Children.Add(control); return control;
    }
}
