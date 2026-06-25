using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Api.Services;

// AES-256-GCM credential encryption.
// The on-disk format is a JSON string { iv, data, tag } with each field hex-
// encoded (12-byte IV, 16-byte GCM auth tag).
//
// Registered as a singleton; the key is read once from config "ApiCheck:EncryptionKey",
// a 64-char hex string = 32 bytes.
public sealed class Encryption
{
    private const int IvLength = 12;
    private const int TagLength = 16;

    private readonly byte[]? _key;
    private readonly bool _isConfigured;

    public Encryption(IConfiguration config, IHostEnvironment env)
    {
        var hex = config["ApiCheck:EncryptionKey"];

        // Fail safe in production (same policy as JwtService): a missing, malformed,
        // or obviously-weak key must stop the deployment, not silently disable
        // credential encryption or encrypt with a guessable key.
        if (env.IsProduction())
        {
            if (string.IsNullOrEmpty(hex) || !IsHex64(hex))
                throw new InvalidOperationException(
                    "ApiCheck:EncryptionKey must be a 64-character hex string (32 bytes) in Production. Generate one: openssl rand -hex 32");
            if (IsWeakKey(hex))
                throw new InvalidOperationException(
                    "ApiCheck:EncryptionKey is a placeholder/weak value (repeated single character); set a real random key in Production.");
        }

        if (!string.IsNullOrEmpty(hex) && IsHex64(hex))
        {
            _key = Convert.FromHexString(hex);
            _isConfigured = true;
        }
        else
        {
            _key = null;
            _isConfigured = false;
        }
    }

    // A 64-hex key made of one repeated character (e.g. all zeros) is a placeholder.
    private static bool IsWeakKey(string hex)
    {
        for (var i = 1; i < hex.Length; i++)
            if (char.ToLowerInvariant(hex[i]) != char.ToLowerInvariant(hex[0])) return false;
        return true;
    }

    // True when the key is present and exactly 64 hex chars.
    public bool IsConfigured => _isConfigured;

    private byte[] GetKey()
    {
        if (_key is null)
            throw new InvalidOperationException(
                "API_CHECK_ENCRYPTION_KEY must be a 64-character hex string (32 bytes).");
        return _key;
    }

    public string Encrypt(string plaintext)
    {
        var key = GetKey();
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(iv, plainBytes, cipherBytes, tag);

        var payload = new EncryptedPayload
        {
            iv = Convert.ToHexString(iv).ToLowerInvariant(),
            data = Convert.ToHexString(cipherBytes).ToLowerInvariant(),
            tag = Convert.ToHexString(tag).ToLowerInvariant(),
        };
        return JsonSerializer.Serialize(payload);
    }

    public string Decrypt(string encrypted)
    {
        var key = GetKey();
        var payload = JsonSerializer.Deserialize<EncryptedPayload>(encrypted)
            ?? throw new FormatException("Invalid encrypted payload");

        var iv = Convert.FromHexString(payload.iv);
        var cipherBytes = Convert.FromHexString(payload.data);
        var tag = Convert.FromHexString(payload.tag);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(iv, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static bool IsHex64(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private sealed class EncryptedPayload
    {
        public string iv { get; set; } = "";
        public string data { get; set; } = "";
        public string tag { get; set; } = "";
    }
}
