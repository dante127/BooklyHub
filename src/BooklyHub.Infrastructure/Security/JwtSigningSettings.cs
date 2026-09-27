using System.Text;
using Microsoft.Extensions.Configuration;

namespace BooklyHub.Infrastructure.Security;

public sealed record JwtSigningSettings(string Secret, string Issuer, string Audience, int ExpirationMinutes)
{
    public const string SecretKey = "Jwt:Secret";

    // HS256 requires a >=256-bit key; shorter keys silently weaken signatures.
    private const int MinimumSecretBytes = 32;

    public static JwtSigningSettings FromConfiguration(IConfiguration configuration)
    {
        var secret = Require(configuration[SecretKey], SecretKey);

        if (Encoding.UTF8.GetByteCount(secret) < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                $"{SecretKey} must be at least {MinimumSecretBytes} bytes for HS256 signing. Current length: {Encoding.UTF8.GetByteCount(secret)} bytes.");
        }

        var issuer = Require(configuration["Jwt:Issuer"], "Jwt:Issuer");
        var audience = Require(configuration["Jwt:Audience"], "Jwt:Audience");

        var expirationMinutes = int.TryParse(configuration["Jwt:ExpirationMinutes"], out var parsed) ? parsed : 60;
        if (expirationMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException($"Jwt:ExpirationMinutes must be between 1 and 1440. Current value: {expirationMinutes}.");
        }

        return new JwtSigningSettings(secret, issuer, audience, expirationMinutes);
    }

    private static string Require(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key}' is missing or empty. Provide it via environment variable '{key.Replace(":", "__")}', a mounted secret, or dotnet user-secrets. There is no built-in fallback value.");
        }

        return value;
    }
}
