using System.IdentityModel.Tokens.Jwt;
using System.Text;
using GarageFlow.Serverless.Application.Security.Ports;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;

public sealed class JwtUserTokenValidator : IUserTokenValidator
{
    private readonly ISecretValueProvider _secretValueProvider;
    private readonly string _userSecretArn;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeProvider _timeProvider;

    public JwtUserTokenValidator(
        ISecretValueProvider secretValueProvider,
        string userSecretArn,
        string issuer,
        string audience,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secretValueProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _secretValueProvider = secretValueProvider;
        _userSecretArn = userSecretArn;
        _issuer = issuer;
        _audience = audience;
        _timeProvider = timeProvider;
    }

    public async Task<bool> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(token))
            {
                return false;
            }

            var unvalidatedToken = handler.ReadJwtToken(token);
            if (!string.Equals(unvalidatedToken.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
            {
                return false;
            }

            var secret = await _secretValueProvider.GetAsync(_userSecretArn, cancellationToken).ConfigureAwait(false);
            JwtSecretGuard.Validate(secret);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    notBefore.HasValue && expires.HasValue && notBefore.Value <= now && expires.Value > now,
            };

            var principal = handler.ValidateToken(token, parameters, out _);
            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(subject, out var userId) && userId != Guid.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
