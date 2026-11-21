using System.Diagnostics;
using Avalonia.Threading;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Textures;
using Editor;
using Snooper.Hosting;
using Snooper.Rendering.Actors;

namespace FModel.Desktop;

/// <summary>GLFW and Cocoa windows are created and advanced on Avalonia's UI thread.</summary>
internal sealed class DesktopSnooperHost : IDisposable, IBridgeHost
{
    private readonly MainWindow _owner;
    private readonly Func<Task> _showExports;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private EditorWindow? _window;
    private bool _stepping;
    private bool _disposed;

    public DesktopSnooperHost(MainWindow owner, Func<Task> showExports)
    {
        _owner = owner;
        _showExports = showExports;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 30), DispatcherPriority.Background, (_, _) => Step());
        _timer.Stop();
        Bridge.Host = this;
        Bridge.PendingRequestChanged += RequestChanged;
    }

    public string Name => "FModel";
    public string ImGuiIniPath => Path.Combine(DesktopSettings.DataDirectory, "snooper.ini");
    public bool CanBrowseAssets => true;
#if DEBUG
    public bool SuppressViewerLogs { get; set; }
#endif
    public ExportSession Session => _owner.Workspace.Exports;
    public void ShowExportSession() => Dispatcher.UIThread.Post(async () => await _showExports());

    public EditorWindow Show(Actor? actor = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is null)
        {
            var provider = _owner.Workspace.Provider ?? throw new InvalidOperationException("Load a game directory first.");
            var screen = _owner.Screens.ScreenFromWindow(_owner);
            var width = screen is null ? 1200 : (int)(screen.WorkingArea.Width / screen.Scaling * 0.9);
            var height = screen is null ? 800 : (int)(screen.WorkingArea.Height / screen.Scaling * 0.85);
            var editor = new EditorWindow(30, width, height, provider, startVisible: false, closeHides: true);
            try { editor.InitializeExternalLoop(); }
            catch { editor.Dispose(); throw; }
            _window = editor;
            _clock.Restart();
            _timer.Start();
        }
        SnooperScene.Load(_window, actor);
        return _window;
    }

    public bool TryDeliver(CUE4Parse.UE4.Assets.Exports.UObject asset)
    {
        if (!Bridge.TryDeliver(asset)) return false;
        _window?.Invoke(_window.Show);
        return true;
    }

    public void InspectTexture(CTexture texture, string name, bool nearestNeighbor)
        => Show().InspectTexture(texture, name, nearestNeighbor);

    private void RequestChanged(AssetRequest? request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _owner.SetViewerRequest(request?.DisplayText);
            if (request is not null) _owner.Activate();
        });
    }

    private void Step()
    {
        if (_stepping || _window is not { } window) return;
        _stepping = true;
        try
        {
            var elapsed = _clock.Elapsed.TotalSeconds;
            _clock.Restart();
            if (!window.StepExternalLoop(Math.Clamp(elapsed, 0.000001, 1))) Reset();
        }
        catch (Exception error)
        {
            _owner.AppendLog("Snooper: " + error);
            Reset();
        }
        finally { _stepping = false; }
    }

    public void Reset()
    {
        _timer.Stop();
        var window = _window;
        if (window is not null)
        {
            Snooper.Core.Managers.ThreadManager.ClearAndDispose();
            if (!Snooper.Core.Managers.ThreadManager.WaitForIdle(TimeSpan.FromSeconds(30)))
            {
                _timer.Start();
                throw new TimeoutException("Snooper is still reading assets. Wait for loading to finish before closing the game directory.");
            }
        }
        Bridge.CancelRequest();
        window?.Dispose();
        _window = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        Bridge.PendingRequestChanged -= RequestChanged;
    }
}
