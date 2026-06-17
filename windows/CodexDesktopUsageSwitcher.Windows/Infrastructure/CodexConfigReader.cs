namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Reads cli_auth_credentials_store from ~/.codex/config.toml. When Codex is configured to store
// credentials in the OS keyring (not the auth.json file), file-based profile switching is unsafe
// and must be refused — same gate as the original tool.
internal static class CodexConfigReader
{
    public static string? CredentialsStore(string codexHome)
    {
        var config = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(config))
        {
            return null;
        }

        try
        {
            foreach (var raw in File.ReadLines(config))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || !line.StartsWith("cli_auth_credentials_store", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                return line[(separator + 1)..].Trim().Trim('"', '\'').ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    public static bool FileSwitchingDisabled(string? store) =>
        store == "keyring" || (store is not null && store != "file" && store != "auto");
}
