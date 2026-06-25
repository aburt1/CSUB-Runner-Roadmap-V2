namespace Api.Auth;

// bcrypt password + key hashing. Work factor is fixed at 10 so already-stored
// hashes keep verifying.
public static class Passwords
{
    public static string Hash(string plaintext) => BCrypt.Net.BCrypt.HashPassword(plaintext, 10);

    public static bool Verify(string plaintext, string hash) => BCrypt.Net.BCrypt.Verify(plaintext, hash);
}
