using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using GarageFlow.Serverless.Application.Security.Ports;
using GarageFlow.Serverless.Host;
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
            .Setup(candidate => candidate.ValidateAsync("user-token", CancellationToken.None))
            .ReturnsAsync(true);
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Type = "REQUEST",
            RouteArn = "arn:aws:execute-api:us-east-1:123456789012:api/stage/GET/resource",
            Headers = new Dictionary<string, string> { [headerName] = "Bearer user-token" },
        };

        var response = await function.FunctionHandler(request, null!);

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
            .Setup(candidate => candidate.ValidateAsync("token", CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("secret unavailable"));
        var function = new RequestAuthorizerFunction(validator.Object);
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        var response = await function.FunctionHandler(request, null!);

        Assert.False(response.IsAuthorized);
    }
}
