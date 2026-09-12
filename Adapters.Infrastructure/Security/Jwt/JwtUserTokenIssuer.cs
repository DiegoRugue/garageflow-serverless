using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Ports;
using GarageFlow.Serverless.Application.Security.Ports;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;

public sealed class JwtUserTokenIssuer : IUserTokenIssuer
{
    public const int TokenLifetimeSeconds = 900;

    private readonly ISecretValueProvider _secretValueProvider;
    private readonly string _userSecretArn;
    private readonly string _internalSecretArn;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeProvider _timeProvider;

    public JwtUserTokenIssuer(
        ISecretValueProvider secretValueProvider,
        string userSecretArn,
        string internalSecretArn,
        string issuer,
        string audience,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secretValueProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _secretValueProvider = secretValueProvider;
        _userSecretArn = RequireConfiguration(userSecretArn);
        _internalSecretArn = RequireConfiguration(internalSecretArn);
        _issuer = RequireConfiguration(issuer);
        _audience = RequireConfiguration(audience);
        _timeProvider = timeProvider;
    }

    public async Task<string> IssueAsync(VerifiedCustomer customer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);

        if (customer.UserId == Guid.Empty ||
            customer.CustomerId == Guid.Empty ||
            !string.Equals(customer.Role, "Customer", StringComparison.Ordinal))
        {
            throw new AuthenticationUnavailableException();
        }

        var userSecret = await _secretValueProvider.GetAsync(_userSecretArn, cancellationToken).ConfigureAwait(false);
        var internalSecret = await _secretValueProvider.GetAsync(_internalSecretArn, cancellationToken).ConfigureAwait(false);
        JwtSecretGuard.ValidateDistinct(userSecret, internalSecret);

        var now = _timeProvider.GetUtcNow();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, customer.UserId.ToString("D")),
            new Claim("customer_id", customer.CustomerId.ToString("D")),
            new Claim("role", customer.Role),
            new Claim("must_change_password", customer.MustChangePassword ? "true" : "false", ClaimValueTypes.Boolean),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(userSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _issuer,
            _audience,
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
