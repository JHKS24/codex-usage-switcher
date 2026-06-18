using System.Text;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class ClaudeHistoryReaderTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _projectsDir;

    public ClaudeHistoryReaderTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "claude-reader-test-" + Guid.NewGuid().ToString("N"));
        _projectsDir = Path.Combine(_configDir, "projects", "demo");
        Directory.CreateDirectory(_projectsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Fact]
    public void Read_flattens_usage_dedups_and_groups_turns()
    {
        // Layout of the synthetic file:
        //  1) usage line before any user input  -> turn 0
        //  2) a real user-text line             -> turn becomes 1
        //  3) usage line after the boundary     -> turn 1
        //  4) exact duplicate of (3) by id      -> skipped (dedup)
        //  5) a tool-result "user" line         -> NOT a turn boundary
        var file = Path.Combine(_projectsDir, "session.jsonl");
        var lines = new[]
        {
            UsageLine("msg-a", "req-1", "claude-opus-4", "2026-06-16T10:00:00.000Z", input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheCreate: 1_000_000),
            UserTextLine("hello there", "2026-06-16T10:01:00.000Z"),
            UsageLine("msg-b", "req-2", "claude-opus-4", "2026-06-16T10:02:00.000Z", input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheCreate: 1_000_000),
            UsageLine("msg-b", "req-2", "claude-opus-4", "2026-06-16T10:02:00.000Z", input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheCreate: 1_000_000),
            ToolResultLine("2026-06-16T10:03:00.000Z"),
        };
        File.WriteAllText(file, string.Join('\n', lines) + '\n', Encoding.UTF8);

        var entries = ClaudeHistoryReader.Read(_configDir);

        Assert.Equal(2, entries.Count); // the duplicate (4) was dropped
        Assert.All(entries, e => Assert.Equal("claude-opus-4", e.Model));
        // total = input + output + cacheRead + cacheCreate.
        Assert.All(entries, e => Assert.Equal(4_000_000, e.TotalTokens));
        Assert.All(entries, e => Assert.Equal(1_000_000, e.OutputTokens));
        // opus cost: (1e6*15 + 1e6*75 + 1e6*1.5)/1e6 + 1e6*18.75/1e6 = 110.25.
        Assert.All(entries, e => Assert.Equal(110.25, e.CostUsd!.Value, 6));

        // First usage line precedes the user input -> turn 0; the second -> turn 1.
        Assert.Equal($"{file}#0", entries[0].TurnKey);
        Assert.Equal($"{file}#1", entries[1].TurnKey);
    }

    [Theory]
    [InlineData("claude-opus-4-20250101", 15d, 75d, 18.75d, 30d, 1.5d)]
    [InlineData("claude-sonnet-4-5", 3d, 15d, 3.75d, 6d, 0.3d)]
    [InlineData("claude-haiku-3-5", 1d, 5d, 1.25d, 2d, 0.1d)]
    [InlineData("some-future-model", 3d, 15d, 3.75d, 6d, 0.3d)] // unknown -> sonnet default
    public void Cost_uses_correct_rate_bucket(
        string model,
        double inRate,
        double outRate,
        double cw5Rate,
        double cw1Rate,
        double crRate)
    {
        // 1M of each token kind, with a SPLIT cache (5m + 1h) to exercise both
        // cache-write branches: cost = in + out + cr + cw5 + cw1.
        var file = Path.Combine(_projectsDir, "rates.jsonl");
        var line = UsageLineSplitCache(
            "msg-1",
            "req-1",
            model,
            "2026-06-16T09:00:00.000Z",
            input: 1_000_000,
            output: 1_000_000,
            cacheRead: 1_000_000,
            cache5m: 1_000_000,
            cache1h: 1_000_000);
        File.WriteAllText(file, line + '\n', Encoding.UTF8);

        var entry = Assert.Single(ClaudeHistoryReader.Read(_configDir));

        var expected = inRate + outRate + crRate + cw5Rate + cw1Rate;
        Assert.Equal(expected, entry.CostUsd!.Value, 6);
        // total = input + output + cacheRead + cacheCreate, where (with only the
        // split cache present) cacheCreate falls back to cache5m + cache1h = 2M.
        // So total = 1M + 1M + 1M + 2M = 5M (matches claudeService.ts:511,715).
        Assert.Equal(5_000_000, entry.TotalTokens);
    }

    [Fact]
    public void Read_returns_empty_when_projects_dir_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "claude-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(ClaudeHistoryReader.Read(missing));
    }

    [Fact]
    public void Read_skips_malformed_lines()
    {
        var file = Path.Combine(_projectsDir, "broken.jsonl");
        var lines = new[]
        {
            "not json at all",
            "{ this is : broken",
            "",
            UsageLine("msg-x", "req-x", "claude-sonnet-4", "2026-06-16T08:00:00.000Z", input: 10, output: 5, cacheRead: 0, cacheCreate: 0),
        };
        File.WriteAllText(file, string.Join('\n', lines) + '\n', Encoding.UTF8);

        var entry = Assert.Single(ClaudeHistoryReader.Read(_configDir));
        Assert.Equal(15, entry.TotalTokens);
    }

    private static string UsageLine(
        string id,
        string requestId,
        string model,
        string timestamp,
        long input,
        long output,
        long cacheRead,
        long cacheCreate)
    {
        return "{" +
            $"\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"requestId\":\"{requestId}\"," +
            "\"message\":{" +
            $"\"id\":\"{id}\",\"model\":\"{model}\",\"usage\":{{" +
            $"\"input_tokens\":{input},\"output_tokens\":{output}," +
            $"\"cache_read_input_tokens\":{cacheRead},\"cache_creation_input_tokens\":{cacheCreate}}}}}}}";
    }

    private static string UsageLineSplitCache(
        string id,
        string requestId,
        string model,
        string timestamp,
        long input,
        long output,
        long cacheRead,
        long cache5m,
        long cache1h)
    {
        return "{" +
            $"\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"requestId\":\"{requestId}\"," +
            "\"message\":{" +
            $"\"id\":\"{id}\",\"model\":\"{model}\",\"usage\":{{" +
            $"\"input_tokens\":{input},\"output_tokens\":{output}," +
            $"\"cache_read_input_tokens\":{cacheRead}," +
            $"\"cache_creation\":{{\"ephemeral_5m_input_tokens\":{cache5m},\"ephemeral_1h_input_tokens\":{cache1h}}}}}}}}}";
    }

    private static string UserTextLine(string text, string timestamp)
    {
        return "{" +
            $"\"type\":\"user\",\"timestamp\":\"{timestamp}\"," +
            $"\"message\":{{\"content\":\"{text}\"}}}}";
    }

    private static string ToolResultLine(string timestamp)
    {
        // type=user but carries a toolUseResult -> must NOT start a new turn.
        return "{" +
            $"\"type\":\"user\",\"timestamp\":\"{timestamp}\",\"toolUseResult\":{{\"ok\":true}}," +
            "\"message\":{\"content\":\"tool output\"}}";
    }
}
