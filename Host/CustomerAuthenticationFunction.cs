using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GarageFlow.Serverless.Application.Customers.Authentication;

namespace GarageFlow.Serverless.Host;

public sealed class CustomerAuthenticationFunction
{
    public const string AuthenticationPath = "/auth/customers/token";
    public const int MaximumRequestBodyBytes = 8192;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Lazy<AuthenticateCustomerHandler> _handler;

    public CustomerAuthenticationFunction()
        : this(new Lazy<AuthenticateCustomerHandler>(RuntimeComposition.CreateAuthenticationHandler))
    {
    }

    public CustomerAuthenticationFunction(AuthenticateCustomerHandler handler)
        : this(new Lazy<AuthenticateCustomerHandler>(() => handler ?? throw new ArgumentNullException(nameof(handler))))
    {
    }

    private CustomerAuthenticationFunction(Lazy<AuthenticateCustomerHandler> handler)
    {
        _handler = handler;
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext context)
    {
        _ = context;

        var path = request?.RawPath ?? request?.RequestContext?.Http?.Path;
        if (!string.Equals(path, AuthenticationPath, StringComparison.Ordinal))
        {
            return Problem(HttpStatusCode.NotFound, "not_found");
        }

        var method = request?.RequestContext?.Http?.Method;
        if (!string.Equals(method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase))
        {
            var response = Problem(HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            response.Headers["Allow"] = HttpMethod.Post.Method;
            return response;
        }

        try
        {
            var body = DecodeBody(request!);
            var payload = JsonSerializer.Deserialize<AuthenticationRequestPayload>(body, SerializerOptions);
            if (payload?.Cpf is null || payload.Password is null)
            {
                return Problem(HttpStatusCode.BadRequest, "validation_error");
            }

            var result = await _handler.Value
                .HandleAsync(
                    new AuthenticateCustomerRequest(payload.Cpf, payload.Password),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (result is null)
            {
                return Problem(HttpStatusCode.Unauthorized, "invalid_credentials");
            }

            return Json(
                HttpStatusCode.OK,
                new
                {
                    token = result.Token,
                    mustChangePassword = result.MustChangePassword,
                    tokenType = "Bearer",
                    expiresIn = 900,
                });
        }
        catch (PayloadTooLargeException)
        {
            return Problem(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        }
        catch (Exception exception) when (exception is JsonException or FormatException or DecoderFallbackException or AuthenticationValidationException)
        {
            return Problem(HttpStatusCode.BadRequest, "validation_error");
        }
        catch
        {
            return Problem(HttpStatusCode.ServiceUnavailable, "authentication_unavailable");
        }
    }

    private static string DecodeBody(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (request.Body is null)
        {
            throw new JsonException();
        }

        byte[] bytes;
        if (request.IsBase64Encoded)
        {
            bytes = Convert.FromBase64String(request.Body);
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(request.Body);
        }

        if (bytes.Length > MaximumRequestBodyBytes)
        {
            throw new PayloadTooLargeException();
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static APIGatewayHttpApiV2ProxyResponse Problem(HttpStatusCode statusCode, string detail) =>
        Json(statusCode, new { detail });

    private static APIGatewayHttpApiV2ProxyResponse Json(HttpStatusCode statusCode, object payload) => new()
    {
        StatusCode = (int)statusCode,
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Cache-Control"] = "no-store",
        },
        Body = JsonSerializer.Serialize(payload, SerializerOptions),
    };

    private sealed record AuthenticationRequestPayload(string? Cpf, string? Password);

    private sealed class PayloadTooLargeException : Exception;
}
