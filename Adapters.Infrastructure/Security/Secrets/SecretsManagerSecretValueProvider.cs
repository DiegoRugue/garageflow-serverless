using System.Collections.Concurrent;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Security.Ports;

namespace GarageFlow.Serverless.Adapters.Infrastructure.Security.Secrets;

public sealed class SecretsManagerSecretValueProvider : ISecretValueProvider
{
    public static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromSeconds(60);

    private readonly IAmazonSecretsManager _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheLifetime;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public SecretsManagerSecretValueProvider(
        IAmazonSecretsManager client,
        TimeProvider timeProvider,
        TimeSpan? cacheLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var effectiveLifetime = cacheLifetime ?? DefaultCacheLifetime;
        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > DefaultCacheLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        }

        _client = client;
        _timeProvider = timeProvider;
        _cacheLifetime = effectiveLifetime;
    }

    public async Task<string> GetAsync(string arn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arn))
        {
            throw new AuthenticationUnavailableException();
        }

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(arn, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var gate = _locks.GetOrAdd(arn, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(arn, out cached) && cached.ExpiresAt > now)
            {
                return cached.Value;
            }

            try
            {
                var response = await _client
                    .GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn }, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrEmpty(response.SecretString))
                {
                    throw new AuthenticationUnavailableException();
                }

                var entry = new CacheEntry(response.SecretString, now.Add(_cacheLifetime));
                _cache[arn] = entry;
                return entry.Value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
        finally
        {
            gate.Release();
        }
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt);
}
