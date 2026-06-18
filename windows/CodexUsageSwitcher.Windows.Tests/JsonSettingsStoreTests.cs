using System.Text.Json;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"codex-switcher-tests-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaults()
    {
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        var metrics = await store.LoadTrayMetricVisibilityAsync(CancellationToken.None);

        Assert.Equal(["codex:5h", "codex:week"], metrics.Order().ToArray());
    }

    [Fact]
    public async Task SetAndLoad_RoundTrips()
    {
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        await store.SetTrayMetricVisibilityAsync("claude:5h", visible: true, CancellationToken.None);
        await store.SetTrayMetricVisibilityAsync("codex:5h", visible: false, CancellationToken.None);
        var metrics = await store.LoadTrayMetricVisibilityAsync(CancellationToken.None);

        Assert.Equal(["claude:5h", "codex:week"], metrics.Order().ToArray());
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaults_AndPreservesTheBrokenFile()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{ this is not json");
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        var metrics = await store.LoadTrayMetricVisibilityAsync(CancellationToken.None);

        Assert.Equal(["codex:5h", "codex:week"], metrics.Order().ToArray());
        Assert.True(File.Exists(SettingsPath + ".bad"));
    }

    [Fact]
    public async Task Load_LegacyFileWithProviderSection_StillReadsTrayMetrics()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            SettingsPath,
            """{"Providers": {"cursor": false}, "TrayMetrics": ["claude:week"]}""");
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        var metrics = await store.LoadTrayMetricVisibilityAsync(CancellationToken.None);

        Assert.Equal(["claude:week"], metrics.ToArray());
    }

    [Fact]
    public async Task Set_ConcurrentToggles_AllSurvive()
    {
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        // Read-modify-write under one gate: none of these may drop another's update.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            store.SetTrayMetricVisibilityAsync($"metric:{index}", visible: true, CancellationToken.None)));

        var metrics = await store.LoadTrayMetricVisibilityAsync(CancellationToken.None);
        Assert.All(Enumerable.Range(0, 16), index => Assert.Contains($"metric:{index}", metrics));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("TrayMetrics").ValueKind);
    }

    [Fact]
    public async Task Set_LeavesNoTempFileBehind()
    {
        var store = JsonSettingsStore.CreateAt(SettingsPath);

        await store.SetTrayMetricVisibilityAsync("codex:5h", visible: true, CancellationToken.None);

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.True(File.Exists(SettingsPath));
    }
}
