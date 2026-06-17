namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// The live Codex auth state: which saved profile (if any) the current ~/.codex/auth.json belongs
// to, and whether the active marker agrees. Shaped to the JSON the app already consumes
// (active_label / matched_profile / auth_match / codex_running).
internal readonly record struct CodexCurrentState(
    string ActiveLabel,
    string? MatchedProfile,
    string AuthMatch,
    bool? CodexRunning,
    IReadOnlyList<string> MatchedProfiles,
    IReadOnlyList<string> IdentityMatchedProfiles,
    string? MatchMethod);

// Matches the live auth.json to a saved profile, preferring an exact byte (hash) match and falling
// back to identity (email/plan/org from the id_token). Mirrors the original tool's logic; reads
// only file hashes and public identity fields — never token contents.
internal sealed class CurrentStateBuilder
{
    private readonly SwitcherPaths _paths;
    private readonly ProfileEnumerator _profiles;

    public CurrentStateBuilder(SwitcherPaths paths, ProfileEnumerator profiles)
    {
        _paths = paths;
        _profiles = profiles;
    }

    public CodexCurrentState Build(bool? codexRunning)
    {
        var active = ReadActive();
        var targetDigest = CodexAuthReader.Sha256(_paths.TargetAuth);
        var currentIdentity = IdentityOf(CodexAuthReader.Read(_paths.CodexHome));

        var hashMatched = new List<string>();
        var identityMatched = new List<string>();
        foreach (var directory in _profiles.DirectoryPaths())
        {
            var name = Path.GetFileName(directory);
            var digest = CodexAuthReader.Sha256(Path.Combine(directory, SwitcherPaths.AuthName));
            if (targetDigest is not null && digest is not null && targetDigest == digest)
            {
                hashMatched.Add(name);
                continue;
            }

            if (IdentitiesMatch(currentIdentity, IdentityOf(CodexAuthReader.Read(directory))))
            {
                identityMatched.Add(name);
            }
        }

        var (method, effective) = Resolve(hashMatched, identityMatched);
        var matchedProfile = effective.Count == 1 ? effective[0] : null;
        return new CodexCurrentState(
            active,
            matchedProfile,
            DetermineAuthMatch(active, effective, matchedProfile),
            codexRunning,
            hashMatched,
            identityMatched,
            method);
    }

    private static (string? Method, IReadOnlyList<string> Effective) Resolve(
        IReadOnlyList<string> hashMatched, IReadOnlyList<string> identityMatched)
    {
        if (hashMatched.Count > 0)
        {
            return ("hash", hashMatched);
        }

        return identityMatched.Count > 0 ? ("identity", identityMatched) : (null, []);
    }

    private static string DetermineAuthMatch(string active, IReadOnlyList<string> effective, string? matchedProfile)
    {
        if (active != "unknown")
        {
            return effective.Contains(active) ? "matched" : "mismatch";
        }

        return matchedProfile is not null ? "matched" : "unknown";
    }

    private string ReadActive()
    {
        try
        {
            return File.Exists(_paths.ActiveFile) ? File.ReadAllText(_paths.ActiveFile).Trim() : "unknown";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private readonly record struct Identity(string? Email, string? Plan, string? Organization);

    private static Identity? IdentityOf(CodexAuthSummary? summary) =>
        summary is null || summary.Error is not null
            ? null
            : new Identity(summary.Email, summary.Plan, summary.Organization);

    private static bool IdentitiesMatch(Identity? left, Identity? right)
    {
        if (left is not { } a || right is not { } b)
        {
            return false;
        }

        if (string.IsNullOrEmpty(a.Email) || a.Email != b.Email)
        {
            return false;
        }

        if (a.Plan != b.Plan)
        {
            return false;
        }

        return string.IsNullOrEmpty(a.Organization) || string.IsNullOrEmpty(b.Organization) || a.Organization == b.Organization;
    }
}
