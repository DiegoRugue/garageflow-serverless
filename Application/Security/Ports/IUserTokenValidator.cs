namespace GarageFlow.Serverless.Application.Security.Ports;

public interface IUserTokenValidator
{
    Task<bool> ValidateAsync(string token, CancellationToken cancellationToken);
}
