using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using CUE4Parse_Conversion;
using FModel.ViewModels;

namespace FModel.Desktop;

internal sealed class StreamingLevelWindow : Window
{
    public StreamingLevelWindow(StreamingLevelFilterArgs args)
    {
        var model = new StreamingLevelFilterViewModel(args);
        Title = "Streamed Levels"; Width = 480; Height = 620;
        MinWidth = 360; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var title = new TextBlock { Text = $"{model.WorldName}\n{model.TotalCount:N0} streamed levels", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var tree = new TreeView
        {
            ItemsSource = model.Children,
            ItemTemplate = new FuncTreeDataTemplate<TreeNodeVm>((node, _) =>
            {
                if (node is null) return new TextBlock();
                var checkbox = new CheckBox { Content = node.Name, DataContext = node, IsThreeState = node is ActorNodeVm };
                checkbox.Bind(CheckBox.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay });
                return checkbox;
            }, node => node is ActorNodeVm actor ? actor.Children : [])
        };
        tree.ContainerPrepared += (_, e) =>
        {
            if (e.Container is TreeViewItem { DataContext: ActorNodeVm actor } item) item.IsExpanded = actor.IsExpanded;
        };
        var footer = Ui.Row(Ui.Button("OK", () => { Close(); return Task.CompletedTask; }),
            Ui.Button("Skip All", () => { model.SkipAll(); Close(); return Task.CompletedTask; }));
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(title); panel.Children.Add(footer); panel.Children.Add(tree);
        Content = panel;
    }
}

public partial class MainWindow
{
    private void FilterStreamingLevels(StreamingLevelFilterArgs args, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            if (token.IsCancellationRequested) { done.TrySetCanceled(token); return; }
            try
            {
                var window = new StreamingLevelWindow(args);
                using var registration = token.Register(() => Dispatcher.UIThread.Post(window.Close));
                await window.ShowDialog(_exportWindow ?? (Window)this);
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
        });
        done.Task.WaitAsync(token).GetAwaiter().GetResult();
    }
}
