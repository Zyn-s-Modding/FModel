using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace FModel.Desktop;

internal sealed class EndpointEditorWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    internal RemoteEndpoint? Value { get; private set; }

    internal EndpointEditorWindow(RemoteEndpoint endpoint, bool mappings)
    {
        Title = mappings ? "Mapping Endpoint" : "AES Endpoint";
        Width = 1050; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var url = new TextBox { Text = endpoint.Url };
        var expression = new TextBox { Text = endpoint.Path };
        var source = new CodeDocument("{}");
        var result = new CodeDocument("{}");
        var status = new TextBlock { Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        JToken? body = null;
        var configured = new RemoteEndpoint();
        async Task Send()
        {
            configured.Url = url.Text?.Trim() ?? "";
            configured.Path = expression.Text?.Trim() ?? "";
            body = await RemoteEndpoints.Request(configured, _lifetime.Token);
            source.Editor.Text = body.ToString(Formatting.Indented);
        }
        var send = Ui.Button("Send", Send);
        var test = Ui.Button("Test", async () =>
        {
            if (body is null) await Send();
            var path = expression.Text?.Trim() ?? "";
            var selected = body!.SelectTokens(path).ToArray();
            if (!mappings) RemoteEndpoints.ReadKeys(body, path);
            else if (selected.FirstOrDefault() is not { } mapping || !Uri.TryCreate(mapping.ToString(), UriKind.Absolute, out _))
                throw new InvalidDataException("The expression must select a mapping URL.");
            result.Editor.Text = new JArray(selected.Select(x => x.DeepClone())).ToString(Formatting.Indented);
            status.Text = "Your endpoint configuration is valid! Please, avoid any unnecessary modifications!";
        });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*"), Margin = new Thickness(8) };
        var left = new DockPanel(); var right = new DockPanel();
        var endpointRow = Header("Endpoint", url, send); DockPanel.SetDock(endpointRow, Dock.Top); left.Children.Add(endpointRow); left.Children.Add(source);
        var expressionRow = Header("Expression", expression, test); DockPanel.SetDock(expressionRow, Dock.Top); right.Children.Add(expressionRow); right.Children.Add(result);
        grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right);
        var footer = new DockPanel();
        var buttons = Ui.Row(Ui.Button("Expression Syntax", () => OpenLink("https://www.newtonsoft.com/json/help/html/QueryJsonSelectTokenJsonPath.htm")),
            Ui.Button("Online Evaluator", () => OpenLink("https://jsonpath.com/")), Ui.Button("OK", () =>
            {
                var value = new RemoteEndpoint { Url = url.Text?.Trim() ?? "", Path = expression.Text?.Trim() ?? "" };
                if ((value.Url.Length > 0 || value.Path.Length > 0) && !value.IsConfigured) throw new ArgumentException("Enter a valid endpoint URL and JSONPath expression.");
                Value = value; Close(true); return Task.CompletedTask;
            }));
        buttons.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons); footer.Children.Add(status);
        var panel = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(grid); Content = panel;
        url.TextChanged += (_, _) => { body = null; status.Text = ""; };
        expression.TextChanged += (_, _) => status.Text = "";
        Closed += (_, _) => { _lifetime.Cancel(); source.Dispose(); result.Dispose(); };
    }

    private static Grid Header(string name, Control input, Control action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*,5,Auto"), Margin = new Thickness(0, 0, 0, 5) };
        grid.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 2); grid.Children.Add(input); Grid.SetColumn(action, 4); grid.Children.Add(action); return grid;
    }
    private static Task OpenLink(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; }
}
