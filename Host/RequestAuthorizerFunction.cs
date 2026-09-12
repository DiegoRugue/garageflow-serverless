using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GarageFlow.Serverless.Application.Security.Ports;

namespace GarageFlow.Serverless.Host;

public sealed class RequestAuthorizerFunction
{
    private readonly Lazy<IUserTokenValidator> _tokenValidator;

    public RequestAuthorizerFunction()
        : this(new Lazy<IUserTokenValidator>(RuntimeComposition.CreateAuthorizerTokenValidator))
    {
    }

    public RequestAuthorizerFunction(IUserTokenValidator tokenValidator)
        : this(new Lazy<IUserTokenValidator>(() => tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator))))
    {
    }

    private RequestAuthorizerFunction(Lazy<IUserTokenValidator> tokenValidator)
    {
        _tokenValidator = tokenValidator;
    }

    public async Task<APIGatewayCustomAuthorizerV2SimpleResponse> FunctionHandler(
        APIGatewayCustomAuthorizerV2Request request,
        ILambdaContext context)
    {
        try
        {
            if (!TryGetBearerToken(request?.Headers, out var token))
            {
                return Deny();
            }

            using var invocationDeadline = InvocationDeadline.Start(context);
            if (invocationDeadline is null)
            {
                return Deny();
            }

            var isAuthorized = await _tokenValidator.Value
                .ValidateAsync(token, invocationDeadline.Token)
                .ConfigureAwait(false);
            return new APIGatewayCustomAuthorizerV2SimpleResponse { IsAuthorized = isAuthorized };
        }
        catch
        {
            return Deny();
        }
    }

    private static bool TryGetBearerToken(
        IDictionary<string, string>? headers,
        out string token)
    {
        token = string.Empty;
        if (headers is null)
        {
            return false;
        }

        var authorizationHeaders = headers
            .Where(header => string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (authorizationHeaders.Length != 1 || string.IsNullOrWhiteSpace(authorizationHeaders[0].Value))
        {
            return false;
        }

        var value = authorizationHeaders[0].Value.Trim();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = value["Bearer ".Length..].Trim();
        return token.Length > 0 && !token.Any(char.IsWhiteSpace) && !token.Contains(',');
    }

    private static APIGatewayCustomAuthorizerV2SimpleResponse Deny() => new() { IsAuthorized = false };
}
