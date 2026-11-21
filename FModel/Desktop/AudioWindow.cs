using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace FModel.Desktop;

internal sealed class MacAudio : IDisposable
{
    private nint _handle;
    [DllImport("FModelAudio")] private static extern nint fmodel_audio_create(byte[] bytes, int length);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_destroy(nint handle);
    [DllImport("FModelAudio")] private static extern int fmodel_audio_play(nint handle);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_pause(nint handle);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_stop(nint handle);
    [DllImport("FModelAudio")] private static extern double fmodel_audio_duration(nint handle);
    [DllImport("FModelAudio")] private static extern double fmodel_audio_position(nint handle);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_seek(nint handle, double seconds);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_volume(nint handle, float volume);
    [DllImport("FModelAudio")] private static extern void fmodel_audio_loop(nint handle, int loop);
    public MacAudio(byte[] data)
    {
        _handle = fmodel_audio_create(data, data.Length);
        if (_handle == 0) throw new NotSupportedException("macOS could not decode this audio format. Export the source audio for use with a decoder that supports it.");
    }
    public double Duration => fmodel_audio_duration(_handle);
    public double Position { get => fmodel_audio_position(_handle); set => fmodel_audio_seek(_handle, value); }
    public void Play() { if (fmodel_audio_play(_handle) == 0) throw new IOException("The audio output could not start."); }
    public void Pause() => fmodel_audio_pause(_handle);
    public void Stop() => fmodel_audio_stop(_handle);
    public void Volume(double volume) => fmodel_audio_volume(_handle, (float)volume);
    public void Loop(bool loop) => fmodel_audio_loop(_handle, loop ? 1 : 0);
    [DllImport("libFModelAudio.dylib")] private static extern int fmodel_audio_playing(nint handle);
    [DllImport("libFModelAudio.dylib")] private static extern double fmodel_audio_sample_rate(nint handle);
    [DllImport("libFModelAudio.dylib")] private static extern int fmodel_audio_channels(nint handle);
    [DllImport("libFModelAudio.dylib")] private static extern int fmodel_audio_byte_rate(nint handle);
    [DllImport("libFModelAudio.dylib")] private static extern int fmodel_audio_spectrum(nint handle, [Out] float[] bands, int count);
    [DllImport("libFModelAudio.dylib")] private static extern int fmodel_audio_device(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string uid);
    [DllImport("libFModelAudio.dylib")] private static extern nint fmodel_audio_devices();
    [DllImport("libFModelAudio.dylib")] private static extern void fmodel_audio_free(nint pointer);
    [DllImport("libFModelAudio.dylib")] private static extern nint fmodel_audio_pcm_wav(nint handle, out int length);
    public bool IsPlaying => fmodel_audio_playing(_handle) != 0;
    public double SampleRate => fmodel_audio_sample_rate(_handle);
    public int Channels => fmodel_audio_channels(_handle);
    public int ByteRate => fmodel_audio_byte_rate(_handle);
    public bool Spectrum(float[] bands) => fmodel_audio_spectrum(_handle, bands, bands.Length) != 0;
    public void Device(string uid) => fmodel_audio_device(_handle, uid);
    public byte[] DecodePcm()
    {
        var pointer = fmodel_audio_pcm_wav(_handle, out var length);
        if (pointer == 0 || length < 44) throw new InvalidDataException("AVFoundation could not convert the audio to PCM.");
        try { var data = new byte[length]; Marshal.Copy(pointer, data, 0, length); return data; }
        finally { fmodel_audio_free(pointer); }
    }
    public static AudioDevice[] Devices()
    {
        var json = fmodel_audio_devices();
        try { return JsonSerializer.Deserialize<AudioDevice[]>(Marshal.PtrToStringUTF8(json) ?? "[]") ?? []; }
        finally { fmodel_audio_free(json); }
    }
    public void Dispose() { if (_handle != 0) { fmodel_audio_destroy(_handle); _handle = 0; } }
}

internal sealed record AudioDevice(string Name, string Id)
{
    public override string ToString() => Name;
}

