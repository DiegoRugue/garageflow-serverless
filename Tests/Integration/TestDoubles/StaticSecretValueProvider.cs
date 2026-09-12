using GarageFlow.Serverless.Application.Security.Ports;

namespace GarageFlow.Serverless.Tests.Integration.TestDoubles;

internal sealed class StaticSecretValueProvider : ISecretValueProvider
{
    private readonly IReadOnlyDictionary<string, string> _secrets;

    public StaticSecretValueProvider(IReadOnlyDictionary<string, string> secrets)
    {
        _secrets = secrets;
    }

    public Task<string> GetAsync(string arn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_secrets[arn]);
    }
}
