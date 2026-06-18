using System.Text;
using System.Text.Json.Nodes;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class NativeSwitcherClientTests : IDisposable
{
    private readonly string _root;
    private readonly SwitcherPaths _paths;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public NativeSwitcherClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "native-switcher-test-" + Guid.NewGuid().ToString("N"));
        var switchHome = Path.Combine(_root, ".codex-switch");
        _paths = new SwitcherPaths(
            CodexHome: Path.Combine(_root, ".codex"),
            SwitchHome: switchHome,
            ProfilesRoot: Path.Combine(switchHome, "profiles"),
            BackupRoot: Path.Combine(switchHome, "backups"),
            ActiveFile: Path.Combine(switchHome, "active"),
            ClaudeCredentials: Path.Combine(_root, "claude", "credentials.json"),
            ClaudePending: Path.Combine(switchHome, "pending.json"),
            ClaudeCooldown: Path.Combine(switchHome, "cooldown.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeLister(IReadOnlyList<CodexProcessRow>? rows) : IProcessLister
    {
        public IReadOnlyList<CodexProcessRow>? List() => rows;
    }

    private sealed class RouteHttp : IHttpJsonClient
    {
        public Task<HttpJsonResponse> SendAsync(HttpJsonRequest request, CancellationToken cancellationToken)
        {
            var body = request.Url.Contains("wham")
                ? """{"rate_limit":{"primary_window":{"used_percent":25,"reset_after_seconds":3600},"secondary_window":{"used_percent":90}}}"""
                : request.Url.Contains("oauth/usage")
                    ? """{"five_hour":{"utilization":40,"resets_at":"R"},"seven_day":{"utilization":70,"resets_at":"R7"}}"""
                    : "{}";
            return Task.FromResult(new HttpJsonResponse(true, 200, body, null));
        }
    }

    private static IProcessLister None => new FakeLister([]);
    private static IProcessLister CannotInspect => new FakeLister(null);
    private static IProcessLister CodexUp => new FakeLister([new(1234, "Codex.exe", @"C:\Programs\Codex\Codex.exe")]);

    private NativeSwitcherClient Build(IProcessLister lister)
    {
        var profiles = new ProfileEnumerator(_paths);
        return new NativeSwitcherClient(
            _paths,
            profiles,
            new CurrentStateBuilder(_paths, profiles),
            new SensitiveFileStore(_paths),
            new CodexProcessProbe(lister, selfPid: 999999),
            new CodexUsageClient(new RouteHttp()),
            new ClaudeUsageClient(new ClaudeCredentialStore(_paths.ClaudeCredentials), new RouteHttp()));
    }

    private static string AuthContent(string token)
    {
        var payload = "{\"email\":\"a@b.com\",\"https://api.openai.com/auth\":{\"chatgpt_plan_type\":\"pro\"}}";
        var idToken = "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
        return "{\"tokens\":{\"id_token\":\"" + idToken + "\",\"account_id\":\"acc\",\"access_token\":\"" + token + "\"}}";
    }

    private void SeedProfile(string name, string token)
    {
        Directory.CreateDirectory(_paths.ProfileDir(name));
        File.WriteAllText(_paths.ProfileAuth(name), AuthContent(token), Encoding.UTF8);
    }

    private async Task<(JsonNode Json, int Exit)> Run(IProcessLister lister, params string[] args)
    {
        var result = await Build(lister).RunAsync(args, Budget, CancellationToken.None);
        return (JsonNode.Parse(result.Stdout)!, result.ExitCode);
    }

    [Fact]
    public async Task List_returns_seeded_profiles()
    {
        SeedProfile("work", "T");
        SeedProfile("edu", "T");

        var (json, exit) = await Run(None, "list", "--json");

        Assert.Equal(0, exit);
        Assert.Equal(2, json["profiles"]!.AsArray().Count);
    }

    [Fact]
    public async Task Current_is_unknown_when_nothing_configured()
    {
        var (json, _) = await Run(None, "current", "--json");

        Assert.Equal("unknown", json["active_label"]!.GetValue<string>());
        Assert.Equal("unknown", json["auth_match"]!.GetValue<string>());
        Assert.False(json["codex_running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Usage_reports_remaining_percent()
    {
        SeedProfile("work", "T");

        var (json, _) = await Run(None, "usage", "--json");

        var row = json["usage"]!.AsArray()[0]!;
        Assert.Equal("work", row["profile"]!.GetValue<string>());
        Assert.Equal(75, row["five_hour_left"]!.GetValue<int>());
        Assert.Equal(10, row["weekly_left"]!.GetValue<int>());
    }

    [Fact]
    public async Task ClaudeUsage_is_login_required_without_credentials()
    {
        var (json, _) = await Run(None, "claude-usage", "--json");

        Assert.False(json["authenticated"]!.GetValue<bool>());
        Assert.Equal("login_required", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Snapshot_carries_every_section()
    {
        SeedProfile("work", "T");
        new ClaudeCredentialStore(_paths.ClaudeCredentials)
            .Save(new ClaudeCredentials("AT", "RT", null, ClaudeCredentialStore.DefaultScopes));

        var (json, _) = await Run(None, "snapshot", "--json");

        Assert.NotNull(json["profiles"]);
        Assert.NotNull(json["current"]);
        Assert.Single(json["usage"]!.AsArray());
        Assert.True(json["claude_usage"]!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StopCodex_is_ok_when_nothing_running()
    {
        var (json, exit) = await Run(None, "stop-codex", "--json", "--grace-seconds", "0");

        Assert.Equal(0, exit);
        Assert.True(json["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Use_switches_profile_when_codex_is_not_running()
    {
        SeedProfile("work", "PROFILE-TOKEN");
        Directory.CreateDirectory(_paths.CodexHome);
        File.WriteAllText(_paths.TargetAuth, AuthContent("OLD-TOKEN"), Encoding.UTF8);

        var (json, exit) = await Run(None, "use", "work", "--apply", "--json");

        Assert.Equal(0, exit);
        Assert.Equal("work", json["switched"]!.GetValue<string>());
        Assert.Contains("PROFILE-TOKEN", File.ReadAllText(_paths.TargetAuth));
    }

    [Fact]
    public async Task Use_refuses_when_codex_is_running()
    {
        SeedProfile("work", "T");

        var (json, exit) = await Run(CodexUp, "use", "work", "--apply", "--json");

        Assert.NotEqual(0, exit);
        Assert.Contains("quit Codex", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Use_refuses_when_processes_cannot_be_inspected()
    {
        SeedProfile("work", "T");

        var (json, exit) = await Run(CannotInspect, "use", "work", "--apply", "--json");

        Assert.NotEqual(0, exit);
        Assert.Contains("inspect", json["error"]!.GetValue<string>());
    }
}
