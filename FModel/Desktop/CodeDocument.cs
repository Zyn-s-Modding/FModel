using System.Text.RegularExpressions;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using FModel.Extensions;
using FModel.Extensions.Themes;

namespace FModel.Desktop;

internal sealed class CodeDocument : UserControl, IDisposable
{
    internal readonly TextEditor Editor;
    internal string Syntax { get; }
    internal static EJsonHighlightTheme JsonTheme { get; set; }
    private readonly bool _json;
    private readonly FoldingManager _folding;
    private readonly SearchPanel _search;
    private readonly List<int> _history = [];
    private int _historyIndex = -1;
    private readonly Func<string, Task>? _openAsset;
    private static readonly Regex Paths = new("\"(?:ObjectPath|AssetPathName|AssetName|ParameterName|CollisionProfileName|TableId)\":\\s*\"(?<path>(?!/?Script/)[^\"\\r\\n]*/[^\"\\r\\n]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Jumps = new(@"\bgoto\s+Label_(?<label>\d+);", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Hex = new("\"Hex\"\\s*:\\s*\"(?<hex>[0-9A-Fa-f]{3,8})\"", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public CodeDocument(string text, string syntax = "Json", Func<string, Task>? openAsset = null)
    {
        _openAsset = openAsset;
        Syntax = syntax;
        _json = syntax == "Json";
        Editor = new TextEditor
        {
            Text = text, IsReadOnly = true, ShowLineNumbers = true,
            FontFamily = DesktopFonts.Code, FontSize = 11,
            Background = Brush.Parse("#262630"), Foreground = Brush.Parse("#DAE5F2"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        using (var stream = AssetLoader.Open(new Uri($"avares://FModel/Resources/{syntax}.xshd")))
        using (var reader = XmlReader.Create(stream))
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        ApplyTheme(JsonTheme);
        _folding = FoldingManager.Install(Editor.TextArea);
        if (syntax == "Json") _folding.UpdateFoldings(JsonFoldings(text), -1);
        _search = SearchPanel.Install(Editor);
        Editor.TextArea.TextView.LineTransformers.Add(new AssetLinkColors());
        var position = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Editor.TextArea.Caret.PositionChanged += (_, _) => position.Text = $"Ln {Editor.TextArea.Caret.Line:N0}, Col {Editor.TextArea.Caret.Column:N0}";
        Editor.ContextMenu = new ContextMenu { Items =
        {
            Ui.Menu("Find", () => { _search.Open(); return Task.CompletedTask; }),
            Ui.Menu("Fold All", () => { foreach (var fold in _folding.AllFoldings) fold.IsFolded = true; return Task.CompletedTask; }),
            Ui.Menu("Unfold All", () => { foreach (var fold in _folding.AllFoldings) fold.IsFolded = false; return Task.CompletedTask; }),
            Ui.Menu("Copy", () => { Editor.Copy(); return Task.CompletedTask; }),
            Ui.Menu("Select All", () => { Editor.SelectAll(); return Task.CompletedTask; })
        }};
        var panel = new DockPanel();
        DockPanel.SetDock(position, Dock.Bottom); panel.Children.Add(position); panel.Children.Add(Editor);
        Content = panel;
        Editor.KeyDown += (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && e.Key == Key.F)
            { _search.Open(); e.Handled = true; }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right)
            {
                var next = _historyIndex + (e.Key == Key.Left ? -1 : 1);
                if (next >= 0 && next < _history.Count) { _historyIndex = next; MoveTo(_history[next]); }
                e.Handled = true;
            }
        };
        Editor.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None) return;
            if (JumpAt(e.GetPosition(Editor)) is { } label)
            {
                var line = Editor.Text.GetNameLineNumberText($"        Label_{label}:");
                if (line > 0) { Remember(Editor.CaretOffset); MoveTo(Editor.Document.GetLineByNumber(line).Offset); Remember(Editor.CaretOffset); }
                e.Handled = true; return;
            }
            if (_openAsset is null) return;
            var hit = PathAt(e.GetPosition(Editor));
            if (hit is null) return;
            Remember(Editor.CaretOffset);
            e.Handled = true;
            try { await _openAsset(hit); }
            catch (Exception error) { if (TopLevel.GetTopLevel(this) is Window window) await Ui.Message(window, "Asset reference", error.Message); }
        };
        Editor.PointerMoved += (_, e) =>
        {
            Editor.Cursor = new Cursor(PathAt(e.GetPosition(Editor)) is null && JumpAt(e.GetPosition(Editor)) is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
            var hit = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (hit is not { } at) return;
            var line = Editor.Document.GetLineByNumber(at.Line);
            var match = Hex.Match(Editor.Document.GetText(line));
            if (match.Success && SkiaSharp.SKColor.TryParse(match.Groups["hex"].Value, out var color))
            {
                var foreground = color.Red * color.Red * .299 + color.Green * color.Green * .587 + color.Blue * color.Blue * .114 > 130 * 130 ? Brushes.Black : Brushes.White;
                ToolTip.SetTip(Editor, new ToolTip { Content = "#" + match.Groups["hex"].Value,
                    Background = new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue)), Foreground = foreground, BorderBrush = foreground });
            }
            else ToolTip.SetTip(Editor, null);
        };
        Editor.PointerWheelChanged += (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return;
            Editor.FontSize = Math.Clamp(Editor.FontSize + e.Delta.Y * 2.4, 6, 32); e.Handled = true;
        };
    }

    internal void OpenSearch() => _search.Open();

    private string? JumpAt(Point position)
    {
        if (Editor.GetPositionFromPoint(position) is not { } at) return null;
        var line = Editor.Document.GetLineByNumber(at.Line);
        return Jumps.Matches(Editor.Document.GetText(line)).Cast<Match>().FirstOrDefault(match =>
            at.Column - 1 >= match.Index && at.Column - 1 <= match.Index + match.Length)?.Groups["label"].Value;
    }

    internal void NavigateTo(string name)
    {
        var line = Editor.Text.GetNameLineNumber(name);
        if (line < 1 || line > Editor.Document.LineCount) return;
        var offset = Editor.Document.GetLineByNumber(line).Offset;
        Remember(Editor.CaretOffset);
        MoveTo(offset);
        Remember(offset);
    }

    internal void ApplyTheme(EJsonHighlightTheme theme)
    {
        if (_json) { Editor.SyntaxHighlighting.ApplyJsonTheme(theme); Editor.TextArea.TextView.Redraw(); }
    }

    private void MoveTo(int offset)
    {
        Editor.CaretOffset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        Editor.ScrollToLine(Editor.TextArea.Caret.Line);
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void Remember(int offset)
    {
        if (_historyIndex >= 0 && _history[_historyIndex] == offset) return;
        if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(offset); _historyIndex = _history.Count - 1;
    }

    private string? PathAt(Point point)
    {
        var hit = Editor.GetPositionFromPoint(point);
        if (hit is not { } at) return null;
        var line = Editor.Document.GetLineByNumber(at.Line);
        var offset = at.Column - 1;
        foreach (Match match in Paths.Matches(Editor.Document.GetText(line)))
            if (offset >= match.Index && offset <= match.Index + match.Length) return match.Groups["path"].Value;
        return null;
    }

    private static IEnumerable<NewFolding> JsonFoldings(string text)
    {
        var folds = new List<NewFolding>();
        var stack = new Stack<(int Offset, int Line)>();
        var quoted = false; var escaped = false; var line = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') line++;
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') quoted = true;
            else if (c is '{' or '[') stack.Push((i, line));
            else if (c is '}' or ']' && stack.TryPop(out var start) && line > start.Line)
                folds.Add(new NewFolding(start.Offset, i + 1) { Name = text[start.Offset] == '{' ? "{ … }" : "[ … ]" });
        }
        return folds.OrderBy(x => x.StartOffset);
    }

    public void Dispose() { FoldingManager.Uninstall(_folding); }

    private sealed class AssetLinkColors : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (Match match in Paths.Matches(CurrentContext.Document.GetText(line)))
            {
                var group = match.Groups["path"];
                ChangeLinePart(line.Offset + group.Index, line.Offset + group.Index + group.Length,
                    element => element.TextRunProperties.SetForegroundBrush(Brushes.Plum));
            }
            foreach (Match match in Jumps.Matches(CurrentContext.Document.GetText(line)))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    element => element.TextRunProperties.SetForegroundBrush(Brushes.Plum));
            foreach (Match match in Hex.Matches(CurrentContext.Document.GetText(line)))
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    element => element.TextRunProperties.SetForegroundBrush(Brushes.PeachPuff));
        }
    }
}
