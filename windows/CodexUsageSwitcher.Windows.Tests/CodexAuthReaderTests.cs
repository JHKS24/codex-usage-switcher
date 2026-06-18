using System.Text;
using CodexUsageSwitcher.Windows.Infrastructure;
using Xunit;

namespace CodexUsageSwitcher.Windows.Tests;

public sealed class CodexAuthReaderTests : IDisposable
{
    private readonly string _home;

    public CodexAuthReaderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "auth-reader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private static string B64Url(string json)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void WriteAuth(string idTokenPayloadJson, string accountId, string accessToken)
    {
        var idToken = "hdr." + B64Url(idTokenPayloadJson) + ".sig";
        var auth = "{\"tokens\":{\"id_token\":\"" + idToken + "\",\"account_id\":\"" + accountId +
                   "\",\"access_token\":\"" + accessToken + "\"}}";
        File.WriteAllText(Path.Combine(_home, "auth.json"), auth, Encoding.UTF8);
    }

    [Fact]
    public void Read_extracts_public_identity_and_keeps_the_token_for_the_call()
    {
        WriteAuth(
            "{\"email\":\"a@b.com\",\"https://api.openai.com/auth\":{\"chatgpt_plan_type\":\"pro\"," +
            "\"organizations\":[{\"is_default\":false,\"title\":\"Other\"},{\"is_default\":true,\"title\":\"Acme\"}]}}",
            "acc-1", "SECRET-xyz");

        var summary = CodexAuthReader.Read(_home);

        Assert.NotNull(summary);
        Assert.Null(summary!.Error);
        Assert.Equal("a@b.com", summary.Email);
        Assert.Equal("pro", summary.Plan);
        Assert.Equal("Acme", summary.Organization); // is_default wins over first
        Assert.Equal("acc-1", summary.AccountId);
        Assert.Equal("SECRET-xyz", summary.AccessToken);
    }

    [Fact]
    public void ToString_redacts_the_access_token()
    {
        WriteAuth("{\"email\":\"a@b.com\"}", "acc-1", "SECRET-xyz");
        var text = CodexAuthReader.Read(_home)!.ToString();
        Assert.DoesNotContain("SECRET-xyz", text);
        Assert.Contains("[redacted]", text);
        Assert.Contains("a@b.com", text); // public identity still visible
    }

    [Fact]
    public void Read_returns_null_when_missing_and_error_when_tokens_absent()
    {
        Assert.Null(CodexAuthReader.Read(_home)); // no auth.json
        File.WriteAllText(Path.Combine(_home, "auth.json"), "{\"other\":1}", Encoding.UTF8);
        Assert.Equal("auth.json is missing tokens", CodexAuthReader.Read(_home)!.Error);
    }

    [Theory]
    [InlineData("work", true)]
    [InlineData("edu-1", true)]
    [InlineData("a.b_c", true)]
    [InlineData("1abc", true)]
    [InlineData("", false)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    [InlineData("../escape", false)]
    public void ProfileName_validates(string name, bool valid) => Assert.Equal(valid, ProfileName.IsValid(name));
}
