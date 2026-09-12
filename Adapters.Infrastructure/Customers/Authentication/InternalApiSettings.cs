using GarageFlow.Serverless.Application.Customers.Authentication;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Customers.Authentication;

public sealed record InternalApiSettings
{
    private InternalApiSettings(Uri baseUri)
    {
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public static InternalApiSettings Create(string baseUrl, string transport)
    {
        if ((transport != Uri.UriSchemeHttp && transport != Uri.UriSchemeHttps) ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, transport, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new AuthenticationUnavailableException();
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.EndsWith('/')
                ? baseUri.AbsolutePath
                : $"{baseUri.AbsolutePath}/",
        };

        return new InternalApiSettings(builder.Uri);
    }
}
