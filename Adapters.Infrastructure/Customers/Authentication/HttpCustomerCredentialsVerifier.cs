using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Ports;
using GarageFlow.Serverless.Application.Security.Ports;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Customers.Authentication;

public sealed class HttpCustomerCredentialsVerifier : ICustomerCredentialsVerifier
{
    public const int MaximumResponseBodyBytes = 8192;
    public const string RelativeEndpoint = "internal/auth/customer-credentials/verify";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly InternalApiSettings _settings;
    private readonly IInternalServiceTokenIssuer _serviceTokenIssuer;

    public HttpCustomerCredentialsVerifier(
        HttpClient httpClient,
        InternalApiSettings settings,
        IInternalServiceTokenIssuer serviceTokenIssuer)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serviceTokenIssuer);

        _httpClient = httpClient;
        _settings = settings;
        _serviceTokenIssuer = serviceTokenIssuer;
    }

    public async Task<VerifiedCustomer?> VerifyAsync(
        string normalizedCpf,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            var serviceToken = await _serviceTokenIssuer.IssueAsync(cancellationToken).ConfigureAwait(false);
            using var exchangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchangeCancellation.CancelAfter(_httpClient.Timeout);
            var exchangeToken = exchangeCancellation.Token;
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_settings.BaseUri, RelativeEndpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new CredentialsRequest(normalizedCpf, password), SerializerOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, exchangeToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return null;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new AuthenticationUnavailableException();
            }

            var body = await ReadBodyAsync(response.Content, exchangeToken).ConfigureAwait(false);
            var identity = JsonSerializer.Deserialize<CredentialsResponse>(body, SerializerOptions);
            if (identity is null ||
                identity.UserId is null || identity.UserId == Guid.Empty ||
                identity.CustomerId is null || identity.CustomerId == Guid.Empty ||
                !string.Equals(identity.Role, "Customer", StringComparison.Ordinal) ||
                identity.MustChangePassword is null)
            {
                throw new AuthenticationUnavailableException();
            }

            return new VerifiedCustomer(
                identity.UserId.Value,
                identity.CustomerId.Value,
                identity.Role!,
                identity.MustChangePassword.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AuthenticationUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AuthenticationUnavailableException(exception);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            throw new AuthenticationUnavailableException();
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBodyBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            total += bytesRead;
        }

        if (total > MaximumResponseBodyBytes)
        {
            throw new AuthenticationUnavailableException();
        }

        return new UTF8Encoding(false, true).GetString(buffer, 0, total);
    }

    private sealed record CredentialsRequest(string Cpf, string Password);

    private sealed record CredentialsResponse(
        Guid? UserId,
        Guid? CustomerId,
        string? Role,
        bool? MustChangePassword);
}
