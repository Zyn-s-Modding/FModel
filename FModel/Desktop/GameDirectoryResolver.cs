using System.Text.Json;
using CUE4Parse.UE4.Versions;
using System.Text.RegularExpressions;

namespace FModel.Desktop;

internal static class GameDirectoryResolver
{
    internal static IEnumerable<GameProfile> DiscoverInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var steam = Path.Combine(home, "Library", "Application Support", "Steam", "steamapps");
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        var libraryFile = Path.Combine(steam, "libraryfolders.vdf");
        if (File.Exists(libraryFile))
            foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\""))
                libraries.Add(Path.Combine(match.Groups["value"].Value.Replace("\\\\", "\\"), "steamapps"));
        foreach (var library in libraries.Where(Directory.Exists))
            foreach (var manifest in Directory.EnumerateFiles(library, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var install = Regex.Match(text, "\"installdir\"\\s*\"(?<value>[^\"]+)\"").Groups["value"].Value;
                var name = Regex.Match(text, "\"name\"\\s*\"(?<value>[^\"]+)\"").Groups["value"].Value;
                if (install.Length > 0) candidates[Path.Combine(library, "common", install)] = name;
            }
        var epic = Path.Combine(home, "Library", "Application Support", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epic))
            foreach (var manifest in Directory.EnumerateFiles(epic, "*.item"))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (json.RootElement.TryGetProperty("InstallLocation", out var location) && location.GetString() is { Length: > 0 } path)
                        candidates[path] = json.RootElement.TryGetProperty("DisplayName", out var title) ? title.GetString() ?? "" : "";
                }
                catch (JsonException error) { Serilog.Log.Debug(error, "Could not read launcher manifest {Path}", manifest); }
            }
        foreach (var applications in new[] { "/Applications", Path.Combine(home, "Applications") }.Where(Directory.Exists))
            foreach (var app in Directory.EnumerateDirectories(applications, "*.app")) candidates.TryAdd(app, Path.GetFileNameWithoutExtension(app));
        var found = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (candidate, name) in candidates)
        {
            try
            {
                if (!Directory.Exists(candidate)) continue;
                var archive = FindArchiveDirectory(candidate);
                if (!Directory.Exists(archive) || !Directory.EnumerateFiles(archive).Any(x => Path.GetExtension(x).ToLowerInvariant() is ".pak" or ".utoc")) continue;
                var profile = Detect(archive);
                if (name.Length > 0) profile.Name = name;
                found.TryAdd(profile.Directory, profile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { Serilog.Log.Debug(error, "Could not inspect installed game {Path}", candidate); }
        }
        return found.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    // Accept installation roots as well as archive directories. Avoid treating
    // Chromium's resources.pak and other unrelated engine files as UE archives.
    public static GameProfile Detect(string selectedDirectory)
    {
        var selected = Path.GetFullPath(selectedDirectory);
        var archives = FindArchiveDirectory(selected);
        var name = new DirectoryInfo(archives).Name;
        if (name.Equals("Paks", StringComparison.OrdinalIgnoreCase))
            name = new DirectoryInfo(archives).Parent?.Parent?.Name ?? name;
        var profile = new GameProfile { Name = name, Directory = archives };
        for (var parent = new DirectoryInfo(archives); parent is not null; parent = parent.Parent)
        {
            var versionPath = Path.Combine(parent.FullName, "Engine", "Build", "Build.version");
            if (!File.Exists(versionPath)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(versionPath));
            var data = json.RootElement;
            var version = $"GAME_UE{data.GetProperty("MajorVersion").GetInt32()}_{data.GetProperty("MinorVersion").GetInt32()}";
            if (Enum.TryParse<EGame>(version, out var game)) profile.Version = game;
            if (data.TryGetProperty("BranchName", out var branch) && branch.GetString() is { } branchName && branchName.StartsWith("++Fortnite+Release-", StringComparison.Ordinal))
            {
                profile.Name = "Fortnite " + branchName["++Fortnite+Release-".Length..];
                // 24.20 is an intermediate engine build: its metadata says 5.1,
                // textures/audio use the 5.2 layout, while meshes retain these
                // earlier custom-version values and omit normal precision.
                if (branchName == "++Fortnite+Release-24.20")
                {
                    profile.Version = EGame.GAME_UE5_2;
                    profile.CustomVersions[FUE5MainStreamObjectVersion.GUID.ToString()] = (int)FUE5MainStreamObjectVersion.Type.WorldPartitionActorDescActorAndClassPaths;
                    profile.CustomVersions[FFortniteMainBranchObjectVersion.GUID.ToString()] = (int)FFortniteMainBranchObjectVersion.Type.WorldPartitionHLODActorDescSerializeStats;
                    profile.VersionOptions["Nanite.HasNormalPrecision"] = false;
                }
            }
            break;
        }
        return profile;
    }

    private static string FindArchiveDirectory(string selected, bool inspectBundles = true)
    {
        if (!System.IO.Directory.Exists(selected)) throw new DirectoryNotFoundException(selected);
        var direct = Path.Combine(selected, "Content", "Paks");
        if (System.IO.Directory.Exists(direct)) return direct;
        var projects = System.IO.Directory.EnumerateDirectories(selected)
            .Where(x => !Path.GetFileName(x).Equals("Engine", StringComparison.OrdinalIgnoreCase))
            .Select(x => Path.Combine(x, "Content", "Paks")).Where(System.IO.Directory.Exists).Take(2).ToArray();
        if (projects.Length == 1) return projects[0];
        if (!inspectBundles) return selected;
        var appRoots = selected.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? new[] { selected } :
            System.IO.Directory.EnumerateDirectories(selected, "*.app").ToArray();
        foreach (var app in appRoots)
            foreach (var engine in new[] { "UE4", "UE" })
            {
                var content = Path.Combine(app, "Contents", engine);
                if (!Directory.Exists(content)) continue;
                var path = FindArchiveDirectory(content, false);
                if (path != content) return path;
            }
        return selected;
    }
}
