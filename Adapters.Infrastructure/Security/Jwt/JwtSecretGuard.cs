using System.Text;
using GarageFlow.Serverless.Application.Customers.Authentication;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;

internal static class JwtSecretGuard
{
    private static readonly string[] PlaceholderFragments =
    [
        "set_me",
        "changeme",
        "replace",
        "placeholder",
        "<set-me>",
        "example",
        "replace-me",
        "replace_with",
        "your-secret",
        "example-secret",
    ];

    public static void Validate(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            Encoding.UTF8.GetByteCount(secret) < 32 ||
            PlaceholderFragments.Any(fragment => secret.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AuthenticationUnavailableException();
        }
    }

    public static void ValidateDistinct(string userSecret, string internalSecret)
    {
        Validate(userSecret);
        Validate(internalSecret);

        if (string.Equals(userSecret, internalSecret, StringComparison.Ordinal))
        {
            throw new AuthenticationUnavailableException();
        }
    }
}
