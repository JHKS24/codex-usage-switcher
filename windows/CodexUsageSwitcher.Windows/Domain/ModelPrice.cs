namespace CodexUsageSwitcher.Windows.Domain;

// Per-MILLION-token USD prices for one model, as published on the official OpenAI / Anthropic
// pricing pages. Cost for an event = tokens / 1e6 * the matching rate. Cache5m/Cache1h are the
// Anthropic cache-write tiers; OpenAI uses only CacheRead (cached input). Dimensions a provider
// does not bill are 0.
internal readonly record struct ModelPrice(
    double InputPerMTok,
    double CacheReadPerMTok,
    double Cache5mPerMTok,
    double Cache1hPerMTok,
    double OutputPerMTok);
