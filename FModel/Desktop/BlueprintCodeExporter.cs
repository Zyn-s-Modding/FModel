using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;

namespace FModel.Desktop;

internal sealed class BlueprintCodeExporter(GameFile file, string code, bool keepDirectories = true) : ExporterBase(file, "Blueprint")
{
    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return [new ExportFile("cpp", Encoding.UTF8.GetBytes(code))];
    }
    protected override (string, string) ResolveOutputPath(ExportFile file) => ArchiveWorkspace.ExportFilePath(Session.BaseDirectory.FullName, SavePath + ".cpp", keepDirectories);
}
