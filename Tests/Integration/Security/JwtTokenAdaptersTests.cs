using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Serverless.Tests.Integration.Security;

public sealed class JwtTokenAdaptersTests
{
    private const string UserArn = "arn:user";
    private const string InternalArn = "arn:internal";
    private const string Issuer = "GarageFlow";
    private const string Audience = "GarageFlow.Adapters.Api";
    private static readonly string UserSecret = new('u', 64);
    private static readonly string InternalSecret = new('i', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UserIssuerCreatesExactCustomerTokenClaims()
    {
        var provider = CreateProvider();
        var issuer = new JwtUserTokenIssuer(provider, UserArn, InternalArn, Issuer, Audience, new ManualTimeProvider(Now));
        var customer = new VerifiedCustomer(
            Guid.Parse("2ab02472-9f29-4729-92ad-1f03ba994a84"),
            Guid.Parse("6334f73c-b315-4ab0-bb49-65e141a8927f"),
            "Customer",
            true);

        var serialized = await issuer.IssueAsync(customer, CancellationToken.None);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(serialized);

        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Header.Alg);
        Assert.Equal(Issuer, token.Issuer);
        Assert.Equal(Audience, Assert.Single(token.Audiences));
        Assert.Equal("2ab02472-9f29-4729-92ad-1f03ba994a84", token.Subject);
        Assert.Equal("6334f73c-b315-4ab0-bb49-65e141a8927f", token.Claims.Single(claim => claim.Type == "customer_id").Value);
        Assert.Equal("Customer", token.Claims.Single(claim => claim.Type == "role").Value);
        Assert.Equal("true", token.Claims.Single(claim => claim.Type == "must_change_password").Value);
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), token.Claims.Single(claim => claim.Type == "iat").Value);
        Assert.Equal(Now.ToUnixTimeSeconds(), new DateTimeOffset(token.ValidFrom, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal(Now.AddMinutes(15).ToUnixTimeSeconds(), new DateTimeOffset(token.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.True(Guid.TryParse(token.Claims.Single(claim => claim.Type == "jti").Value, out _));
    }

    [Fact]
    public async Task ServiceIssuerCreatesExactSixtySecondInternalToken()
    {
        var provider = CreateProvider();
        var issuer = new JwtInternalServiceTokenIssuer(provider, InternalArn, UserArn, new ManualTimeProvider(Now));

        var serialized = await issuer.IssueAsync(CancellationToken.None);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(serialized);

        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Header.Alg);
        Assert.Equal("GarageFlow.Serverless", token.Issuer);
        Assert.Equal("GarageFlow.InternalAuth", Assert.Single(token.Audiences));
        Assert.Equal("customer-auth-function", token.Subject);
        Assert.Equal("customer-credentials:verify", token.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), token.Claims.Single(claim => claim.Type == "iat").Value);
        Assert.Equal(Now.ToUnixTimeSeconds(), new DateTimeOffset(token.ValidFrom, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal(Now.AddSeconds(60).ToUnixTimeSeconds(), new DateTimeOffset(token.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task IssuersRejectEqualUserAndInternalSecrets()
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string>
        {
            [UserArn] = UserSecret,
            [InternalArn] = UserSecret,
        });
        var userIssuer = new JwtUserTokenIssuer(provider, UserArn, InternalArn, Issuer, Audience, new ManualTimeProvider(Now));
        var customer = new VerifiedCustomer(Guid.NewGuid(), Guid.NewGuid(), "Customer", false);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            userIssuer.IssueAsync(customer, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(PlaceholderSecretCases))]
    public async Task UserIssuerRejectsPlaceholderSecretWithGenericFailure(string secret)
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string>
        {
            [UserArn] = secret,
            [InternalArn] = InternalSecret,
        });
        var issuer = new JwtUserTokenIssuer(provider, UserArn, InternalArn, Issuer, Audience, new ManualTimeProvider(Now));
        var customer = new VerifiedCustomer(Guid.NewGuid(), Guid.NewGuid(), "Customer", false);

        var exception = await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            issuer.IssueAsync(customer, CancellationToken.None));

        Assert.Equal("Customer authentication is unavailable.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PlaceholderSecretCases))]
    public async Task InternalIssuerRejectsPlaceholderSecretWithGenericFailure(string secret)
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string>
        {
            [UserArn] = UserSecret,
            [InternalArn] = secret,
        });
        var issuer = new JwtInternalServiceTokenIssuer(provider, InternalArn, UserArn, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            issuer.IssueAsync(CancellationToken.None));

        Assert.Equal("Customer authentication is unavailable.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "6334f73c-b315-4ab0-bb49-65e141a8927f", "Customer")]
    [InlineData("2ab02472-9f29-4729-92ad-1f03ba994a84", "00000000-0000-0000-0000-000000000000", "Customer")]
    [InlineData("2ab02472-9f29-4729-92ad-1f03ba994a84", "6334f73c-b315-4ab0-bb49-65e141a8927f", "Admin")]
    public async Task UserIssuerRejectsUnexpectedIdentityBeforeReadingSecrets(
        string userId,
        string customerId,
        string role)
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string>());
        var issuer = new JwtUserTokenIssuer(provider, UserArn, InternalArn, Issuer, Audience, new ManualTimeProvider(Now));
        var customer = new VerifiedCustomer(Guid.Parse(userId), Guid.Parse(customerId), role, false);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            issuer.IssueAsync(customer, CancellationToken.None));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("replace-me-with-a-production-secret-value")]
    public async Task ValidatorRejectsWeakSecretWithoutThrowing(string secret)
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string> { [UserArn] = secret });
        var validator = new JwtUserTokenValidator(provider, UserArn, Issuer, Audience, new ManualTimeProvider(Now));
        var token = JwtTestTokens.Create(UserSecret, Issuer, Audience, Now,
            [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())]);

        var valid = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.False(valid);
    }

    [Theory]
    [MemberData(nameof(PlaceholderSecretCases))]
    public async Task ValidatorRejectsTokenSignedWithPlaceholderSecret(string secret)
    {
        var provider = new StaticSecretValueProvider(new Dictionary<string, string> { [UserArn] = secret });
        var validator = new JwtUserTokenValidator(provider, UserArn, Issuer, Audience, new ManualTimeProvider(Now));
        var token = JwtTestTokens.Create(secret, Issuer, Audience, Now,
            [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())]);

        var valid = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.False(valid);
    }

    [Fact]
    public async Task ValidatorAcceptsCurrentCustomerTokenIncludingTemporaryPasswordFlag()
    {
        var provider = CreateProvider();
        var issuer = new JwtUserTokenIssuer(provider, UserArn, InternalArn, Issuer, Audience, new ManualTimeProvider(Now));
        var validator = new JwtUserTokenValidator(provider, UserArn, Issuer, Audience, new ManualTimeProvider(Now));
        var token = await issuer.IssueAsync(
            new VerifiedCustomer(Guid.NewGuid(), Guid.NewGuid(), "Customer", true),
            CancellationToken.None);

        var valid = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.True(valid);
    }

    [Fact]
    public async Task ValidatorAcceptsLegacyAdminTokenWithoutIssuedAtOrTokenId()
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "19efdb17-3ea7-41a5-9343-1405b26a7189"),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        var token = JwtTestTokens.Create(UserSecret, Issuer, Audience, Now, claims);
        var validator = CreateValidator();

        var valid = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.True(valid);
    }

    [Theory]
    [MemberData(nameof(InvalidTokenCases))]
    public async Task ValidatorRejectsMalformedOrUntrustedTokens(string token)
    {
        var validator = CreateValidator();

        var valid = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.False(valid);
    }

    public static TheoryData<string> InvalidTokenCases()
    {
        var sub = new[] { new Claim(JwtRegisteredClaimNames.Sub, "19efdb17-3ea7-41a5-9343-1405b26a7189") };
        return new TheoryData<string>
        {
            "not-a-token",
            JwtTestTokens.Create(InternalSecret, Issuer, Audience, Now, sub),
            JwtTestTokens.Create(UserSecret, "Wrong", Audience, Now, sub),
            JwtTestTokens.Create(UserSecret, Issuer, "Wrong", Now, sub),
            JwtTestTokens.Create(UserSecret, Issuer, Audience, Now.AddMinutes(-20), sub, expires: Now.AddSeconds(-1)),
            JwtTestTokens.Create(UserSecret, Issuer, Audience, Now, sub, notBefore: Now.AddSeconds(1)),
            JwtTestTokens.Create(UserSecret, Issuer, Audience, Now, [new Claim(JwtRegisteredClaimNames.Sub, "not-a-uuid")]),
            JwtTestTokens.Create(UserSecret, "GarageFlow.Serverless", "GarageFlow.InternalAuth", Now, sub),
            JwtTestTokens.Create(UserSecret, Issuer, Audience, Now, sub, algorithm: SecurityAlgorithms.None),
        };
    }

    public static TheoryData<string> PlaceholderSecretCases() => new()
    {
        "__SET_ME_JWT_SECRET________________",
        "replace_this_secret_with_real_value",
        "PREFIX_CHANGEme_SECRET_VALUE_1234567890",
        "PREFIX_PLACEHOLDER_SECRET_VALUE_1234567890",
        "PREFIX_<SeT-Me>_SECRET_VALUE_1234567890",
        "PREFIX_EXAMPLE_SECRET_VALUE_1234567890",
    };

    private static StaticSecretValueProvider CreateProvider() => new(new Dictionary<string, string>
    {
        [UserArn] = UserSecret,
        [InternalArn] = InternalSecret,
    });

    private static JwtUserTokenValidator CreateValidator() =>
        new(CreateProvider(), UserArn, Issuer, Audience, new ManualTimeProvider(Now));
}
