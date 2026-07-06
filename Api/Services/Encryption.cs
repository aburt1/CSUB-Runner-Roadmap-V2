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
                    "ApiCheck:EncryptionKey is a placeholder/weak value (the committed dev key or a low-entropy repeating pattern); set a real random key in Production. Generate one: openssl rand -hex 32");
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

    // A key is weak if it is a low-entropy repeating pattern: a short unit tiled to fill
    // the 64 chars (all-same-character, a 2-char unit x32, an 8-char unit x8, etc.). This
    // also catches the committed dev key in Api/appsettings.Development.json:ApiCheck:
    // EncryptionKey (a 16-char hex unit tiled x4), which is valid 64-hex and so slipped
    // past the old all-same-char-only check. Mirrors JwtService's known-placeholder guard:
    // a well-formed but guessable/published key must not pass the Production fail-safe.
    // (The dev value is referenced by location + type, not inlined, so this file holds no
    // secret literal.)
    private static bool IsWeakKey(string hex) => IsRepeatingPattern(hex);

    // True when the value is a unit of length < len repeated to fill it (unit lengths that
    // divide the string: 1, 2, 4, 8, 16, 32 for a 64-char key). Compared case-insensitively
    // so "AAAA..." and "aaaa..." are both caught.
    private static bool IsRepeatingPattern(string hex)
    {
        var len = hex.Length;
        for (var unit = 1; unit <= len / 2; unit++)
        {
            if (len % unit != 0) continue;
            var matches = true;
            for (var i = unit; i < len && matches; i++)
                if (char.ToLowerInvariant(hex[i]) != char.ToLowerInvariant(hex[i - unit])) matches = false;
            if (matches) return true;
        }
        return false;
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
