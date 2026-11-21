using System.Text.Json;

namespace FModel.Desktop;

internal static class GameProfiles
{
    internal static GameProfile Clone(GameProfile profile) => JsonSerializer.Deserialize<GameProfile>(JsonSerializer.Serialize(profile, DesktopSettings.JsonOptions), DesktopSettings.JsonOptions)!;

    public static GameProfile Load(string path)
    {
        var absolute = Path.GetFullPath(path);
        var profile = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(absolute), DesktopSettings.JsonOptions)
            ?? throw new InvalidDataException("The game profile is empty.");
        var directory = Path.GetDirectoryName(absolute)!;
        if (profile.LiveGame.Length == 0) profile.Directory = Path.GetFullPath(profile.Directory, directory);
        if (profile.MappingsPath.Length > 0) profile.MappingsPath = Path.GetFullPath(profile.MappingsPath, directory);
        return profile;
    }
}
