using System.Reflection;

namespace AssetBeeDrone.Updating;

public static class AppVersion
{
    public static Version Current { get; } = ReadCurrent();

    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string core = value.Trim();
        int plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        return Version.TryParse(core, out version!);
    }

    public static bool IsNewer(string candidateVersion, Version current)
    {
        return TryParse(candidateVersion, out Version remote) && remote > current;
    }

    private static Version ReadCurrent()
    {
        Assembly assembly = typeof(AppVersion).Assembly;
        string? informational =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
        if (TryParse(informational, out Version fromInfo))
        {
            return fromInfo;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion ?? new Version(0, 0, 0);
    }
}
