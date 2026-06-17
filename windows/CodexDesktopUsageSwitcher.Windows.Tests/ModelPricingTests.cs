using CodexDesktopUsageSwitcher.Windows.Application;
using CodexDesktopUsageSwitcher.Windows.Domain;
using CodexDesktopUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexDesktopUsageSwitcher.Windows.Tests;

public sealed class ModelPricingTests
{
    // No cache file -> Load() falls back to the bundled snapshot embedded in the build.
    private static ModelPricing Bundled() =>
        new(new ModelPriceStore(Path.Combine(Path.GetTempPath(), "no-cache-" + Guid.NewGuid().ToString("N"), "x.json")).Load());

    [Fact]
    public void Bundled_snapshot_has_current_models_with_official_rates()
    {
        var p = Bundled();
        Assert.True(p.TryGetPrice("gpt-5.5", out var gpt));
        Assert.Equal(5, gpt.InputPerMTok);
        Assert.Equal(30, gpt.OutputPerMTok);
        Assert.True(p.TryGetPrice("claude-opus-4-8", out var opus));
        Assert.Equal(5, opus.InputPerMTok);
        Assert.Equal(25, opus.OutputPerMTok);
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("claude-sonnet-4-5-20250929")] // trailing date stripped
    [InlineData("anthropic.claude-haiku-4-5")] // provider prefix stripped
    [InlineData("Claude-Opus-4-8")]            // case-insensitive
    public void Known_models_resolve(string model) => Assert.True(Bundled().TryGetPrice(model, out _));

    [Fact]
    public void Unknown_model_is_unpriced()
    {
        Assert.False(Bundled().TryGetPrice("gpt-9-imaginary", out _));
        Assert.Null(Bundled().EstimateCost(new InsightEntry(1, "gpt-9-imaginary", 100, 40, null, null, InputTokens: 100)));
    }

    [Fact]
    public void EstimateCost_uses_the_token_breakdown()
    {
        // claude-opus-4-8: input $5, cacheRead $0.5, output $25 per MTok. 1M of each -> $30.5.
        var entry = new InsightEntry(1, "claude-opus-4-8", 0, 1_000_000, null, null,
            InputTokens: 1_000_000, CacheReadTokens: 1_000_000);
        Assert.Equal(30.5, Bundled().EstimateCost(entry)!.Value, 6);
    }

    [Fact]
    public void Store_drops_out_of_range_prices_and_keeps_the_rest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "price-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "model_prices.json");
        File.WriteAllText(file, "{\"schemaVersion\":1,\"models\":{" +
            "\"good\":{\"input\":1,\"cacheRead\":0,\"cache5m\":0,\"cache1h\":0,\"output\":2}," +
            "\"bad\":{\"input\":-5,\"cacheRead\":0,\"cache5m\":0,\"cache1h\":0,\"output\":999999}}}");
        try
        {
            var map = new ModelPriceStore(file).Load();
            Assert.True(map.ContainsKey("good"));
            Assert.False(map.ContainsKey("bad"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
