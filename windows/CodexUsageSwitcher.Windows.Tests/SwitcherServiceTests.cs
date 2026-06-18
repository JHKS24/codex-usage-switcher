using CodexUsageSwitcher.Windows.Application;
using CodexUsageSwitcher.Windows.Domain;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class SwitcherServiceTests
{
    private const string SnapshotJson = """
        {
          "ok": true,
          "profiles": [
            {"profile": "main", "exists": true},
            {"profile": "sub", "exists": false}
          ],
          "current": {
            "active_label": "main",
            "matched_profile": "main",
            "auth_match": "matched",
            "codex_running": true
          },
          "usage": [
            {"profile": "main", "plan": "plus", "five_hour_left": 80, "weekly_left": 55, "weekly_reset": null, "error": null},
            {"profile": "sub", "plan": null, "five_hour_left": null, "weekly_left": null, "weekly_reset": null, "error": "Profile sub is missing auth tokens."}
          ],
          "claude_usage": {
            "ok": true,
            "authenticated": true,
            "five_hour": {"utilization": 25.0},
            "seven_day": {"utilization": 40.0}
          }
        }
        """;

    [Fact]
    public async Task LoadSnapshot_UsesCombinedCommand_AndParsesEverySection()
    {
        var client = new FakeSwitcherClient().Respond("snapshot", 0, SnapshotJson);
        var service = CreateService(client);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        Assert.Equal(["snapshot"], client.Commands);
        Assert.Equal(2, snapshot.Profiles.Count);
        Assert.Equal("main", snapshot.Profiles[0].Name);
        Assert.True(snapshot.Profiles[0].HasAuth);
        Assert.Equal("main", snapshot.Current.MatchedProfile);
        Assert.Equal(80, snapshot.UsageRows[0].FiveHourLeft);
        Assert.True(snapshot.ClaudeUsage.Authenticated);
        Assert.Equal(75, snapshot.ClaudeUsage.FiveHourLeft);
        Assert.Equal(60, snapshot.ClaudeUsage.WeeklyLeft);
    }

    [Fact]
    public async Task LoadSnapshot_FallsBackToPerCommandCli_WhenSnapshotUnavailable()
    {
        var client = new FakeSwitcherClient()
            .Respond("list", 0, """{"ok": true, "profiles": [{"profile": "main", "exists": true}]}""")
            .Respond("current", 0, """{"ok": true, "active_label": "main", "matched_profile": "main", "auth_match": "matched", "codex_running": false}""")
            .Respond("usage", 0, """{"ok": true, "usage": [{"profile": "main", "plan": "plus", "five_hour_left": 70, "weekly_left": 50}]}""")
            .Respond("claude-usage", 0, """{"ok": true, "authenticated": false, "error": "login_required"}""");
        var service = CreateService(client);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        Assert.Equal("snapshot", client.Commands[0]);
        Assert.Equal(
            new[] { "claude-usage", "current", "list", "usage" },
            client.Commands.Skip(1).Order().ToArray());
        Assert.Single(snapshot.Profiles);
        Assert.Equal(70, snapshot.UsageRows[0].FiveHourLeft);
        Assert.False(snapshot.ClaudeUsage.Authenticated);
        Assert.Equal("로그인 필요", snapshot.ClaudeUsage.Message);
    }

    [Fact]
    public async Task LoadSnapshot_DegradesEverySection_WhenBackendEmitsNothing()
    {
        var client = new FakeSwitcherClient();
        var service = CreateService(client);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Profiles);
        Assert.Empty(snapshot.UsageRows);
        Assert.Equal("unknown", snapshot.Current.ActiveLabel);
        Assert.False(snapshot.ClaudeUsage.Authenticated);
        Assert.Equal("조회 실패", snapshot.ClaudeUsage.Message);
    }

    [Fact]
    public async Task LoadSnapshot_ReportsTransientClaudeNetworkFailure_NotLoginRequired()
    {
        var json = """
            {
              "ok": true,
              "profiles": [],
              "current": {"active_label": "main", "auth_match": "matched"},
              "usage": [],
              "claude_usage": {"ok": false, "authenticated": true, "error": "network", "message": "timed out"}
            }
            """;
        var client = new FakeSwitcherClient().Respond("snapshot", 0, json);
        var service = CreateService(client);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.ClaudeUsage.Authenticated);
        Assert.Null(snapshot.ClaudeUsage.FiveHourLeft);
        Assert.Equal("네트워크 오류 · 잠시 후 재시도", snapshot.ClaudeUsage.Message);
    }

    [Fact]
    public async Task SwitchProfile_RunsStopUseOpen_InOrder()
    {
        var client = new FakeSwitcherClient()
            .Respond("stop-codex", 0, """{"ok": true}""")
            .Respond("use", 0, """{"ok": true, "switched": "sub"}""")
            .Respond("open", 0, "opened");
        var service = CreateService(client);

        var outcome = await service.SwitchProfileAsync("sub", CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(["stop-codex", "use", "open"], client.Commands);
        Assert.Contains("전환 완료", outcome.Message);
    }

    [Fact]
    public async Task SwitchProfile_StopFailure_AbortsBeforeUse()
    {
        var client = new FakeSwitcherClient()
            .Respond("stop-codex", 13, """{"ok": false, "error": "could not inspect running processes"}""");
        var service = CreateService(client);

        var outcome = await service.SwitchProfileAsync("sub", CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(["stop-codex"], client.Commands);
        Assert.Contains("could not inspect", outcome.Message);
    }

    [Fact]
    public async Task SwitchProfile_OpenFailure_StillReportsSwitchSuccess()
    {
        var client = new FakeSwitcherClient()
            .Respond("stop-codex", 0, """{"ok": true}""")
            .Respond("use", 0, """{"ok": true}""")
            .Respond("open", 12, "", "Codex Desktop app not found");
        var service = CreateService(client);

        var outcome = await service.SwitchProfileAsync("sub", CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Contains("자동 실행 실패", outcome.Message);
    }

    [Fact]
    public async Task LoadSnapshot_BuildsTrayMetrics_RespectingVisibility()
    {
        var client = new FakeSwitcherClient().Respond("snapshot", 0, SnapshotJson);
        var service = CreateService(client, visibleMetrics: ["codex:5h", "claude:week"]);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        var visible = snapshot.TrayMetrics.Where(metric => metric.Visible).Select(metric => metric.Key).ToArray();
        Assert.Equal(["codex:5h", "claude:week"], visible);
        var codexFive = snapshot.TrayMetrics.Single(metric => metric.Key == "codex:5h");
        Assert.Equal(80, codexFive.RemainingPercent);
    }

    [Fact]
    public async Task LoadSnapshot_BuildsCodexTrayMetrics_ForEverySavedProfile()
    {
        const string json = """
            {
              "ok": true,
              "profiles": [
                {"profile": "alpha", "exists": true},
                {"profile": "beta", "exists": true},
                {"profile": "gamma", "exists": true}
              ],
              "current": {
                "active_label": "alpha",
                "matched_profile": "alpha",
                "auth_match": "matched"
              },
              "usage": [
                {"profile": "alpha", "five_hour_left": 10, "weekly_left": 20, "error": null},
                {"profile": "beta", "five_hour_left": 30, "weekly_left": 40, "error": null},
                {"profile": "gamma", "five_hour_left": 50, "weekly_left": 60, "error": null}
              ],
              "claude_usage": {
                "ok": true,
                "authenticated": true
              }
            }
            """;
        var client = new FakeSwitcherClient().Respond("snapshot", 0, json);
        var service = CreateService(client, visibleMetrics: ["codexsub:week", "codexprofile:gamma:5h"]);

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        Assert.Equal(
            [
                "codex:5h",
                "codex:week",
                "codexsub:5h",
                "codexsub:week",
                "codexprofile:gamma:5h",
                "codexprofile:gamma:week",
                "claude:5h",
                "claude:week",
            ],
            snapshot.TrayMetrics.Select(metric => metric.Key).ToArray());

        var betaWeek = snapshot.TrayMetrics.Single(metric => metric.Key == "codexsub:week");
        Assert.True(betaWeek.Visible);
        Assert.Equal(40, betaWeek.RemainingPercent);
        Assert.Equal("프로필 beta", betaWeek.Detail);

        var gammaFiveHour = snapshot.TrayMetrics.Single(metric => metric.Key == "codexprofile:gamma:5h");
        Assert.True(gammaFiveHour.Visible);
        Assert.Equal(50, gammaFiveHour.RemainingPercent);
        Assert.Equal("프로필 gamma", gammaFiveHour.Detail);
    }

    private static SwitcherService CreateService(FakeSwitcherClient client, string[]? visibleMetrics = null)
    {
        return new SwitcherService(
            client,
            new FakeInteractiveCliLauncher(),
            new FakeSettingsStore(visibleMetrics ?? ["codex:5h", "codex:week"]));
    }

    private sealed class FakeInteractiveCliLauncher : IInteractiveCliLauncher
    {
        public CommandOutcome LaunchClaudeLogin() => new(true, 0, "launched");
        public CommandOutcome LaunchClaudeCodeLogin() => new(true, 0, "launched");
        public CommandOutcome LaunchCodexLogin(string profile) => new(true, 0, "launched");
        public CommandOutcome LaunchSaveCurrentProfile(string profile) => new(true, 0, "launched");
    }

    private sealed class FakeSettingsStore(string[] visibleMetrics) : ISettingsStore
    {
        private readonly HashSet<string> _metrics = new(visibleMetrics, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlySet<string>> LoadTrayMetricVisibilityAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlySet<string>>(_metrics);
        }

        public Task SetTrayMetricVisibilityAsync(string metricKey, bool visible, CancellationToken cancellationToken)
        {
            if (visible)
            {
                _metrics.Add(metricKey);
            }
            else
            {
                _metrics.Remove(metricKey);
            }

            return Task.CompletedTask;
        }

        public Task<string?> LoadLanguageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task SetLanguageAsync(string language, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
