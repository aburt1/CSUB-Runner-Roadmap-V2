using System.Net;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.IntegrationTests;

// Unit-level coverage for the SSRF defense in ApiCheckRunner: the private-range bit
// logic (IsPrivateAddress) and the URL gate (ValidateUrlAsync). These guard the
// admin-authored, server-fetched API-check URLs. IsPrivateAddress was made internal
// (test seam, exposed via InternalsVisibleTo); behavior is unchanged.
//
// No shared fixture / DB needed: IsPrivateAddress is static and ValidateUrlAsync only
// touches DNS, so each test builds its own runner with a chosen AllowPrivateTargets.
public class SsrfGuardTests
{
    private static ApiCheckRunner BuildRunner(bool allowPrivateTargets)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiCheck:AllowPrivateTargets"] = allowPrivateTargets ? "true" : "false",
                ["ApiCheck:EncryptionKey"] =
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            })
            .Build();

        // Db is never used by ValidateUrlAsync, but the ctor requires one.
        var db = new Api.Data.Db("Server=unused;Database=unused;");
        var encryption = new Encryption(config, new TestHostEnvironment());
        return new ApiCheckRunner(db, encryption, config, NullLogger<ApiCheckRunner>.Instance);
    }

    // Minimal IHostEnvironment (Development) so Encryption's ctor doesn't enforce the
    // Production key policy — this suite only exercises the SSRF guard, not encryption.
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Api.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    [Theory]
    // Loopback, the RFC1918 ranges, link-local, and the IPv6 equivalents.
    [InlineData("10.0.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    // Public addresses must NOT be flagged private.
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("93.184.216.34", false)]
    public void IsPrivateAddress_classifies_each_range(string ip, bool expectedPrivate)
    {
        var address = IPAddress.Parse(ip);
        Assert.Equal(expectedPrivate, ApiCheckRunner.IsPrivateAddress(address));
    }

    [Fact]
    public async Task ValidateUrl_rejects_private_ip_when_targets_not_allowed()
    {
        var runner = BuildRunner(allowPrivateTargets: false);
        var result = await runner.ValidateUrlAsync("http://10.0.0.1/");
        Assert.False(result.valid);
    }

    [Fact]
    public async Task ValidateUrl_rejects_localhost_when_targets_not_allowed()
    {
        var runner = BuildRunner(allowPrivateTargets: false);
        var result = await runner.ValidateUrlAsync("http://localhost/");
        Assert.False(result.valid);
        Assert.Equal("Requests to localhost are not allowed", result.reason);
    }

    [Fact]
    public async Task ValidateUrl_rejects_non_http_scheme()
    {
        var runner = BuildRunner(allowPrivateTargets: false);
        var result = await runner.ValidateUrlAsync("ftp://example.com/");
        Assert.False(result.valid);
        Assert.Contains("ftp:", result.reason);
    }

    [Fact]
    public async Task ValidateUrl_rejects_malformed_url()
    {
        var runner = BuildRunner(allowPrivateTargets: false);
        var result = await runner.ValidateUrlAsync("not a url");
        Assert.False(result.valid);
        Assert.Equal("Invalid URL format", result.reason);
    }
}
