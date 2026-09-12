using Amazon.SecretsManager;
using GarageFlow.Serverless.Adapters.Infrastructure.Customers.Authentication;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Jwt;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Secrets;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Ports;
using GarageFlow.Serverless.Application.Security.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace GarageFlow.Serverless.Host;

internal static class RuntimeComposition
{
    private const string DefaultIssuer = "GarageFlow";
    private const string DefaultAudience = "GarageFlow.Adapters.Api";

    public static AuthenticateCustomerHandler CreateAuthenticationHandler()
    {
        var services = CreateCommonServices();
        var userSecretArn = GetEnvironmentValue("JWT_SECRET_ARN");
        var internalSecretArn = GetEnvironmentValue("INTERNAL_AUTH_SECRET_ARN");
        var issuer = GetEnvironmentValue("JWT_ISSUER", DefaultIssuer);
        var audience = GetEnvironmentValue("JWT_AUDIENCE", DefaultAudience);
        var settings = InternalApiSettings.Create(
            GetEnvironmentValue("INTERNAL_API_BASE_URL"),
            GetEnvironmentValue("INTERNAL_API_TRANSPORT"));

        services.AddSingleton(settings);
        services.AddSingleton<IInternalServiceTokenIssuer>(provider => new JwtInternalServiceTokenIssuer(
            provider.GetRequiredService<ISecretValueProvider>(),
            internalSecretArn,
            userSecretArn,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IUserTokenIssuer>(provider => new JwtUserTokenIssuer(
            provider.GetRequiredService<ISecretValueProvider>(),
            userSecretArn,
            internalSecretArn,
            issuer,
            audience,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        });
        services.AddSingleton<ICustomerCredentialsVerifier, HttpCustomerCredentialsVerifier>();
        services.AddSingleton<AuthenticateCustomerHandler>();

        return services.BuildServiceProvider(validateScopes: true).GetRequiredService<AuthenticateCustomerHandler>();
    }

    public static IUserTokenValidator CreateAuthorizerTokenValidator()
    {
        var services = CreateCommonServices();
        var userSecretArn = GetEnvironmentValue("JWT_SECRET_ARN");
        var issuer = GetEnvironmentValue("JWT_ISSUER", DefaultIssuer);
        var audience = GetEnvironmentValue("JWT_AUDIENCE", DefaultAudience);

        services.AddSingleton<IUserTokenValidator>(provider => new JwtUserTokenValidator(
            provider.GetRequiredService<ISecretValueProvider>(),
            userSecretArn,
            issuer,
            audience,
            provider.GetRequiredService<TimeProvider>()));

        return services.BuildServiceProvider(validateScopes: true).GetRequiredService<IUserTokenValidator>();
    }

    private static ServiceCollection CreateCommonServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient());
        services.AddSingleton<ISecretValueProvider, SecretsManagerSecretValueProvider>();
        return services;
    }

    private static string GetEnvironmentValue(string key, string? defaultValue = null) =>
        Environment.GetEnvironmentVariable(key) ?? defaultValue ?? string.Empty;
}
