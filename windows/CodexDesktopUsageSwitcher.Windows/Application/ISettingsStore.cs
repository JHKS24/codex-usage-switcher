namespace CodexDesktopUsageSwitcher.Windows.Application;

internal interface ISettingsStore
{
    Task<IReadOnlySet<string>> LoadTrayMetricVisibilityAsync(CancellationToken cancellationToken);

    // A single locked read-modify-write; exposing separate load/save here would
    // let two quick toggles interleave and drop each other's update.
    Task SetTrayMetricVisibilityAsync(string metricKey, bool visible, CancellationToken cancellationToken);
}
