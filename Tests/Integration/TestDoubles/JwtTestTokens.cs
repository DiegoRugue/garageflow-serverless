using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Serverless.Tests.Integration.TestDoubles;

internal static class JwtTestTokens
{
    public static string Create(
        string secret,
        string issuer,
        string audience,
        DateTimeOffset now,
        IEnumerable<Claim> claims,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var credentials = algorithm == SecurityAlgorithms.None
            ? null
            : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), algorithm);

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            (notBefore ?? now).UtcDateTime,
            (expires ?? now.AddMinutes(15)).UtcDateTime,
            credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
