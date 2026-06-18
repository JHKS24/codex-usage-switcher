using CodexUsageSwitcher.Windows.Domain;

namespace CodexUsageSwitcher.Windows.Application;

// Resolves a transcript model id to its published price. Normalizes the id (lowercase, strip a
// provider prefix and a trailing -YYYYMMDD date) and looks it up in the price map. A miss is the
// "new model detected" signal: the caller shows tokens but no estimated cost ("unpriced") rather
// than guessing a wrong rate — and the next price refresh (once the official pages list the
// model) makes it priced automatically. Cost is computed here from the per-event token breakdown.
internal sealed class ModelPricing
{
    private readonly IReadOnlyDictionary<string, ModelPrice> _prices;

    public ModelPricing(IReadOnlyDictionary<string, ModelPrice> prices) => _prices = prices;

    public bool TryGetPrice(string model, out ModelPrice price) =>
        _prices.TryGetValue(Normalize(model), out price);

    // Estimated USD for one event, or null when the model is unpriced (unknown to the table).
    public double? EstimateCost(InsightEntry entry)
    {
        if (!TryGetPrice(entry.Model, out var p))
        {
            return null;
        }

        return (entry.InputTokens * p.InputPerMTok
            + entry.CacheReadTokens * p.CacheReadPerMTok
            + entry.Cache5mTokens * p.Cache5mPerMTok
            + entry.Cache1hTokens * p.Cache1hPerMTok
            + entry.OutputTokens * p.OutputPerMTok) / 1_000_000.0;
    }

    // lowercase; drop a leading "anthropic." / "openai/" provider prefix; drop a trailing
    // "-YYYYMMDD" date so "claude-sonnet-4-5-20250929" matches the "claude-sonnet-4-5" entry.
    private static string Normalize(string model)
    {
        var m = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (m.StartsWith("anthropic.", StringComparison.Ordinal))
        {
            m = m["anthropic.".Length..];
        }
        else if (m.StartsWith("openai/", StringComparison.Ordinal))
        {
            m = m["openai/".Length..];
        }

        var dash = m.LastIndexOf('-');
        if (dash > 0 && m.Length - dash - 1 == 8 && m.AsSpan(dash + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            m = m[..dash];
        }

        return m;
    }
}
