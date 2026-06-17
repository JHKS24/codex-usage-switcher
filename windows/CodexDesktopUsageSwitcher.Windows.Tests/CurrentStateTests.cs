using System.Text;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class CurrentStateTests : IDisposable
{
    private readonly string _root;
    private readonly SwitcherPaths _paths;
    private readonly CurrentStateBuilder _builder;
    private readonly ProfileEnumerator _profiles;

    public CurrentStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "current-state-test-" + Guid.NewGuid().ToString("N"));
        var switchHome = Path.Combine(_root, ".codex-switch");
        _paths = new SwitcherPaths(
            CodexHome: Path.Combine(_root, ".codex"),
            SwitchHome: switchHome,
            ProfilesRoot: Path.Combine(switchHome, "profiles"),
            BackupRoot: Path.Combine(switchHome, "backups"),
            ActiveFile: Path.Combine(switchHome, "active"),
            ClaudeCredentials: Path.Combine(_root, "claude.json"),
            ClaudePending: Path.Combine(switchHome, "pending.json"),
            ClaudeCooldown: Path.Combine(switchHome, "cooldown.json"));
        _profiles = new ProfileEnumerator(_paths);
        _builder = new CurrentStateBuilder(_paths, _profiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string B64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string AuthContent(string email, string plan, string org, string accessToken)
    {
        var payload = $"{{\"email\":\"{email}\",\"https://api.openai.com/auth\":{{\"chatgpt_plan_type\":\"{plan}\"," +
                      $"\"organizations\":[{{\"is_default\":true,\"title\":\"{org}\"}}]}}}}";
        var idToken = "h." + B64Url(payload) + ".s";
        return "{\"tokens\":{\"id_token\":\"" + idToken + "\",\"account_id\":\"acc\",\"access_token\":\"" + accessToken + "\"}}";
    }

    private void WriteCodex(string content)
    {
        Directory.CreateDirectory(_paths.CodexHome);
        File.WriteAllText(_paths.TargetAuth, content, Encoding.UTF8);
    }

    private void WriteProfile(string name, string content)
    {
        Directory.CreateDirectory(_paths.ProfileDir(name));
        File.WriteAllText(_paths.ProfileAuth(name), content, Encoding.UTF8);
    }

    private void WriteActive(string name)
    {
        Directory.CreateDirectory(_paths.SwitchHome);
        File.WriteAllText(_paths.ActiveFile, name + "\n");
    }

    [Fact]
    public void Build_matches_by_hash_when_bytes_are_identical()
    {
        var content = AuthContent("a@b.com", "pro", "Acme", "TOK");
        WriteCodex(content);
        WriteProfile("work", content);
        WriteActive("work");

        var state = _builder.Build(codexRunning: false);

        Assert.Equal("hash", state.MatchMethod);
        Assert.Equal("work", state.MatchedProfile);
        Assert.Equal("matched", state.AuthMatch);
        Assert.False(state.CodexRunning);
    }

    [Fact]
    public void Build_falls_back_to_identity_when_bytes_differ()
    {
        // Same identity (email/plan/org) but a different access token -> bytes differ, identity matches.
        WriteCodex(AuthContent("a@b.com", "pro", "Acme", "TOK-1"));
        WriteProfile("work", AuthContent("a@b.com", "pro", "Acme", "TOK-2"));
        WriteActive("work");

        var state = _builder.Build(codexRunning: null);

        Assert.Equal("identity", state.MatchMethod);
        Assert.Equal("work", state.MatchedProfile);
        Assert.Equal("matched", state.AuthMatch);
    }

    [Fact]
    public void Build_reports_mismatch_when_active_differs_from_matched()
    {
        WriteCodex(AuthContent("a@b.com", "pro", "Acme", "TOK"));
        WriteProfile("edu", AuthContent("a@b.com", "pro", "Acme", "TOK")); // hash match -> edu
        WriteActive("work"); // but active says work

        var state = _builder.Build(codexRunning: false);

        Assert.Equal("edu", state.MatchedProfile);
        Assert.Equal("mismatch", state.AuthMatch);
    }

    [Fact]
    public void Build_is_unknown_without_target_or_profiles()
    {
        var state = _builder.Build(codexRunning: null);

        Assert.Equal("unknown", state.ActiveLabel);
        Assert.Null(state.MatchedProfile);
        Assert.Equal("unknown", state.AuthMatch);
        Assert.Null(state.MatchMethod);
    }

    [Fact]
    public void Enumerator_lists_profiles_with_existence_flags()
    {
        WriteProfile("work", AuthContent("a@b.com", "pro", "Acme", "T"));
        Directory.CreateDirectory(_paths.ProfileDir("empty")); // dir without auth.json

        var list = _profiles.List();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, p => p.Name == "work" && p.Exists);
        Assert.Contains(list, p => p.Name == "empty" && !p.Exists);
    }
}
