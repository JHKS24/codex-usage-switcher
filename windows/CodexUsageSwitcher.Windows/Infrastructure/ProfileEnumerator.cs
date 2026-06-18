namespace CodexUsageSwitcher.Windows.Infrastructure;

internal readonly record struct ProfileEntry(string Name, bool Exists);

// Enumerates saved profiles under ~/.codex-switch/profiles, name-sorted, the single source of the
// profile directory list shared by `list`, `current`, and `usage`. A missing or unreadable
// profiles root is just an empty list, never a crash.
internal sealed class ProfileEnumerator
{
    private readonly SwitcherPaths _paths;

    public ProfileEnumerator(SwitcherPaths paths) => _paths = paths;

    public IReadOnlyList<string> DirectoryPaths()
    {
        if (!Directory.Exists(_paths.ProfilesRoot))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(_paths.ProfilesRoot)
                .GetDirectories()
                .OrderBy(directory => directory.Name, StringComparer.Ordinal)
                .Select(directory => directory.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<ProfileEntry> List() =>
        DirectoryPaths()
            .Select(path => new ProfileEntry(
                Path.GetFileName(path),
                File.Exists(Path.Combine(path, SwitcherPaths.AuthName))))
            .ToArray();
}
