using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Engine;

namespace FModel.Desktop;

public partial class MainWindow
{
    private bool ShouldPreview(UObject asset) => SnooperScene.ShouldPreview(asset, Settings);

    private AssetEntry[] SelectedOrDocument() => Documents.IsVisible && Documents.SelectedItem is TabItem { Tag: AssetEntry file }
        ? [file] : SelectedAssets();

    private async Task HandleShortcut(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape && _operation is not null) { _operation.Cancel(); e.Handled = true; return; }
        if (e.Key == Key.F3 && Documents.SelectedItem is TabItem document)
        {
            var editor = document.Content as CodeDocument ?? (document.Content as Control)?.Tag as CodeDocument;
            editor?.OpenSearch(); e.Handled = editor is not null; return;
        }
        if (e.Key == Key.F12 && Workspace.Profile is { } mappingsProfile && _operation is null)
        {
            e.Handled = true;
            await LoadProfile(mappingsProfile);
            return;
        }
        foreach (var (action, text) in Settings.Hotkeys)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            KeyGesture gesture;
            try { gesture = KeyGesture.Parse(text); } catch (FormatException) { continue; }
            var alternate = gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)
                ? new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Meta) | KeyModifiers.Control) : gesture;
            if (!gesture.Matches(e) && !alternate.Matches(e)) continue;
            if (gesture.KeyModifiers == KeyModifiers.None && e.Source is Avalonia.Visual visual &&
                (visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null || visual.FindAncestorOfType<AvaloniaEdit.TextEditor>(includeSelf: true) is not null)) continue;
            e.Handled = true;
            switch (action)
            {
                case "Select Directory": await SelectGameAsync(); break;
                case "Search Packages": await SearchAsync(); break;
                case "Find References": await FindReferencesAsync(); break;
                case "Toggle Asset Explorer": SetBrowserVisible(!ExplorerPanel.IsVisible); break;
                case "Add Asset Tab": AddAssetTab(this, e); break;
                case "Close Asset Tab": if (Documents.SelectedItem is TabItem tab) CloseDocument(tab); break;
                case "Export Raw Data": await Queue(SelectedOrDocument(), "Raw"); break;
                case "Export Properties": await Queue(SelectedOrDocument(), "Properties"); break;
                case "Export Embedded Files": await Queue(SelectedOrDocument(), "Embedded"); break;
                case "Export Textures": await Queue(SelectedOrDocument(), "Texture"); break;
                case "Export Models": await Queue(SelectedOrDocument(), "Model"); break;
                case "Export Worlds": await Queue(SelectedOrDocument(), "World"); break;
                case "Export Animations": await Queue(SelectedOrDocument(), "Animation"); break;
                case "Export Audio": await Queue(SelectedOrDocument(), "Audio"); break;
                case "Export Decompiled Blueprints": await Queue(SelectedOrDocument(), "Code"); break;
                case "Previous Directory Tab": LeftTabs.SelectedIndex = (LeftTabs.SelectedIndex + LeftTabs.ItemCount - 1) % LeftTabs.ItemCount; break;
                case "Next Directory Tab": LeftTabs.SelectedIndex = (LeftTabs.SelectedIndex + 1) % LeftTabs.ItemCount; break;
                case "Previous Asset Tab": if (Documents.ItemCount > 0) Documents.SelectedIndex = (Documents.SelectedIndex + Documents.ItemCount - 1) % Documents.ItemCount; break;
                case "Next Asset Tab": if (Documents.ItemCount > 0) Documents.SelectedIndex = (Documents.SelectedIndex + 1) % Documents.ItemCount; break;
                case "Copy Package Path":
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(string.Join('\n', SelectedOrDocument().Select(x => x.Path)));
                    break;
                case "Export Session": await ShowExports(); break;
                case "Reload Archives": if (Workspace.Profile is { } profile) await LoadProfile(profile); break;
                default: e.Handled = false; continue;
            }
            return;
        }
    }
}
