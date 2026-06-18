using System.Text.Json;
using CodexUsageSwitcher.Windows.Application;

namespace CodexUsageSwitcher.Windows.Infrastructure;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // Serializes every read-modify-write so two quick checkbox toggles cannot
    // interleave and drop each other's update.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;

    private JsonSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public static JsonSettingsStore CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "CodexUsageSwitcher");
        return CreateAt(Path.Combine(directory, "settings.json"));
    }

    public static JsonSettingsStore CreateAt(string settingsPath)
    {
        return new JsonSettingsStore(settingsPath);
    }

    public async Task<IReadOnlySet<string>> LoadTrayMetricVisibilityAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            return (settings?.TrayMetrics ?? DefaultTrayMetrics())
                .Where(metric => !string.IsNullOrWhiteSpace(metric))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTrayMetricVisibilityAsync(string metricKey, bool visible, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            var metrics = (settings?.TrayMetrics ?? DefaultTrayMetrics())
                .Where(metric => !string.IsNullOrWhiteSpace(metric))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (visible)
            {
                metrics.Add(metricKey);
            }
            else
            {
                metrics.Remove(metricKey);
            }

            await SaveFileAsync(
                new SettingsFile(metrics.Order().ToArray(), settings?.Language, NormalizeProfile(settings?.CodexSubProfile)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> LoadCodexSubProfileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            return NormalizeProfile(settings?.CodexSubProfile);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCodexSubProfileAsync(string? profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            var metrics = (settings?.TrayMetrics ?? DefaultTrayMetrics())
                .Where(metric => !string.IsNullOrWhiteSpace(metric))
                .Order()
                .ToArray();
            await SaveFileAsync(
                new SettingsFile(metrics, settings?.Language, NormalizeProfile(profile)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> LoadLanguageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            return NormalizeLanguage(settings?.Language);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetLanguageAsync(string language, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLanguage(language) ?? "en";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadFileAsync(cancellationToken).ConfigureAwait(false);
            var metrics = (settings?.TrayMetrics ?? DefaultTrayMetrics())
                .Where(metric => !string.IsNullOrWhiteSpace(metric))
                .Order()
                .ToArray();
            await SaveFileAsync(new SettingsFile(metrics, normalized, NormalizeProfile(settings?.CodexSubProfile)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch { "ko" => "ko", "en" => "en", _ => null };

    private static string? NormalizeProfile(string? profile)
    {
        var trimmed = profile?.Trim();
        return ProfileName.IsValid(trimmed) ? trimmed : null;
    }

    private async Task<SettingsFile?> LoadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<SettingsFile>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Keep the unreadable file for inspection; without this, the next save
            // silently replaces the user's real settings with defaults.
            TryPreserveBrokenFile();
            return null;
        }
    }

    private void TryPreserveBrokenFile()
    {
        try
        {
            File.Copy(_settingsPath, _settingsPath + ".bad", overwrite: true);
        }
        catch
        {
            // The file may be locked rather than corrupt; preservation is best effort.
        }
    }

    private async Task SaveFileAsync(SettingsFile settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-to-temp + atomic move: a crash mid-write can no longer truncate
        // settings.json in place.
        var tempPath = _settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private static IReadOnlyList<string> DefaultTrayMetrics()
    {
        return ["codex:5h", "codex:week"];
    }

    private sealed record SettingsFile(IReadOnlyList<string>? TrayMetrics, string? Language = null, string? CodexSubProfile = null);
}
