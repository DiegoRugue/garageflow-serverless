using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.Core;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;
using GarageFlow.Serverless.Application.Security.Ports;
using GarageFlow.Serverless.Host;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;
using Moq;

namespace GarageFlow.Serverless.Tests.Integration.Host;

public sealed class RequestAuthorizerFunctionTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    public async Task HandlerPassesCaseInsensitiveBearerTokenToValidator(string headerName)
    {
        var validator = new Mock<IUserTokenValidator>(MockBehavior.Strict);
        validator
            .Setup(candidate => candidate.ValidateAsync(
                "user-token",
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ReturnsAsync(true);
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Type = "REQUEST",
            RouteArn = "arn:aws:execute-api:us-east-1:123456789012:api/stage/GET/resource",
            Headers = new Dictionary<string, string> { [headerName] = "Bearer user-token" },
        };

        var response = await function.FunctionHandler(request, CreateContext(TimeSpan.FromSeconds(5)));

        Assert.True(response.IsAuthorized);
        var serializer = new DefaultLambdaJsonSerializer();
        await using var stream = new MemoryStream();
        serializer.Serialize(response, stream);
        using var json = JsonDocument.Parse(stream.ToArray());
        Assert.True(json.RootElement.GetProperty("isAuthorized").GetBoolean());
        Assert.Single(json.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task HandlerRejectsAmbiguousAuthorizationHeadersWithoutValidation()
    {
        var validator = new Mock<IUserTokenValidator>(MockBehavior.Strict);
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer first",
                ["authorization"] = "Bearer second",
            },
        };

        var response = await function.FunctionHandler(request, null!);

        Assert.False(response.IsAuthorized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Basic abc")]
    [InlineData("Bearer first second")]
    [InlineData("Bearer first,Bearer second")]
    public async Task HandlerRejectsMissingOrMalformedBearerWithoutValidation(string? value)
    {
        var validator = new Mock<IUserTokenValidator>(MockBehavior.Strict);
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = value is null
                ? null
                : new Dictionary<string, string> { ["Authorization"] = value },
        };

        var response = await function.FunctionHandler(request, null!);

        Assert.False(response.IsAuthorized);
    }

    [Fact]
    public async Task HandlerDeniesWhenValidatorFails()
    {
        var validator = new Mock<IUserTokenValidator>(MockBehavior.Strict);
        validator
            .Setup(candidate => candidate.ValidateAsync(
                "token",
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ThrowsAsync(new InvalidOperationException("secret unavailable"));
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        var response = await function.FunctionHandler(request, CreateContext(TimeSpan.FromSeconds(5)));

        Assert.False(response.IsAuthorized);
    }

    [Fact]
    public async Task HandlerDeniesBeforeInvocationDeadlineWhenSecretLookupStalls()
    {
        const string issuer = "GarageFlow";
        const string audience = "GarageFlow.Adapters.Api";
        var secret = new string('u', 64);
        var now = DateTimeOffset.UtcNow;
        var token = JwtTestTokens.Create(
            secret,
            issuer,
            audience,
            now,
            [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString("D"))],
            now.AddMinutes(-1),
            now.AddMinutes(1));
        var secrets = new DelayedSecretValueProvider(
            new Dictionary<string, string> { ["arn:user"] = secret },
            TimeSpan.FromSeconds(1));
        var validator = new JwtUserTokenValidator(secrets, "arn:user", issuer, audience, TimeProvider.System);
        var function = new RequestAuthorizerFunction(validator);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        };

        var response = await function.FunctionHandler(
                request,
                CreateContext(TimeSpan.FromMilliseconds(1100)))
            .WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.False(response.IsAuthorized);
        Assert.Equal(1, secrets.CallCount);
    }

    [Fact]
    public async Task HandlerDoesNotInvokeValidatorWhenResponseMarginConsumesRemainingTime()
    {
        var validator = new Mock<IUserTokenValidator>(MockBehavior.Strict);
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        var response = await function.FunctionHandler(request, CreateContext(TimeSpan.FromMilliseconds(500)));

        Assert.False(response.IsAuthorized);
        validator.Verify(
            candidate => candidate.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ILambdaContext CreateContext(TimeSpan remainingTime) =>
        Mock.Of<ILambdaContext>(context => context.RemainingTime == remainingTime);
}
