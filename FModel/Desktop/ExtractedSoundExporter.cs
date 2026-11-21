using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;

namespace FModel.Desktop;

internal sealed class ExtractedSoundExporter : ExporterBase
{
    private readonly List<PreviewSound> _sounds;
    private readonly bool _convert;
    private readonly ulong _criwareKey;
    private readonly bool _keepDirectories;
    internal ExtractedSoundExporter(GameFile file, List<PreviewSound> sounds, bool convert = false, ulong criwareKey = 0, bool keepDirectories = true) : base(file, "Audio")
    { _sounds = sounds; _convert = convert; _criwareKey = criwareKey; _keepDirectories = keepDirectories; }
    internal ExtractedSoundExporter(UObject export, List<PreviewSound> sounds, bool convert = false, ulong criwareKey = 0, bool keepDirectories = true) : base(export)
    { _sounds = sounds; _convert = convert; _criwareKey = criwareKey; _keepDirectories = keepDirectories; }
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        var files = new List<ExportFile>();
        foreach (var sound in _sounds.DistinctBy(x => x.Path + "." + x.Format))
        {
            ct.ThrowIfCancellationRequested();
            var data = sound.Data;
            var format = sound.Format.ToLowerInvariant();
            if (_convert && format is not ("wav" or "wave"))
            {
                data = AudioConversion.ForPlaybackAsync(data, format, ct, _criwareKey).GetAwaiter().GetResult();
                format = "wav";
            }
            files.Add(new ExportFile(sound.Path.TrimStart('/') + "." + format, data));
        }
        return files;
    }
    protected override (string, string) ResolveOutputPath(ExportFile file)
    {
        return ArchiveWorkspace.ExportFilePath(Session.BaseDirectory.FullName, file.Extension, _keepDirectories);
    }
}
