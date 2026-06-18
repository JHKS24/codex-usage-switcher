using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageSwitcher.Windows.Domain;

namespace CodexUsageSwitcher.Windows.Infrastructure;

// Loads the model price map and keeps it fresh WITHOUT trusting any third-party package at
// runtime. Order of trust: (1) a cached copy this app previously downloaded from the project's
// OWN repo, (2) the snapshot bundled into the build. Updates come only from the project's own
// raw URL — a GitHub Action regenerates that file from the official OpenAI/Anthropic pricing
// pages and a maintainer reviews the PR, so the runtime never fetches from a third party.
//
// Fetched/loaded data is treated as untrusted: it is parsed (never executed) and every price is
// sanity-bounded; an out-of-range or unparseable entry is dropped, and any I/O failure degrades
// to the bundled snapshot. This is the single boundary where price-source errors are handled.
internal sealed class ModelPriceStore
{
    private const int CurrentSchemaVersion = 1;
    private const double MaxPlausiblePerMTok = 10_000; // reject absurd/garbage prices

    // The project's own reviewed price file. Overridable for tests / forks.
    public static string DefaultRemoteUrl =>
        Environment.GetEnvironmentVariable("CDUS_PRICE_URL")
        ?? "https://raw.githubusercontent.com/JHKS24/codex-usage-switcher/main/pricing/model_prices.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _cacheFile;

    public ModelPriceStore(string? cacheFile = null) =>
        _cacheFile = cacheFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageSwitcher", "pricing", "model_prices.json");

    // Best available price map: the cached download if usable, else the bundled snapshot.
    public IReadOnlyDictionary<string, ModelPrice> Load()
    {
        if (TryReadFile(_cacheFile, out var cached) && cached.Count > 0)
        {
            return cached;
        }

        return TryReadText(BundledJson(), out var bundled) ? bundled : new Dictionary<string, ModelPrice>();
    }

    // Best-effort refresh from the project's own reviewed file. Validates + sanity-bounds before
    // replacing the cache; any failure leaves the existing cache/bundle untouched. Never throws.
    public async Task RefreshAsync(CancellationToken ct, string? url = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var text = await http.GetStringAsync(url ?? DefaultRemoteUrl, ct).ConfigureAwait(false);
            if (!TryReadText(text, out var parsed) || parsed.Count == 0)
            {
                return; // unparseable / empty / all-out-of-range -> keep what we have
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = $"{_cacheFile}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, text, ct).ConfigureAwait(false);
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            // offline / timeout / locked / malformed: the bundled snapshot remains the source
        }
    }

    private static bool TryReadFile(string path, out Dictionary<string, ModelPrice> map)
    {
        map = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return File.Exists(path) && TryReadText(File.ReadAllText(path), out map);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadText(string json, out Dictionary<string, ModelPrice> map)
    {
        map = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        PriceFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PriceFileDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto is null || dto.SchemaVersion != CurrentSchemaVersion || dto.Models is null)
        {
            return false;
        }

        foreach (var (id, e) in dto.Models)
        {
            if (e is null || string.IsNullOrWhiteSpace(id) || !InRange(e))
            {
                continue; // drop garbage/out-of-range entries
            }

            map[id] = new ModelPrice(e.Input, e.CacheRead, e.Cache5m, e.Cache1h, e.Output);
        }

        return true;
    }

    private static bool InRange(PriceEntryDto e)
    {
        foreach (var v in new[] { e.Input, e.CacheRead, e.Cache5m, e.Cache1h, e.Output })
        {
            if (double.IsNaN(v) || v < 0 || v > MaxPlausiblePerMTok)
            {
                return false;
            }
        }

        return true;
    }

    private static string BundledJson()
    {
        var assembly = typeof(ModelPriceStore).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("model_prices.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("bundled model_prices.json resource not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("bundled model_prices.json stream is null.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record PriceFileDto(int SchemaVersion, Dictionary<string, PriceEntryDto?>? Models);

    private sealed record PriceEntryDto(double Input, double CacheRead, double Cache5m, double Cache1h, double Output);
}
