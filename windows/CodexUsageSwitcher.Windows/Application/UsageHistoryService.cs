using CodexUsageSwitcher.Windows.Domain;
using CodexUsageSwitcher.Windows.Infrastructure;

namespace CodexUsageSwitcher.Windows.Application;

internal enum HistoryProvider
{
    Claude,
    Codex,
}

// Orchestration facade over the per-provider incremental caches. A load enumerates the live
// files (cheap, metadata-only), hashes them into a revision, and returns the last computed
// insights if the revision is unchanged (FreshnessGate). On a change it refreshes only the
// affected files (FileParseCache), de-dups + computes (InsightsAggregator), and caches the
// result. Per-provider SemaphoreSlims serialize concurrent refreshes (warmup vs open vs force);
// the two providers refresh in parallel. Missing dirs degrade to empty insights — never throws.
internal sealed class UsageHistoryService : IDisposable
{
    private readonly string _claudeConfigDir;
    private readonly string _codexHome;
    private readonly FileParseCache _claudeCache;
    private readonly FileParseCache _codexCache;
    private readonly FreshnessGate _gate = new();
    private readonly SemaphoreSlim _claudeLock = new(1, 1);
    private readonly SemaphoreSlim _codexLock = new(1, 1);
    private readonly ModelPriceStore _priceStore = new();
    private ModelPricing _pricing;

    public UsageHistoryService()
        : this(ClaudeHistoryReader.ResolveConfigDir(), CodexHistoryReader.ResolveCodexHome())
    {
    }

    public UsageHistoryService(string claudeConfigDir, string codexHome)
        : this(claudeConfigDir, codexHome, DefaultCacheRoot())
    {
    }

    public UsageHistoryService(string claudeConfigDir, string codexHome, string cacheRoot)
    {
        _claudeConfigDir = claudeConfigDir;
        _codexHome = codexHome;
        _claudeCache = new FileParseCache(ProviderParsers.Claude(), new FileCacheStore(Path.Combine(cacheRoot, "claude")));
        _codexCache = new FileParseCache(ProviderParsers.Codex(), new FileCacheStore(Path.Combine(cacheRoot, "codex")));
        _pricing = new ModelPricing(_priceStore.Load());
    }

    // Synchronous load (tests / back-compat). Cache-backed, never throws.
    public UsageInsights LoadInsights(HistoryProvider provider, DateTimeOffset now)
        => LoadCore(provider, now, forceRefresh: false, CancellationToken.None);

    // Asynchronous load: all work (enumerate, hash, parse, compute) runs off the caller's
    // thread so the UI never blocks and two providers can refresh in parallel.
    public Task<UsageInsights> LoadInsightsAsync(HistoryProvider provider, DateTimeOffset now, bool forceRefresh, CancellationToken ct)
        => Task.Run(() => LoadCore(provider, now, forceRefresh, ct), ct);

    // Loads both providers concurrently (each on its own thread, behind its own lock).
    public async Task<ProviderInsights> LoadAllAsync(DateTimeOffset now, bool forceRefresh, CancellationToken ct)
    {
        var claude = LoadInsightsAsync(HistoryProvider.Claude, now, forceRefresh, ct);
        var codex = LoadInsightsAsync(HistoryProvider.Codex, now, forceRefresh, ct);
        await Task.WhenAll(claude, codex).ConfigureAwait(false);
        return new ProviderInsights(await claude.ConfigureAwait(false), await codex.ConfigureAwait(false));
    }

    // Warms both caches in the background (used at startup so the first open is hot). Also
    // refreshes the model price table from the project's own repo so costs use the latest rates.
    public async Task WarmupAsync(DateTimeOffset now, CancellationToken ct)
    {
        await RefreshPricesAsync(ct).ConfigureAwait(false);
        await LoadAllAsync(now, forceRefresh: false, ct).ConfigureAwait(false);
    }

    // Best-effort: pull the latest reviewed price table from the project's own repo, then rebuild
    // the in-memory pricing so later loads use fresh rates. Never throws.
    public async Task RefreshPricesAsync(CancellationToken ct)
    {
        await _priceStore.RefreshAsync(ct).ConfigureAwait(false);
        _pricing = new ModelPricing(_priceStore.Load());
    }

    private UsageInsights LoadCore(HistoryProvider provider, DateTimeOffset now, bool forceRefresh, CancellationToken ct)
    {
        var (cache, gate, baseDir) = Resolve(provider);
        // One enumeration drives both the revision key and the parse, so cached insights are
        // never mislabeled by a separate, later enumeration (TOCTOU).
        var files = cache.Enumerate(baseDir);
        var revision = FreshnessGate.Revision(files);
        if (!forceRefresh && _gate.TryGet(provider, revision, out var cached))
        {
            return cached;
        }

        gate.Wait(ct); // on a Task.Run / sync caller thread, not the UI thread
        try
        {
            // Another waiter may have computed this revision while we blocked.
            if (!forceRefresh && _gate.TryGet(provider, revision, out cached))
            {
                return cached;
            }

            var records = cache.Refresh(files, now, new RefreshStats());
            var insights = InsightsAggregator.Aggregate(records, now, TimeZoneInfo.Local, _pricing);
            _gate.Put(provider, revision, insights);
            return insights;
        }
        finally
        {
            gate.Release();
        }
    }

    private (FileParseCache Cache, SemaphoreSlim Lock, string BaseDir) Resolve(HistoryProvider provider) => provider switch
    {
        HistoryProvider.Claude => (_claudeCache, _claudeLock, _claudeConfigDir),
        HistoryProvider.Codex => (_codexCache, _codexLock, _codexHome),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown history provider."),
    };

    private static string DefaultCacheRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageSwitcher",
        "cache");

    public void Dispose()
    {
        // Dispose a lock only if no load currently holds it; otherwise leave it for GC so a
        // background load racing shutdown can't hit ObjectDisposedException. SemaphoreSlim holds
        // no unmanaged resource unless AvailableWaitHandle is used (it isn't here), so this is safe.
        TryDisposeLock(_claudeLock);
        TryDisposeLock(_codexLock);
    }

    private static void TryDisposeLock(SemaphoreSlim semaphore)
    {
        if (semaphore.Wait(0))
        {
            semaphore.Dispose();
        }
    }
}

internal sealed record ProviderInsights(UsageInsights Claude, UsageInsights Codex);
