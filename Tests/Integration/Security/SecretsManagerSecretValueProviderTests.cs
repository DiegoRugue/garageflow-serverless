using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using GarageFlow.Serverless.Adapters.Infrastructure.Security.Secrets;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Tests.Integration.TestDoubles;
using Moq;

namespace GarageFlow.Serverless.Tests.Integration.Security;

public sealed class SecretsManagerSecretValueProviderTests
{
    [Fact]
    public async Task GetWithinCacheLifetimeCallsSecretsManagerOnce()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var client = new Mock<IAmazonSecretsManager>(MockBehavior.Strict);
        client
            .Setup(candidate => candidate.GetSecretValueAsync(
                It.Is<GetSecretValueRequest>(request => request.SecretId == "arn:user"),
                CancellationToken.None))
            .ReturnsAsync(new GetSecretValueResponse { SecretString = new string('u', 64) });
        var provider = new SecretsManagerSecretValueProvider(client.Object, time, TimeSpan.FromSeconds(60));

        var first = await provider.GetAsync("arn:user", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        var second = await provider.GetAsync("arn:user", CancellationToken.None);

        Assert.Equal(new string('u', 64), first);
        Assert.Equal(first, second);
        client.VerifyAll();
    }

    [Fact]
    public async Task GetAfterExpiryDoesNotReturnStaleValueWhenRefreshFails()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        var client = new Mock<IAmazonSecretsManager>(MockBehavior.Strict);
        client
            .SetupSequence(candidate => candidate.GetSecretValueAsync(
                It.Is<GetSecretValueRequest>(request => request.SecretId == "arn:user"),
                CancellationToken.None))
            .ReturnsAsync(new GetSecretValueResponse { SecretString = new string('u', 64) })
            .ThrowsAsync(new AmazonSecretsManagerException("dependency unavailable"));
        var provider = new SecretsManagerSecretValueProvider(client.Object, time, TimeSpan.FromSeconds(60));

        await provider.GetAsync("arn:user", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            provider.GetAsync("arn:user", CancellationToken.None));
    }

    [Fact]
    public async Task GetWithMissingSecretStringFailsClosed()
    {
        var client = new Mock<IAmazonSecretsManager>(MockBehavior.Strict);
        client
            .Setup(candidate => candidate.GetSecretValueAsync(
                It.IsAny<GetSecretValueRequest>(),
                CancellationToken.None))
            .ReturnsAsync(new GetSecretValueResponse { SecretString = null });
        var provider = new SecretsManagerSecretValueProvider(client.Object, TimeProvider.System);

        await Assert.ThrowsAsync<AuthenticationUnavailableException>(() =>
            provider.GetAsync("arn:user", CancellationToken.None));
    }

    [Fact]
    public void ConstructorRejectsCacheLifetimeOverSixtySeconds()
    {
        var client = new Mock<IAmazonSecretsManager>(MockBehavior.Strict);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SecretsManagerSecretValueProvider(client.Object, TimeProvider.System, TimeSpan.FromSeconds(61)));
    }
}