internal sealed class AudioWindow : Window
{
    private sealed record Track(string Path, byte[] Data, string Format)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public byte[]? DecodedData { get; set; }
        public string SaveFormat => DecodedData is null ? Format : "wav";
    }
    private readonly List<Track> _tracks = [];
    private readonly ListBox _playlist = new();
    private readonly TextBlock _name = new() { Text = "No audio file", Foreground = Brush.Parse("#D7B158"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _clock = new() { Text = "00:00:00.00", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _encoding = new() { Text = "Unknown (0)", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _length = new() { Text = "00:00:00", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _byteRate = new() { Text = "0", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100 };
    private readonly Slider _timeline = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(5, 0) };
    private readonly SpectrumDisplay _spectrum = new();
    private readonly Button _play;
    private readonly ComboBox _devices = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loading;
    private MacAudio? _player;
    private Track? _current;
    private readonly DesktopSettings _settings;
    private bool _updating;

    public AudioWindow(DesktopSettings settings)
    {
        _settings = settings;
        Title = "Audio Player"; Width = 1000; Height = 570; MinWidth = 700; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("320,4,*") };
        var left = new DockPanel { Margin = new Thickness(8) };
        var information = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        var available = new[] { new AudioDevice("System Default", "") }.Concat(MacAudio.Devices().OrderBy(x => x.Name)).ToArray();
        _devices.ItemsSource = available;
        _devices.SelectedItem = available.FirstOrDefault(x => x.Id == settings.AudioDeviceId) ?? available[0];
        settings.AudioDeviceId = ((AudioDevice)_devices.SelectedItem).Id;
        _volume.Value = settings.AudioPlayerVolume;
        var values = new (string Label, Control Value)[] { ("Devices", _devices), ("Volume", _volume), ("Encoding", _encoding), ("Length", _length), ("Bytes Per Second", _byteRate) };
        for (var row = 0; row < values.Length; row++)
        {
            var label = new TextBlock { Text = values[row].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, row); information.Children.Add(label);
            var value = values[row].Value; value.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(value, row); Grid.SetColumn(value, 1); information.Children.Add(value);
        }
        DockPanel.SetDock(information, Dock.Top); left.Children.Add(information);
        var title = new SectionHeader { Text = "PLAYLIST" }; DockPanel.SetDock(title, Dock.Top); left.Children.Add(title);
        var filter = new TextBox { Watermark = "Search by name…", Margin = new Thickness(0, 5), InnerLeftContent = AssetIcons.Named("SearchIcon") };
        filter.TextChanged += (_, _) => _playlist.ItemsSource = _tracks.Where(x => x.Name.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        DockPanel.SetDock(filter, Dock.Top); left.Children.Add(filter);
        _playlist.ItemTemplate = new FuncDataTemplate<Track>((track, _) => new TextBlock { Text = track?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        MainWindow.SelectRightClickedItem(_playlist);
        _playlist.DoubleTapped += async (_, _) => { if (_playlist.SelectedItem is Track track) await PlayTrack(track); };
        _playlist.ContextMenu = new ContextMenu { Items =
        {
            Ui.Menu("Play", async () => { if (_playlist.SelectedItem is Track track) await PlayTrack(track); }),
            Ui.Menu("Remove", RemoveSelected),
            Ui.Menu("Save", SaveSelected),
            Ui.Menu("Save Playlist", () => SaveTracks(_tracks.ToArray()))
        }};
        left.Children.Add(_playlist); layout.Children.Add(left);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(splitter, 1); layout.Children.Add(splitter);
        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Background = Brush.Parse("#262630") };
        right.Children.Add(_spectrum);
        var controls = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), Spacing = 3 };
        controls.Children.Add(_name); controls.Children.Add(_clock);
        var previous = Ui.Button("", () => Step(-1)); previous.Content = AssetIcons.Named("SkipPreviousIcon"); ToolTip.SetTip(previous, "Previous");
        var next = Ui.Button("", () => Step(1)); next.Content = AssetIcons.Named("SkipNextIcon"); ToolTip.SetTip(next, "Next");
        _play = Ui.Button("", PlayPause);
        _play.Content = AssetIcons.Named("PlayIcon"); ToolTip.SetTip(_play, "Play / Pause");
        var transport = Ui.Row(previous, _play, next); transport.HorizontalAlignment = HorizontalAlignment.Right; controls.Children.Add(transport);
        right.Children.Add(controls);
        Grid.SetRow(_timeline, 1); right.Children.Add(_timeline);
        var range = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 0, 8, 4) };
        range.Children.Add(new TextBlock { Text = "00:00", FontSize = 10 });
        var end = new TextBlock { FontSize = 10 }; Grid.SetColumn(end, 1); range.Children.Add(end);
        Grid.SetRow(range, 2); right.Children.Add(range);
        Grid.SetColumn(right, 2); layout.Children.Add(right); Content = layout;
        _timeline.PropertyChanged += (_, e) => { if (!_updating && e.Property == Slider.ValueProperty && _player is not null) _player.Position = _timeline.Value; };
        _volume.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { settings.AudioPlayerVolume = (int)_volume.Value; _player?.Volume(_volume.Value / 100); } };
        _devices.SelectionChanged += (_, _) => { if (_devices.SelectedItem is AudioDevice device) { settings.AudioDeviceId = device.Id; _player?.Device(device.Id); } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            if (_player is null) return;
            _updating = true; _timeline.Value = _player.Position; _updating = false;
            _clock.Text = TimeSpan.FromSeconds(_player.Position).ToString(@"hh\:mm\:ss\.ff");
            end.Text = TimeSpan.FromSeconds(_player.Duration).ToString(@"mm\:ss");
            _play.Content = AssetIcons.Named(_player.IsPlaying ? "PauseIcon" : "PlayIcon");
            _player.Spectrum(_spectrum.Values); _spectrum.InvalidateVisual();
        };
        KeyDown += async (_, e) =>
        {
            if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
            try
            {
                foreach (var action in new[] { "Add Audio", "Remove Audio", "Play / Pause Audio", "Previous Audio", "Next Audio" })
                {
                    if (!settings.Hotkeys.TryGetValue(action, out var shortcut) || string.IsNullOrWhiteSpace(shortcut)) continue;
                    var gesture = KeyGesture.Parse(shortcut);
                    var alternate = new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Meta) | (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? KeyModifiers.Control : KeyModifiers.None));
                    if (!gesture.Matches(e) && !alternate.Matches(e)) continue;
                    e.Handled = true;
                    switch (action)
                    {
                        case "Add Audio": await AddFiles(); break;
                        case "Remove Audio": await RemoveSelected(); break;
                        case "Play / Pause Audio": await PlayPause(); break;
                        case "Previous Audio": await Step(-1); break;
                        case "Next Audio": await Step(1); break;
                    }
                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { await Ui.Message(this, "Audio Player", error.Message); }
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            try
            {
                foreach (var file in (e.DataTransfer.TryGetFiles() ?? []).OfType<IStorageFile>())
                {
                    await using var input = await file.OpenReadAsync(); using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
                    await LoadAsync(file.Name, buffer.ToArray(), Path.GetExtension(file.Name).TrimStart('.'));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { await Ui.Message(this, "Audio Player", error.Message); }
        });
        Opened += (_, _) => timer.Start();
        Closed += (_, _) =>
        {
            _lifetime.Cancel(); _loading?.Cancel(); timer.Stop(); _player?.Dispose(); _player = null;
            try { settings.Save(); } catch (Exception error) { Serilog.Log.Error(error, "Could not save audio settings"); }
        };
    }

    private async Task AddFiles()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select an audio file", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio files") { Patterns = ["*.ogg", "*.wav", "*.wem", "*.adpcm", "*.mp3", "*.flac", "*.binka"] }, FilePickerFileTypes.All] });
        foreach (var file in files)
        {
            await using var input = await file.OpenReadAsync(); using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
            await LoadAsync(file.Name, buffer.ToArray(), Path.GetExtension(file.Name).TrimStart('.'));
        }
    }

    public async Task LoadAsync(string name, byte[] data, string format)
    {
        var track = new Track(name, data, format.ToLowerInvariant());
        _tracks.Add(track); _playlist.ItemsSource = _tracks.ToArray();
        if (_tracks.Count == 1) await PlayTrack(track);
    }

    private async Task PlayTrack(Track track)
    {
        _loading?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loading = operation;
        try
        {
            var criwareKey = _settings.Games.FirstOrDefault(x => x.Directory == _settings.LastDirectory)?.CriwareDecryptionKey ?? 0;
            var playable = await AudioConversion.ForPlaybackAsync(track.Data, track.Format, operation.Token, criwareKey);
            operation.Token.ThrowIfCancellationRequested();
            track.DecodedData = playable;
            var player = new MacAudio(playable);
            _player?.Dispose(); _player = player; _current = track; _playlist.SelectedItem = track;
            player.Volume(_volume.Value / 100); player.Device(_settings.AudioDeviceId); player.Play();
            _name.Text = track.Name; _encoding.Text = $"{track.Format.ToUpperInvariant()} ({player.Channels} ch, {player.SampleRate:N0} Hz)";
            _length.Text = TimeSpan.FromSeconds(player.Duration).ToString(@"hh\:mm\:ss");
            _byteRate.Text = player.ByteRate.ToString("N0");
            _timeline.Maximum = Math.Max(player.Duration, 0.01);
        }
        finally { if (ReferenceEquals(_loading, operation)) _loading = null; }
    }

    private async Task PlayPause()
    {
        if (_player is null) { if (_playlist.SelectedItem is Track track) await PlayTrack(track); return; }
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }
    private Task Step(int direction)
    {
        if (_tracks.Count == 0) return Task.CompletedTask;
        var index = _current is null ? 0 : _tracks.IndexOf(_current);
        return PlayTrack(_tracks[(index + direction + _tracks.Count) % _tracks.Count]);
    }
    private async Task RemoveSelected()
    {
        if (_playlist.SelectedItem is not Track track) return;
        var index = _tracks.IndexOf(track); _tracks.Remove(track); _playlist.ItemsSource = _tracks.ToArray();
        if (_current != track) return;
        _loading?.Cancel(); _player?.Dispose(); _player = null; _current = null;
        if (_tracks.Count > 0) await PlayTrack(_tracks[Math.Min(index, _tracks.Count - 1)]);
        else { _name.Text = "No audio file"; _clock.Text = "00:00:00.00"; Array.Clear(_spectrum.Values); _spectrum.InvalidateVisual(); }
    }
    private async Task SaveTracks(Track[] tracks)
    {
        foreach (var track in tracks)
        {
            var output = ArchiveWorkspace.OutputPath(Path.Combine(_settings.OutputDirectory, "Audio"), Path.ChangeExtension(track.Path, track.SaveFormat));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, track.DecodedData ?? track.Data, _lifetime.Token);
        }
    }

    private async Task SaveSelected()
    {
        if (_playlist.SelectedItem is not Track track) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "Save Audio", SuggestedFileName = track.Name + "." + track.SaveFormat });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0); await stream.WriteAsync(track.DecodedData ?? track.Data, _lifetime.Token);
    }

    internal async Task CheckPlaylist(byte[] data, string format)
    {
        await LoadAsync("smoke/First", data, format);
        var first = _current;
        await LoadAsync("smoke/Second", data, format);
        if (_tracks.Count != 2 || _current != first) throw new InvalidOperationException("Adding playlist entries changed the playing track.");
        await Step(1);
        if (_current?.Name != "Second") throw new InvalidOperationException("Next track failed.");
        await Step(-1);
        if (_current != first || _player is null) throw new InvalidOperationException("Previous track failed.");
        _player.Pause();
        _timeline.Value = _player.Duration / 2;
        if (Math.Abs(_player.Position - _timeline.Value) > 0.05) throw new InvalidOperationException("Audio timeline seeking failed.");
        await SaveTracks(_tracks.ToArray());
        if (Directory.GetFiles(Path.Combine(_settings.OutputDirectory, "Audio"), "*.wav", SearchOption.AllDirectories).Length != 2)
            throw new InvalidOperationException("Saving the decoded playlist failed.");
        _playlist.SelectedItem = _current;
        await RemoveSelected();
        if (_tracks.Count != 1 || _current?.Name != "Second") throw new InvalidOperationException("Removing the playing track failed.");
    }

    internal static byte[] DecodeVorbis(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var reader = new NVorbis.VorbisReader(input, false);
        using var pcm = new MemoryStream();
        using var writer = new BinaryWriter(pcm, System.Text.Encoding.UTF8, true);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)reader.Channels); writer.Write(reader.SampleRate);
        writer.Write(reader.SampleRate * reader.Channels * 2); writer.Write((short)(reader.Channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(0);
        var buffer = new float[reader.SampleRate * reader.Channels];
        int count;
        while ((count = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < count; i++) writer.Write((short)(Math.Clamp(buffer[i], -1, 1) * short.MaxValue));
        var length = checked((int)pcm.Length);
        pcm.Position = 4; writer.Write(length - 8); pcm.Position = 40; writer.Write(length - 44);
        return pcm.ToArray();
    }
}

internal sealed class SpectrumDisplay : Control
{
    internal readonly float[] Values = new float[100];
    private readonly LinearGradientBrush _gradient = new()
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops = [new GradientStop(Color.Parse("#54A559"), 0), new GradientStop(Color.Parse("#DBBE53"), 1)]
    };
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width / Values.Length;
        for (var i = 0; i < Values.Length; i++)
        {
            var height = Math.Clamp(Values[i], 0, 1) * Bounds.Height;
            context.DrawRectangle(_gradient, null, new Rect(i * width, Bounds.Height - height, Math.Max(1, width - 2), height));
        }
    }
}
