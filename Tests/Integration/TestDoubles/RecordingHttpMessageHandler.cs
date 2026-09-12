using System.Net;

namespace GarageFlow.Serverless.Tests.Integration.TestDoubles;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

    public RecordingHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public int CallCount { get; private set; }

    public Uri? RequestUri { get; private set; }

    public HttpMethod? Method { get; private set; }

    public string? AuthorizationScheme { get; private set; }

    public string? AuthorizationParameter { get; private set; }

    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        RequestUri = request.RequestUri;
        Method = request.Method;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        RequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return await _responseFactory(request, cancellationToken);
    }

    public static RecordingHttpMessageHandler Returning(HttpStatusCode statusCode, string? body = null) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = body is null ? null : new StringContent(body),
        }));
}
