using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;

namespace FModel.Desktop;

internal sealed class PackagePropertiesExporter(GameFile file, string json, bool keepDirectories = true) : ExporterBase(file, "JsonProperties")
{
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return [new ExportFile("json", Encoding.UTF8.GetBytes(json))];
    }
    protected override (string, string) ResolveOutputPath(ExportFile file) => ArchiveWorkspace.ExportFilePath(Session.BaseDirectory.FullName, SavePath + ".json", keepDirectories);
}
