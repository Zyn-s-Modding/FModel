using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;

namespace FModel.Desktop;

// Retain dotted file names and package payloads; resolve archive names as relative
// paths on Unix instead of assuming Windows drive and separator rules.
internal sealed class ArchiveRawExporter : ExporterBase
{
    private readonly GameFile _file;
    private readonly IFileProvider _provider;
    internal string? OutputDirectory { get; set; }
    internal bool KeepDirectories { get; set; }
    public ArchiveRawExporter(GameFile file, IFileProvider provider, bool keepDirectories = true) : base(file, "RawData")
    { _file = file; _provider = provider; KeepDirectories = keepDirectories; }
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _provider.SavePackage(_file).Select(x => new ExportFile(x.Key, x.Value)).ToArray();
    }
    protected override (string, string) ResolveOutputPath(ExportFile file)
    {
        return ArchiveWorkspace.ExportFilePath(OutputDirectory ?? Session.BaseDirectory.FullName, file.Extension, KeepDirectories);
    }
}
