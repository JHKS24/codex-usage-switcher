namespace CodexUsageSwitcher.Windows.Application;

internal interface ISettingsStore
{
    Task<IReadOnlySet<string>> LoadTrayMetricVisibilityAsync(CancellationToken cancellationToken);

    // A single locked read-modify-write; exposing separate load/save here would
    // let two quick toggles interleave and drop each other's update.
    Task SetTrayMetricVisibilityAsync(string metricKey, bool visible, CancellationToken cancellationToken);

    Task<string?> LoadCodexSubProfileAsync(CancellationToken cancellationToken);

    Task SetCodexSubProfileAsync(string? profile, CancellationToken cancellationToken);

    // The persisted UI language code ("en" / "ko"), or null when the user has never chosen one
    // (callers then fall back to the system culture).
    Task<string?> LoadLanguageAsync(CancellationToken cancellationToken);

    Task SetLanguageAsync(string language, CancellationToken cancellationToken);
}
