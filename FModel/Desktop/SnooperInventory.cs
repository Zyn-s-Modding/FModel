using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Engine;

namespace FModel.Desktop;

internal static class SnooperInventory
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: --snooper-inventory profile.json mesh-name-filter output.json");
        using var workspace = new ArchiveWorkspace { Log = Console.WriteLine };
        workspace.Open(GameProfiles.Load(args[0]), new DesktopSettings(), CancellationToken.None);
        var results = new List<object>();
        foreach (var file in workspace.Files.Where(x => x.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase) && x.File.IsUePackage
            && x.Path.Contains("/Heads/", StringComparison.OrdinalIgnoreCase)
            && !x.Name.StartsWith("T_") && !x.Name.StartsWith("MI_")).Take(200))
        {
            try
            {
                foreach (var mesh in workspace.Provider!.LoadPackage(file.File).GetExports().OfType<USkinnedAsset>())
                {
                    results.Add(new { file.Path, mesh.ExportType, MorphTargets = mesh.MorphTargets.Length });
                    Console.WriteLine($"{file.Name}: {mesh.MorphTargets.Length} morph targets");
                    if (mesh.MorphTargets.Length > 0) break;
                }
            }
            catch (Exception error) { results.Add(new { file.Path, Error = error.Message }); }
            if (results.Any(x => x.GetType().GetProperty("MorphTargets")?.GetValue(x) is int count && count > 0)) break;
        }
        File.WriteAllText(args[2], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
