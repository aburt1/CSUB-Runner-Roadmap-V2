using System.Security.Cryptography;
using System.Text.Json;
using Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Api.IntegrationTests;

// Direct unit tests for the AES-256-GCM credential encryption (no server/DB needed).
// Encryption is constructed straight from an in-memory config + a fake environment,
// mirroring SecurityHardeningTests. The key strength policy (IsWeakKey / IsHex64) is
// private, so it is exercised through the constructor's public production fail-safe.
public class EncryptionTests
{
    // A strong 64-char hex key (32 bytes) — not a repeated character. Test-only literal.
    private const string StrongKey = "9f3a1c7e0b52d84f6a1e9c0d47b23f8e5a6c1d20e94f7b3a8c5d6e1f024a9b7c";

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly IHostEnvironment Production = new FakeEnv("Production");
    private static readonly IHostEnvironment Development = new FakeEnv("Development");

    private static IConfiguration Config(string? key) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiCheck:EncryptionKey"] = key })
            .Build();

    private static Encryption ConfiguredEncryption() => new(Config(StrongKey), Development);

    // ---- round-trip ----

    [Fact]
    public void Encrypt_then_decrypt_returns_the_original_plaintext()
    {
        var enc = ConfiguredEncryption();
        const string plaintext = "super-secret-api-token-12345";

        var roundTripped = enc.Decrypt(enc.Encrypt(plaintext));

        Assert.Equal(plaintext, roundTripped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("unicode: café — 日本語 — 🔐")]
    public void Round_trip_preserves_edge_case_plaintexts(string plaintext)
    {
        var enc = ConfiguredEncryption();
        Assert.Equal(plaintext, enc.Decrypt(enc.Encrypt(plaintext)));
    }

    // ---- random IV ----

    [Fact]
    public void Two_encryptions_of_the_same_plaintext_differ()
    {
        var enc = ConfiguredEncryption();
        const string plaintext = "identical-input";

        var first = enc.Encrypt(plaintext);
        var second = enc.Encrypt(plaintext);

        // A fresh random IV per encryption means the ciphertext (and iv) must differ,
        // yet both must still decrypt back to the same plaintext.
        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, enc.Decrypt(first));
        Assert.Equal(plaintext, enc.Decrypt(second));

        var ivA = JsonDocument.Parse(first).RootElement.GetProperty("iv").GetString();
        var ivB = JsonDocument.Parse(second).RootElement.GetProperty("iv").GetString();
        Assert.NotEqual(ivA, ivB);
    }

    // ---- on-disk {iv, data, tag} hex shape ----

    [Fact]
    public void Encrypted_payload_has_the_iv_data_tag_hex_shape()
    {
        var enc = ConfiguredEncryption();

        var payload = JsonDocument.Parse(enc.Encrypt("payload-shape")).RootElement;

        var iv = payload.GetProperty("iv").GetString()!;
        var data = payload.GetProperty("data").GetString()!;
        var tag = payload.GetProperty("tag").GetString()!;

        // 12-byte IV and 16-byte GCM tag, each hex-encoded (2 chars per byte).
        Assert.Equal(24, iv.Length);
        Assert.Equal(32, tag.Length);
        Assert.True(IsLowerHex(iv), "iv must be lowercase hex");
        Assert.True(IsLowerHex(data), "data must be lowercase hex");
        Assert.True(IsLowerHex(tag), "tag must be lowercase hex");
    }

    // ---- tamper detection ----

    [Fact]
    public void Tampering_with_the_ciphertext_makes_decrypt_fail()
    {
        var enc = ConfiguredEncryption();
        var payload = JsonDocument.Parse(enc.Encrypt("do-not-tamper")).RootElement;

        var tampered = JsonSerializer.Serialize(new
        {
            iv = payload.GetProperty("iv").GetString(),
            data = FlipFirstHexNibble(payload.GetProperty("data").GetString()!),
            tag = payload.GetProperty("tag").GetString(),
        });

        Assert.Throws<AuthenticationTagMismatchException>(() => enc.Decrypt(tampered));
    }

    [Fact]
    public void Tampering_with_the_tag_makes_decrypt_fail()
    {
        var enc = ConfiguredEncryption();
        var payload = JsonDocument.Parse(enc.Encrypt("do-not-tamper")).RootElement;

        var tampered = JsonSerializer.Serialize(new
        {
            iv = payload.GetProperty("iv").GetString(),
            data = payload.GetProperty("data").GetString(),
            tag = FlipFirstHexNibble(payload.GetProperty("tag").GetString()!),
        });

        Assert.Throws<AuthenticationTagMismatchException>(() => enc.Decrypt(tampered));
    }

    // ---- key strength policy (IsWeakKey / IsHex64), via the production fail-safe ----

    [Fact]
    public void Strong_64_hex_key_in_production_is_accepted_and_configured()
    {
        var enc = new Encryption(Config(StrongKey), Production);
        Assert.True(enc.IsConfigured);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // all zeros
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")] // all f
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // all a
    public void All_same_character_key_in_production_is_rejected(string weakKey) =>
        Assert.Throws<InvalidOperationException>(() => new Encryption(Config(weakKey), Production));

    [Theory]
    [InlineData(null)]                                                                // missing
    [InlineData("")]                                                                  // empty
    [InlineData("tooshort")]                                                          // not 64 chars
    [InlineData("9f3a1c7e0b52d84f6a1e9c0d47b23f8e5a6c1d20e94f7b3a8c5d6e1f024a9b7")]  // 63 chars
    [InlineData("zf3a1c7e0b52d84f6a1e9c0d47b23f8e5a6c1d20e94f7b3a8c5d6e1f024a9b7c")]  // non-hex char
    public void Missing_or_malformed_key_in_production_is_rejected(string? badKey) =>
        Assert.Throws<InvalidOperationException>(() => new Encryption(Config(badKey), Production));

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }

    // Flip the low bit of the first hex nibble so the byte string still decodes but the
    // plaintext/tag no longer matches — GCM must reject it.
    private static string FlipFirstHexNibble(string hex)
    {
        var chars = hex.ToCharArray();
        chars[0] = chars[0] == '0' ? '1' : '0';
        return new string(chars);
    }
}
