using System.Reflection;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Domain.Customers.ValueObjects;
using GarageFlow.Serverless.Host;

namespace GarageFlow.Serverless.Tests.Integration.Architecture;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void ProductionAssembliesRespectDependencyDirection()
    {
        AssertGarageFlowReferences(typeof(Cpf).Assembly);
        AssertGarageFlowReferences(typeof(AuthenticateCustomerHandler).Assembly, "GarageFlow.Serverless.Domain");
        AssertGarageFlowReferences(typeof(JwtUserTokenIssuer).Assembly, "GarageFlow.Serverless.Application");
        AssertGarageFlowReferences(
            typeof(CustomerAuthenticationFunction).Assembly,
            "GarageFlow.Serverless.Adapters.Infrastructure",
            "GarageFlow.Serverless.Application");
    }

    [Fact]
    public void InnerRingsDoNotReferenceFrameworkOrSdkAssemblies()
    {
        var forbiddenPrefixes = new[]
        {
            "Amazon.",
            "AWSSDK.",
            "Microsoft.Extensions.",
            "Microsoft.IdentityModel.",
            "System.IdentityModel.",
        };

        foreach (var assembly in new[] { typeof(Cpf).Assembly, typeof(AuthenticateCustomerHandler).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
            Assert.DoesNotContain(references, name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void PublicLambdaHandlersHaveRequiredAwsSignatures()
    {
        var authenticationMethod = typeof(CustomerAuthenticationFunction).GetMethod("FunctionHandler");
        var authorizerMethod = typeof(RequestAuthorizerFunction).GetMethod("FunctionHandler");

        Assert.NotNull(authenticationMethod);
        Assert.Equal(
            typeof(Task<APIGatewayHttpApiV2ProxyResponse>),
            authenticationMethod.ReturnType);
        Assert.Equal(
            [typeof(APIGatewayHttpApiV2ProxyRequest), typeof(ILambdaContext)],
            authenticationMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        Assert.NotNull(authorizerMethod);
        Assert.Equal(
            typeof(Task<APIGatewayCustomAuthorizerV2SimpleResponse>),
            authorizerMethod.ReturnType);
        Assert.Equal(
            [typeof(APIGatewayCustomAuthorizerV2Request), typeof(ILambdaContext)],
            authorizerMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    private static void AssertGarageFlowReferences(Assembly assembly, params string[] expected)
    {
        var actual = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("GarageFlow.", StringComparison.Ordinal) == true)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sortedExpected = expected.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(sortedExpected, actual);
    }
}
