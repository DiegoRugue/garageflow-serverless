namespace GarageFlow.Serverless.Application.Security.Ports;

public interface ISecretValueProvider
{
    Task<string> GetAsync(string arn, CancellationToken cancellationToken);
}
