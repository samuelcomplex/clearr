using System.Security.Cryptography;
using System.Text;
using Clearr.Storage;

namespace Clearr.Api;

/// <summary>
/// Optional UI password (PBKDF2) with an HMAC-signed session cookie, plus an *arr-style API key
/// (X-Api-Key header) for scripts and Home Assistant.
/// </summary>
public class AuthService
{
    public const string Cookie = "clearr_session";
    private readonly SettingsStore _settings;
    private readonly byte[] _secret;

    public AuthService(SettingsStore settings, ConfigPaths paths)
    {
        _settings = settings;
        var secretPath = paths.File("session.key");
        if (!File.Exists(secretPath))
        {
            SecureFile.WriteAllBytes(secretPath, RandomNumberGenerator.GetBytes(32));
        }

        _secret = File.ReadAllBytes(secretPath);

        var keyPath = paths.File("api.key");
        if (!File.Exists(keyPath))
        {
            SecureFile.WriteAllText(keyPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        }

        ApiKey = File.ReadAllText(keyPath).Trim();
    }

    public string ApiKey { get; }

    public bool PasswordRequired => !string.IsNullOrEmpty(_settings.Get().PasswordHash);

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password)
    {
        var parts = _settings.Get().PasswordHash.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iter))
        {
            return false;
        }

        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iter, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[3]));
    }

    public string IssueSession()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds().ToString();
        return expires + "." + Sign(expires);
    }

    public bool IsAuthenticated(HttpContext ctx)
    {
        if (!PasswordRequired || HasValidApiKey(ctx))
        {
            return true;
        }

        var c = ctx.Request.Cookies[Cookie];
        var dot = c?.IndexOf('.') ?? -1;
        if (c == null || dot < 0)
        {
            return false;
        }

        var exp = c[..dot];
        return long.TryParse(exp, out var e) && e > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c[(dot + 1)..]), Encoding.UTF8.GetBytes(Sign(exp)));
    }

    public bool HasValidApiKey(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("X-Api-Key", out var key)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()), Encoding.UTF8.GetBytes(ApiKey));

    // Sessions are bound to the current password: changing or removing it signs everyone out.
    private string Sign(string value) =>
        Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(value + "|" + _settings.Get().PasswordHash)));
}
