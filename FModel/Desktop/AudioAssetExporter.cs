using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Sounds;

namespace FModel.Desktop;

internal sealed class AudioAssetExporter : ExporterBase
{
    private readonly UObject _sound;
    public AudioAssetExporter(UObject sound) : base(sound) => _sound = sound;
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _sound.Decode(true, out var format, out var data);
        if (data is not { Length: > 0 }) throw new InvalidDataException("The sound contains no decodable audio payload.");
        return [new ExportFile(format.ToLowerInvariant(), data)];
    }
}
