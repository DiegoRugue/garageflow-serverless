using System.Reflection;
using Amazon.Lambda.APIGatewayEvents;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Security.Ports;
using GarageFlow.Serverless.Host;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;

namespace GarageFlow.Serverless.Tests.Integration.Host;

public sealed class RuntimeCompositionTests
{
    [Fact]
    public void CompositionBuildsBothRuntimeGraphsFromEnvironment()
    {
        using var region = new EnvironmentVariableScope("AWS_REGION", "us-east-1");
        using var userArn = new EnvironmentVariableScope("JWT_SECRET_ARN", "arn:aws:secretsmanager:us-east-1:123456789012:secret:user");
        using var internalArn = new EnvironmentVariableScope("INTERNAL_AUTH_SECRET_ARN", "arn:aws:secretsmanager:us-east-1:123456789012:secret:internal");
        using var baseUrl = new EnvironmentVariableScope("INTERNAL_API_BASE_URL", "https://internal.example");
        using var transport = new EnvironmentVariableScope("INTERNAL_API_TRANSPORT", "https");
        using var issuer = new EnvironmentVariableScope("JWT_ISSUER", null);
        using var audience = new EnvironmentVariableScope("JWT_AUDIENCE", null);
        var composition = typeof(CustomerAuthenticationFunction).Assembly.GetType(
            "GarageFlow.Serverless.Host.RuntimeComposition",
            throwOnError: true)!;

        var authenticationHandler = InvokeFactory(composition, "CreateAuthenticationHandler");
        var tokenValidator = InvokeFactory(composition, "CreateAuthorizerTokenValidator");

        Assert.IsType<AuthenticateCustomerHandler>(authenticationHandler);
        Assert.IsAssignableFrom<IUserTokenValidator>(tokenValidator);
    }

    [Fact]
    public async Task ParameterlessAuthenticationHandlerMapsMissingConfigurationToUnavailable()
    {
        using var region = new EnvironmentVariableScope("AWS_REGION", "us-east-1");
        using var userArn = new EnvironmentVariableScope("JWT_SECRET_ARN", null);
        using var internalArn = new EnvironmentVariableScope("INTERNAL_AUTH_SECRET_ARN", null);
        using var baseUrl = new EnvironmentVariableScope("INTERNAL_API_BASE_URL", null);
        using var transport = new EnvironmentVariableScope("INTERNAL_API_TRANSPORT", null);
        var function = new CustomerAuthenticationFunction();
        var request = new APIGatewayHttpApiV2ProxyRequest
        {
            RawPath = "/auth/customers/token",
            Body = "{\"cpf\":\"52998224725\",\"password\":\"password\"}",
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "POST" },
            },
        };

        var response = await function.FunctionHandler(request, null!);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("authentication_unavailable", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParameterlessAuthorizerDeniesWhenSecretArnIsMissing()
    {
        using var region = new EnvironmentVariableScope("AWS_REGION", "us-east-1");
        using var userArn = new EnvironmentVariableScope("JWT_SECRET_ARN", null);
        var function = new RequestAuthorizerFunction();
        var request = new APIGatewayCustomAuthorizerV2Request
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        var response = await function.FunctionHandler(request, null!);

        Assert.False(response.IsAuthorized);
    }

    private static object InvokeFactory(Type composition, string methodName)
    {
        var method = composition.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<object>(method.Invoke(null, null));
    }
}
