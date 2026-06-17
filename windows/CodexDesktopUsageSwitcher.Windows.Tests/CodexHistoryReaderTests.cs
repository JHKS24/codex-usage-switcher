using System.Text;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class CodexHistoryReaderTests : IDisposable
{
    private readonly string _codexHome;
    private readonly string _sessionsDir;

    public CodexHistoryReaderTests()
    {
        _codexHome = Path.Combine(Path.GetTempPath(), "codex-reader-test-" + Guid.NewGuid().ToString("N"));
        _sessionsDir = Path.Combine(_codexHome, "sessions", "2026", "06", "16");
        Directory.CreateDirectory(_sessionsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_codexHome))
        {
            Directory.Delete(_codexHome, recursive: true);
        }
    }

    [Fact]
    public void Read_accumulates_state_and_maps_turn_models()
    {
        // session_meta sets a fallback model; turn_context maps turn-1 -> a specific
        // model; task_started selects turn-1; two token_count events follow. Each
        // token_count emits one entry using last_token_usage as the per-event delta.
        var file = Path.Combine(_sessionsDir, "rollout-2026-06-16T09-00-00-abcdef.jsonl");
        var lines = new[]
        {
            SessionMetaLine("gpt-5-codex", "2026-06-16T09:00:00.000Z"),
            TurnContextLine("turn-1", "gpt-5.1-codex", "2026-06-16T09:00:05.000Z"),
            TaskStartedLine("turn-1", "2026-06-16T09:00:06.000Z"),
            TokenCountLine("2026-06-16T09:00:10.000Z", input: 100, output: 40, total: 140),
            TokenCountLine("2026-06-16T09:00:20.000Z", input: 200, output: 60, total: 260),
        };
        File.WriteAllText(file, string.Join('\n', lines) + '\n', Encoding.UTF8);

        var entries = CodexHistoryReader.Read(_codexHome);

        Assert.Equal(2, entries.Count);
        // Model comes from the turn-1 mapping, not the session_meta fallback.
        Assert.All(entries, e => Assert.Equal("gpt-5.1-codex", e.Model));
        // Codex semantics: TotalTokens = total_tokens, OutputTokens = output_tokens.
        Assert.Equal(140, entries[0].TotalTokens);
        Assert.Equal(40, entries[0].OutputTokens);
        Assert.Equal(260, entries[1].TotalTokens);
        Assert.Equal(60, entries[1].OutputTokens);
        // Cost is never shown for Codex.
        Assert.All(entries, e => Assert.Null(e.CostUsd));
        // TurnKey = <file>#<turnId> for both events of the same turn.
        Assert.All(entries, e => Assert.Equal($"{file}#turn-1", e.TurnKey));
    }

    [Fact]
    public void Read_falls_back_to_session_model_without_turn_context()
    {
        // No turn_context / task_started: model falls back to session_meta and the
        // turn key is null (excluded from turn stats).
        var file = Path.Combine(_sessionsDir, "rollout-2026-06-16T11-00-00-noturn.jsonl");
        var lines = new[]
        {
            SessionMetaLine("gpt-5-codex", "2026-06-16T11:00:00.000Z"),
            TokenCountLine("2026-06-16T11:00:10.000Z", input: 10, output: 5, total: 15),
        };
        File.WriteAllText(file, string.Join('\n', lines) + '\n', Encoding.UTF8);

        var entry = Assert.Single(CodexHistoryReader.Read(_codexHome));
        Assert.Equal("gpt-5-codex", entry.Model);
        Assert.Equal(15, entry.TotalTokens);
        Assert.Null(entry.TurnKey);
    }

    [Fact]
    public void Read_withholds_a_final_line_without_a_trailing_newline_until_it_is_completed()
    {
        // A crashed/abandoned rollout whose final token_count line never got its '\n': the event
        // is withheld (deliberate — the incremental cache re-reads it once the line is completed).
        var file = Path.Combine(_sessionsDir, "rollout-2026-06-16T12-00-00-tail.jsonl");
        var head = SessionMetaLine("gpt-5-codex", "2026-06-16T12:00:00.000Z") + "\n";
        var lastEvent = TokenCountLine("2026-06-16T12:00:10.000Z", input: 10, output: 5, total: 15);

        File.WriteAllText(file, head + lastEvent, Encoding.UTF8); // no trailing '\n' -> withheld
        Assert.Empty(CodexHistoryReader.Read(_codexHome));

        File.WriteAllText(file, head + lastEvent + "\n", Encoding.UTF8); // terminated -> included
        var entry = Assert.Single(CodexHistoryReader.Read(_codexHome));
        Assert.Equal(15, entry.TotalTokens);
    }

    [Fact]
    public void Read_returns_empty_when_sessions_dir_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "codex-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(CodexHistoryReader.Read(missing));
    }

    private static string SessionMetaLine(string model, string timestamp)
    {
        return "{" +
            $"\"type\":\"session_meta\",\"timestamp\":\"{timestamp}\"," +
            $"\"payload\":{{\"id\":\"sess-1\",\"model\":\"{model}\"}}}}";
    }

    private static string TurnContextLine(string turnId, string model, string timestamp)
    {
        return "{" +
            $"\"type\":\"turn_context\",\"timestamp\":\"{timestamp}\"," +
            $"\"payload\":{{\"turn_id\":\"{turnId}\",\"model\":\"{model}\"}}}}";
    }

    private static string TaskStartedLine(string turnId, string timestamp)
    {
        return "{" +
            $"\"timestamp\":\"{timestamp}\"," +
            $"\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{turnId}\"}}}}";
    }

    private static string TokenCountLine(string timestamp, long input, long output, long total)
    {
        return "{" +
            $"\"timestamp\":\"{timestamp}\"," +
            "\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{" +
            $"\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{total}" +
            "}}}}";
    }
}
