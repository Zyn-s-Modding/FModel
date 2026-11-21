using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FModel.Desktop;

internal sealed class ModelWindow : Window
{
    internal ModelViewport Viewport { get; }
    public ModelWindow(string path, string? temporaryDirectory = null)
    {
        Title = "3D Viewer — " + Path.GetFileName(path); Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel();
        var status = new TextBlock { Text = "Drag to orbit · Right-drag to pan · Scroll to zoom", Margin = new Thickness(8) };
        Viewport = new ModelViewport(path);
        Viewport.Error = error => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = "Renderer: " + error);
        var wire = new CheckBox { Content = "Wireframe" };
        wire.IsCheckedChanged += (_, _) => Viewport.Wireframe = wire.IsChecked == true;
        var controls = Ui.Row(Ui.Button("Reset Camera", () => { Viewport.ResetCamera(); return Task.CompletedTask; }), wire);
        DockPanel.SetDock(controls, Dock.Top); DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(controls); panel.Children.Add(status);
        var hierarchy = new ListBox { Width = 220, ItemsSource = Viewport.Nodes };
        hierarchy.SelectionChanged += (_, _) => { Viewport.SelectedNode = hierarchy.SelectedItem as string; Viewport.RequestNextFrameRendering(); };
        DockPanel.SetDock(hierarchy, Dock.Left); panel.Children.Add(hierarchy); panel.Children.Add(Viewport);
        Content = panel;
        Closed += (_, _) =>
        {
            if (temporaryDirectory is not null)
            {
                try { Directory.Delete(temporaryDirectory, true); }
                catch (IOException) { }
            }
        };
    }
}
