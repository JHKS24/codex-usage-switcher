using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class PathRedactionTests
{
    [Fact]
    public void Scrub_replaces_the_user_home_with_tilde_so_the_username_never_leaks()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"Access to the path '{home}\\.codex\\auth.json' is denied.";

        var scrubbed = PathRedaction.Scrub(text);

        Assert.DoesNotContain(home, scrubbed);
        Assert.Contains("~", scrubbed);
    }

    [Fact]
    public void Scrub_collapses_other_absolute_paths_to_the_final_segment()
    {
        var scrubbed = PathRedaction.Scrub(@"could not find C:\Programs\Codex\Codex.exe now");

        Assert.DoesNotContain(@"C:\Programs\Codex", scrubbed);
        Assert.Contains("Codex.exe", scrubbed);
    }

    [Fact]
    public void Scrub_handles_null_and_empty()
    {
        Assert.Equal(string.Empty, PathRedaction.Scrub(null));
        Assert.Equal(string.Empty, PathRedaction.Scrub(string.Empty));
    }

    [Fact]
    public void Scrub_leaves_text_without_paths_unchanged()
    {
        const string text = "switch failed: the account is busy";
        Assert.Equal(text, PathRedaction.Scrub(text));
    }
}
