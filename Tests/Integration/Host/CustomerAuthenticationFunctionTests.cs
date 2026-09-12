using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Ports;
using GarageFlow.Serverless.Host;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;
using Moq;

namespace GarageFlow.Serverless.Tests.Integration.Host;

public sealed class CustomerAuthenticationFunctionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerReturnsExactSuccessResponseForPlainOrBase64Body(bool base64Encoded)
    {
        const string body = "{\"cpf\":\"529.982.247-25\",\"password\":\"password\"}";
        var customer = new VerifiedCustomer(Guid.NewGuid(), Guid.NewGuid(), "Customer", true);
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync(
                "52998224725",
                "password",
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ReturnsAsync(customer);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        tokenIssuer
            .Setup(candidate => candidate.IssueAsync(
                customer,
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ReturnsAsync("user-token");
        var function = new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object));
        var request = CreateRequest(base64Encoded ? Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) : body);
        request.IsBase64Encoded = base64Encoded;

        var response = await function.FunctionHandler(request, CreateContext(TimeSpan.FromSeconds(10)));

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal("user-token", json.RootElement.GetProperty("token").GetString());
        Assert.True(json.RootElement.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal("Bearer", json.RootElement.GetProperty("tokenType").GetString());
        Assert.Equal(900, json.RootElement.GetProperty("expiresIn").GetInt32());
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"cpf\":52998224725,\"password\":\"password\"}")]
    [InlineData("{\"cpf\":\"52998224725\",\"password\":true}")]
    public async Task HandlerReturnsGenericValidationErrorForMalformedBody(string? body)
    {
        var function = CreateStrictFunction();

        var response = await function.FunctionHandler(CreateRequest(body), null!);

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsValidationErrorForInvalidBase64()
    {
        var function = CreateStrictFunction();
        var request = CreateRequest("not-base64!");
        request.IsBase64Encoded = true;

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsValidationErrorForInvalidCpfBeforeDependencies()
    {
        var function = CreateStrictFunction();

        var response = await function.FunctionHandler(
            CreateRequest("{\"cpf\":\"11111111111\",\"password\":\"password\"}"),
            CreateContext(TimeSpan.FromSeconds(10)));

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsValidationErrorForInvalidBase64Utf8()
    {
        var function = CreateStrictFunction();
        var request = CreateRequest(Convert.ToBase64String([0xC3, 0x28]));
        request.IsBase64Encoded = true;

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerRejectsDecodedBodyOverEightKiBBeforeDependencies()
    {
        var function = CreateStrictFunction();
        var request = CreateRequest(Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 8193))));
        request.IsBase64Encoded = true;

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal((int)HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsNotFoundForWrongPathBeforeDependencies()
    {
        var function = CreateStrictFunction();
        var request = CreateRequest("{}");
        request.RawPath = "/other";
        request.RequestContext.Http.Path = "/other";

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal((int)HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsMethodNotAllowedForWrongMethodBeforeDependencies()
    {
        var function = CreateStrictFunction();
        var request = CreateRequest("{}");
        request.RequestContext.Http.Method = "GET";

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal((int)HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", response.Headers["Allow"]);
        Assert.Equal("method_not_allowed", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerReturnsInvalidCredentialsWhenVerifierReturnsNull()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync(
                "52998224725",
                "wrong",
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ReturnsAsync((VerifiedCustomer?)null);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var function = new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object));

        var response = await function.FunctionHandler(
            CreateRequest("{\"cpf\":\"52998224725\",\"password\":\"wrong\"}"),
            CreateContext(TimeSpan.FromSeconds(10)));

        Assert.Equal((int)HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", ReadDetail(response));
    }

    [Fact]
    public async Task HandlerMapsDependencyFailureToGenericUnavailableResponse()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync(
                "52998224725",
                "password",
                It.Is<CancellationToken>(token => token.CanBeCanceled)))
            .ThrowsAsync(new AuthenticationUnavailableException(new InvalidOperationException("sensitive upstream detail")));
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var function = new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object));

        var response = await function.FunctionHandler(
            CreateRequest("{\"cpf\":\"52998224725\",\"password\":\"password\"}"),
            CreateContext(TimeSpan.FromSeconds(10)));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_unavailable", ReadDetail(response));
        Assert.DoesNotContain("sensitive", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlerReturnsUnavailableBeforeInvocationDeadlineWhenSecretLookupStalls()
    {
        var customer = new VerifiedCustomer(Guid.NewGuid(), Guid.NewGuid(), "Customer", false);
        var verifierToken = CancellationToken.None;
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync("52998224725", "password", It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, _, token) => verifierToken = token)
            .ReturnsAsync(customer);
        var secrets = new DelayedSecretValueProvider(
            new Dictionary<string, string>
            {
                ["arn:user"] = new string('u', 64),
                ["arn:internal"] = new string('i', 64),
            },
            TimeSpan.FromSeconds(1));
        var tokenIssuer = new JwtUserTokenIssuer(
            secrets,
            "arn:user",
            "arn:internal",
            "GarageFlow",
            "GarageFlow.Adapters.Api",
            TimeProvider.System);
        var function = new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer));

        var response = await function.FunctionHandler(
                CreateRequest("{\"cpf\":\"52998224725\",\"password\":\"password\"}"),
                CreateContext(TimeSpan.FromMilliseconds(1100)))
            .WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_unavailable", ReadDetail(response));
        Assert.DoesNotContain("arn:user", response.Body, StringComparison.Ordinal);
        Assert.True(verifierToken.CanBeCanceled);
        Assert.Equal(1, secrets.CallCount);
    }

    [Fact]
    public async Task HandlerDoesNotInvokeDependenciesWhenResponseMarginConsumesRemainingTime()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var function = new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object));

        var response = await function.FunctionHandler(
            CreateRequest("{\"cpf\":\"52998224725\",\"password\":\"password\"}"),
            CreateContext(TimeSpan.FromMilliseconds(500)));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_unavailable", ReadDetail(response));
        verifier.Verify(
            candidate => candidate.VerifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static APIGatewayHttpApiV2ProxyRequest CreateRequest(string? body) => new()
    {
        Version = "2.0",
        RawPath = "/auth/customers/token",
        Body = body,
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
            {
                Method = "POST",
                Path = "/auth/customers/token",
                Protocol = "HTTP/1.1",
                SourceIp = "127.0.0.1",
            },
        },
    };

    private static CustomerAuthenticationFunction CreateStrictFunction()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        return new CustomerAuthenticationFunction(new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object));
    }

    private static string? ReadDetail(APIGatewayHttpApiV2ProxyResponse response)
    {
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.GetProperty("detail").GetString();
    }

    private static ILambdaContext CreateContext(TimeSpan remainingTime) =>
        Mock.Of<ILambdaContext>(context => context.RemainingTime == remainingTime);
}
