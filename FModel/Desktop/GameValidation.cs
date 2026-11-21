using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Options;

namespace FModel.Desktop;

internal static class GameValidation
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: --validate-game profile.json output-directory [asset-path-or-name ...]");
        var profile = GameProfiles.Load(args[0]);
        var output = Path.GetFullPath(args[1]);
        var input = Path.GetFullPath(profile.Directory).TrimEnd(Path.DirectorySeparatorChar);
        if (output == input || output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Validation output must be outside the source archive directory.");
        Directory.CreateDirectory(output);
        var settings = new DesktopSettings { OutputDirectory = Path.Combine(output, "Exports"), MeshFormat = EMeshFormat.Gltf2 };
        using var workspace = new ArchiveWorkspace { Log = Console.WriteLine };
        workspace.Open(profile, settings, default);
        var requested = args.Skip(2).ToArray();
        if (requested.Length == 0) throw new ArgumentException("Specify at least one asset path or name to validate.");
        List<SampleResult> samples = [];
        foreach (var path in requested)
        {
            try
            {
                var matches = workspace.Files.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(path, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidDataException($"Expected one matching asset; found {matches.Length}. Use its full archive path.");
                var asset = matches[0];
                var preview = workspace.Preview(asset, settings, default);
                if (asset.File.IsUePackage && preview.Exports.Length == 0) throw new InvalidDataException("The package produced no exports.");
                if (preview.Exports.OfType<UTexture>().Any() && preview.Images.Count == 0) throw new InvalidDataException("The texture produced no preview pixels.");
                var jsonPath = ArchiveWorkspace.OutputPath(output, "Previews/" + asset.Path + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                await File.WriteAllTextAsync(jsonPath, preview.Text);
                for (var imageIndex = 0; imageIndex < preview.Images.Count; imageIndex++)
                    await File.WriteAllBytesAsync(Path.ChangeExtension(jsonPath, imageIndex + ".png"), preview.Images[imageIndex].Data);
                if (preview.Exports.Any(x => x.ExportType == "AthenaCharacterItemDefinition") && preview.Images.Count == 0)
                    throw new InvalidDataException("The original cosmetic renderer produced no preview image.");
                double? duration = null;
                if (preview.Audio is not null)
                {
                    var pcm = await AudioConversion.ForPlaybackAsync(preview.Audio, preview.AudioFormat!);
                    using var player = new MacAudio(pcm);
                    if (player.Duration <= 0) throw new InvalidDataException("Audio decoded to zero duration.");
                    duration = player.Duration;
                    player.Volume(0);
                    player.Play();
                    await Task.Delay(120);
                    player.Pause();
                    if (player.Position <= 0) throw new IOException("The audio output did not advance during playback.");
                    var seek = Math.Min(0.2, player.Duration / 2);
                    player.Position = seek;
                    if (Math.Abs(player.Position - seek) > 0.05) throw new IOException("Audio seeking did not reach the requested position.");
                    player.Stop();
                    await File.WriteAllBytesAsync(ArchiveWorkspace.OutputPath(output, "decoded-" + Path.GetFileNameWithoutExtension(asset.Name) + ".wav"), pcm);
                }
                else if (preview.Exports.OfType<USoundWave>().Any()) throw new InvalidDataException("The sound produced no audio payload.");
                workspace.Queue(asset, "Auto", default);
                var format = preview.Exports.OfType<UAnimationAsset>().Any() ? EMeshFormat.UEFormat : EMeshFormat.Gltf2;
                var exports = await workspace.Exports.RunAsync(settings.OutputDirectory, new ExportOptions(meshFormat: format, exportMaterials: false));
                if (exports.Count == 0 || exports.Any(x => !x.Success))
                    throw new InvalidDataException(string.Join("\n", exports.Where(x => !x.Success).Select(x => x.Error?.ToString())));
                samples.Add(new(asset.Path, true, (asset.File as VfsEntry)?.Vfs.Name, preview.Exports.Select(x => x.ExportType).ToArray(),
                    preview.Images.Count, preview.AudioFormat, duration, exports.SelectMany(x => x.DiskFilePaths ?? []).ToArray(), null));
                Console.WriteLine("PASS: " + asset.Path);
            }
            catch (Exception error)
            {
                workspace.Exports.Clear();
                samples.Add(new(path, false, null, [], 0, null, null, [], error.ToString()));
                Console.WriteLine($"FAIL: {path}: {error.Message}");
            }
        }
        var report = new
        {
            profile.Name, profile.Directory, profile.Version, profile.CustomVersions, profile.VersionOptions,
            Mapping = Path.GetFileName(profile.MappingsPath), MountedArchives = workspace.Provider!.MountedVfs.Count,
            UnloadedArchives = workspace.Provider.UnloadedVfs.Count, MissingKeyGuids = workspace.Provider.RequiredKeys.Select(x => x.ToString()).ToArray(),
            Files = workspace.Files.Length, Samples = samples
        };
        var reportPath = Path.Combine(output, "validation.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, DesktopSettings.JsonOptions));
        Console.WriteLine($"Report: {reportPath}");
        return samples.All(x => x.Success) ? 0 : 1;
    }

    private sealed record SampleResult(string Path, bool Success, string? Archive, string[] Classes, int Images, string? AudioFormat,
        double? AudioDuration, string[] ExportedFiles, string? Error);
}
