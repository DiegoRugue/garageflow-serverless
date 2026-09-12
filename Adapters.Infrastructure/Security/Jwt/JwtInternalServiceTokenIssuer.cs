using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Security.Ports;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;

public sealed class JwtInternalServiceTokenIssuer : IInternalServiceTokenIssuer
{
    public const int TokenLifetimeSeconds = 60;
    public const string Issuer = "GarageFlow.Serverless";
    public const string Audience = "GarageFlow.InternalAuth";
    public const string Subject = "customer-auth-function";
    public const string Scope = "customer-credentials:verify";

    private readonly ISecretValueProvider _secretValueProvider;
    private readonly string _internalSecretArn;
    private readonly string _userSecretArn;
    private readonly TimeProvider _timeProvider;

    public JwtInternalServiceTokenIssuer(
        ISecretValueProvider secretValueProvider,
        string internalSecretArn,
        string userSecretArn,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secretValueProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _secretValueProvider = secretValueProvider;
        _internalSecretArn = RequireConfiguration(internalSecretArn);
        _userSecretArn = RequireConfiguration(userSecretArn);
        _timeProvider = timeProvider;
    }

    public async Task<string> IssueAsync(CancellationToken cancellationToken)
    {
        var internalSecret = await _secretValueProvider.GetAsync(_internalSecretArn, cancellationToken).ConfigureAwait(false);
        var userSecret = await _secretValueProvider.GetAsync(_userSecretArn, cancellationToken).ConfigureAwait(false);
        JwtSecretGuard.ValidateDistinct(userSecret, internalSecret);

        var now = _timeProvider.GetUtcNow();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Subject),
            new Claim("scope", Scope),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(internalSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            now.UtcDateTime,
            now.AddSeconds(TokenLifetimeSeconds).UtcDateTime,
            credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string RequireConfiguration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AuthenticationUnavailableException();
        }

        return value;
    }
}
