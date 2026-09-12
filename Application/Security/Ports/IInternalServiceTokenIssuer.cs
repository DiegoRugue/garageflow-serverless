namespace GarageFlow.Serverless.Application.Security.Ports;

public interface IInternalServiceTokenIssuer
{
    Task<string> IssueAsync(CancellationToken cancellationToken);
}
