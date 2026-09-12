using GarageFlow.Serverless.Application.Security.Ports;

namespace GarageFlow.Serverless.Tests.Integration.TestDoubles;

internal sealed class DelayedSecretValueProvider : ISecretValueProvider
{
    private readonly IReadOnlyDictionary<string, string> _secrets;
    private readonly TimeSpan _delay;
    private int _callCount;

    public DelayedSecretValueProvider(IReadOnlyDictionary<string, string> secrets, TimeSpan delay)
    {
        _secrets = secrets;
        _delay = delay;
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public async Task<string> GetAsync(string arn, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        await Task.Delay(_delay, cancellationToken);
        return _secrets[arn];
    }
}
