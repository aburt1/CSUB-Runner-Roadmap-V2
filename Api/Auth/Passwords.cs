namespace Api.Auth;

// bcrypt password + key hashing, same work factor (10) as the old server.
public static class Passwords
{
    public static string Hash(string plaintext) => BCrypt.Net.BCrypt.HashPassword(plaintext, 10);

    public static bool Verify(string plaintext, string hash) => BCrypt.Net.BCrypt.Verify(plaintext, hash);
}
