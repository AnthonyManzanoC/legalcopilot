using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegalPilot.Api.Domain;

namespace LegalPilot.Api.Application;

public sealed class PasswordHasher
{
    public (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string password, string hash, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public sealed class TokenService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<TokenService> logger)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _developmentSigningKey = RandomNumberGenerator.GetBytes(64);
    private bool _developmentWarningLogged;

    public string Create(AuthPrincipal principal, TimeSpan lifetime)
    {
        var key = GetSigningKey();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "LPJWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = principal.UserId,
            tenant = principal.TenantId,
            email = principal.Email,
            roles = principal.Roles.Select(r => r.ToString()).ToArray(),
            exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds(),
            iss = configuration["LegalPilot:Issuer"] ?? "legalpilot-ecuador"
        }, _jsonOptions));
        var signature = Sign($"{header}.{payload}", key);
        return $"{header}.{payload}.{signature}";
    }

    public AuthPrincipal? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var expected = Sign($"{parts[0]}.{parts[1]}", GetSigningKey());
        if (!FixedEquals(expected, parts[2]))
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var exp = root.GetProperty("exp").GetInt64();
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
        {
            return null;
        }

        var roles = root.GetProperty("roles")
            .EnumerateArray()
            .Select(e => Enum.TryParse<UserRole>(e.GetString(), out var role) ? role : UserRole.Client)
            .ToArray();

        return new AuthPrincipal(
            root.GetProperty("sub").GetGuid(),
            root.GetProperty("tenant").GetGuid(),
            root.GetProperty("email").GetString() ?? string.Empty,
            roles);
    }

    public static string RandomToken(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static (string RawToken, RefreshTokenSession Session) BuildRefreshSession(UserAccount user, string? ipAddress)
    {
        var raw = RandomToken(48);
        return (raw, new RefreshTokenSession(
            Guid.NewGuid(),
            user.TenantId,
            user.Id,
            Sha256(raw),
            DateTimeOffset.UtcNow.AddDays(14),
            DateTimeOffset.UtcNow,
            ipAddress,
            null,
            null));
    }

    public static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }

    private byte[] GetSigningKey()
    {
        var raw = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_TOKEN_SIGNING_KEY"),
            configuration["LegalPilot:TokenSigningKey"]);

        if (IsPlaceholder(raw) || raw!.Length < 32)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException("LegalPilot requiere LEGALPILOT_TOKEN_SIGNING_KEY de 32+ caracteres en produccion.");
            }

            if (!_developmentWarningLogged)
            {
                _developmentWarningLogged = true;
                logger.LogWarning("Using ephemeral development signing key. Configure LEGALPILOT_TOKEN_SIGNING_KEY for durable sessions.");
            }

            return _developmentSigningKey;
        }

        return Encoding.UTF8.GetBytes(raw);
    }

    private static bool IsPlaceholder(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("DEV_ONLY", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstConfigured(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string Sign(string value, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}

public sealed class SecretProtector(IConfiguration configuration, IWebHostEnvironment environment)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[]? _cachedKey = ResolveKey(configuration, environment);

    public bool Configured => _cachedKey is not null;

    public string Protect(string secret)
    {
        var key = RequireKey();
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var envelope = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, envelope, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, envelope, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, envelope, NonceSize + TagSize, ciphertext.Length);
        return Convert.ToBase64String(envelope);
    }

    public string Unprotect(string protectedSecret)
    {
        var key = RequireKey();
        var envelope = Convert.FromBase64String(protectedSecret);
        if (envelope.Length <= NonceSize + TagSize)
        {
            throw new InvalidOperationException("Token cifrado invalido.");
        }

        var nonce = envelope[..NonceSize];
        var tag = envelope[NonceSize..(NonceSize + TagSize)];
        var ciphertext = envelope[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] RequireKey()
    {
        return _cachedKey ?? throw new InvalidOperationException("Configure LEGALPILOT_DATA_PROTECTION_KEY para cifrar tokens OAuth y secretos operativos.");
    }

    private static byte[]? ResolveKey(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var raw = FirstConfigured(
            Environment.GetEnvironmentVariable("LEGALPILOT_DATA_PROTECTION_KEY"),
            configuration["LegalPilot:Security:DataProtectionKey"]);

        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("DEV_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException("LegalPilot requiere LEGALPILOT_DATA_PROTECTION_KEY en produccion para proteger tokens OAuth.");
            }

            return null;
        }

        try
        {
            var decoded = Convert.FromBase64String(raw);
            if (decoded.Length is 32)
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
            // Plain text keys are hashed below so operators may use secret-manager strings.
        }

        if (raw.Length < 32)
        {
            throw new InvalidOperationException("LEGALPILOT_DATA_PROTECTION_KEY debe tener al menos 32 caracteres o ser base64 de 32 bytes.");
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static string? FirstConfigured(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

public static class HttpAuth
{
    public static AuthPrincipal RequirePrincipal(HttpRequest request, TokenService tokens)
    {
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : null;

        return tokens.Validate(token) ?? throw new UnauthorizedAccessException("Token invalido o ausente.");
    }

    public static void RequireRole(AuthPrincipal principal, params UserRole[] roles)
    {
        if (!principal.HasAnyRole(roles))
        {
            throw new ForbiddenOperationException("No tiene permisos para esta accion.");
        }
    }
}
