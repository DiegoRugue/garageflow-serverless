using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GarageFlow.Serverless.Adapters.Infrastructure.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Security.Ports;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;
using Moq;

namespace GarageFlow.Serverless.Tests.Integration.Customers;

public sealed class HttpCustomerCredentialsVerifierTests
{
    [Fact]
    public async Task VerifyPostsNormalizedCredentialsWithServiceBearerToken()
    {
        const string responseBody = """
            {"userId":"2ab02472-9f29-4729-92ad-1f03ba994a84","customerId":"6334f73c-b315-4ab0-bb49-65e141a8927f","role":"Customer","mustChangePassword":true}
            """;
        var messageHandler = RecordingHttpMessageHandler.Returning(HttpStatusCode.OK, responseBody);
        using var client = new HttpClient(messageHandler) { Timeout = TimeSpan.FromSeconds(5) };
        var serviceTokenIssuer = new Mock<IInternalServiceTokenIssuer>(MockBehavior.Strict);
        serviceTokenIssuer
            .Setup(candidate => candidate.IssueAsync(CancellationToken.None))
            .ReturnsAsync("service-token");
        var verifier = new HttpCustomerCredentialsVerifier(
            client,
            InternalApiSettings.Create("https://internal.example/base", "https"),
            serviceTokenIssuer.Object);

        var customer = await verifier.VerifyAsync("52998224725", "password", CancellationToken.None);

        Assert.NotNull(customer);
        Assert.Equal(Guid.Parse("2ab02472-9f29-4729-92ad-1f03ba994a84"), customer.UserId);
        Assert.Equal(Guid.Parse("6334f73c-b315-4ab0-bb49-65e141a8927f"), customer.CustomerId);
        Assert.Equal("Customer", customer.Role);
        Assert.True(customer.MustChangePassword);
        Assert.Equal(HttpMethod.Post, messageHandler.Method);
        Assert.Equal(new Uri("https://internal.example/base/internal/auth/customer-credentials/verify"), messageHandler.RequestUri);
        Assert.Equal("Bearer", messageHandler.AuthorizationScheme);
        Assert.Equal("service-token", messageHandler.AuthorizationParameter);
        using var requestJson = JsonDocument.Parse(Assert.IsType<string>(messageHandler.RequestBody));
        Assert.Equal("52998224725", requestJson.RootElement.GetProperty("cpf").GetString());
        Assert.Equal("password", requestJson.RootElement.GetProperty("password").GetString());
        Assert.Equal(2, requestJson.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task VerifyReturnsNullOnlyForUnauthorized()
    {
        var messageHandler = RecordingHttpMessageHandler.Returning(HttpStatusCode.Unauthorized);
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        var customer = await verifier.VerifyAsync("52998224725", "wrong", CancellationToken.None);

        Assert.Null(customer);
        Assert.Equal(1, messageHandler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task VerifyMapsEveryOtherStatusToUnavailableWithoutRetry(HttpStatusCode statusCode)
    {
        var messageHandler = RecordingHttpMessageHandler.Returning(statusCode, "sensitive dependency response");
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            verifier.VerifyAsync("52998224725", "password", CancellationToken.None));

        Assert.Equal("Customer authentication is unavailable.", exception.Message);
        Assert.Equal(1, messageHandler.CallCount);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"userId\":\"00000000-0000-0000-0000-000000000000\",\"customerId\":\"6334f73c-b315-4ab0-bb49-65e141a8927f\",\"role\":\"Customer\",\"mustChangePassword\":false}")]
    [InlineData("{\"userId\":\"2ab02472-9f29-4729-92ad-1f03ba994a84\",\"customerId\":\"6334f73c-b315-4ab0-bb49-65e141a8927f\",\"role\":\"customer\",\"mustChangePassword\":false}")]
    [InlineData("{\"userId\":\"2ab02472-9f29-4729-92ad-1f03ba994a84\",\"customerId\":\"6334f73c-b315-4ab0-bb49-65e141a8927f\",\"role\":\"Customer\"}")]
    public async Task VerifyRejectsMalformedOrUnexpectedIdentity(string responseBody)
    {
        var messageHandler = RecordingHttpMessageHandler.Returning(HttpStatusCode.OK, responseBody);
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            verifier.VerifyAsync("52998224725", "password", CancellationToken.None));
    }

    [Fact]
    public async Task VerifyRejectsDependencyResponseOverEightKiB()
    {
        var messageHandler = RecordingHttpMessageHandler.Returning(HttpStatusCode.OK, new string('x', 8193));
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            verifier.VerifyAsync("52998224725", "password", CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyMapsTimeoutAndTransportErrorsToUnavailable(bool timeout)
    {
        var messageHandler = new RecordingHttpMessageHandler((_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("transport")));
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            verifier.VerifyAsync("52998224725", "password", CancellationToken.None));
    }

    [Fact]
    public async Task VerifyPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var messageHandler = new RecordingHttpMessageHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token));
        using var client = new HttpClient(messageHandler);
        var verifier = CreateVerifier(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync("52998224725", "password", cancellation.Token));
    }

    [Fact]
    public async Task VerifyTimesOutWhileReadingAStalledPartialResponseBody()
    {
        const string responseBody = """
            {"userId":"2ab02472-9f29-4729-92ad-1f03ba994a84","customerId":"6334f73c-b315-4ab0-bb49-65e141a8927f","role":"Customer","mustChangePassword":true}
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PartialThenDelayedStream(
                Encoding.UTF8.GetBytes(responseBody),
                firstChunkLength: 24,
                TimeSpan.FromMilliseconds(400))),
        };
        var messageHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(messageHandler) { Timeout = TimeSpan.FromMilliseconds(75) };
        var verifier = CreateVerifier(client);
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            verifier.VerifyAsync("52998224725", "password", CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(300), $"Elapsed: {elapsed.Elapsed}");
    }

    [Fact]
    public async Task VerifyPropagatesCallerCancellationWhileReadingAStalledPartialResponseBody()
    {
        const string responseBody = """
            {"userId":"2ab02472-9f29-4729-92ad-1f03ba994a84","customerId":"6334f73c-b315-4ab0-bb49-65e141a8927f","role":"Customer","mustChangePassword":true}
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PartialThenDelayedStream(
                Encoding.UTF8.GetBytes(responseBody),
                firstChunkLength: 24,
                TimeSpan.FromSeconds(1))),
        };
        var messageHandler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(response));
        using var client = new HttpClient(messageHandler) { Timeout = TimeSpan.FromSeconds(5) };
        var verifier = CreateVerifier(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync("52998224725", "password", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData("https://internal.example", "http")]
    [InlineData("http://internal.example", "https")]
    [InlineData("ftp://internal.example", "ftp")]
    [InlineData("/relative", "https")]
    public void SettingsRejectInvalidOrMismatchedTransport(string baseUrl, string transport)
    {
        Assert.Throws<AuthenticationUnavailableException>(() => InternalApiSettings.Create(baseUrl, transport));
    }

    private static HttpCustomerCredentialsVerifier CreateVerifier(HttpClient client)
    {
        var serviceTokenIssuer = new Mock<IInternalServiceTokenIssuer>(MockBehavior.Strict);
        serviceTokenIssuer
            .Setup(candidate => candidate.IssueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("service-token");
        return new HttpCustomerCredentialsVerifier(
            client,
            InternalApiSettings.Create("http://internal.example", "http"),
            serviceTokenIssuer.Object);
    }

    private sealed class PartialThenDelayedStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int _firstChunkLength;
        private readonly TimeSpan _delay;
        private int _position;
        private bool _delayCompleted;

        public PartialThenDelayedStream(byte[] payload, int firstChunkLength, TimeSpan delay)
        {
            _payload = payload;
            _firstChunkLength = firstChunkLength;
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _payload.Length)
            {
                return 0;
            }

            if (_position >= _firstChunkLength && !_delayCompleted)
            {
                await Task.Delay(_delay, cancellationToken);
                _delayCompleted = true;
            }

            var remainingInChunk = _position < _firstChunkLength
                ? _firstChunkLength - _position
                : _payload.Length - _position;
            var bytesToCopy = Math.Min(buffer.Length, remainingInChunk);
            _payload.AsMemory(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return bytesToCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}
