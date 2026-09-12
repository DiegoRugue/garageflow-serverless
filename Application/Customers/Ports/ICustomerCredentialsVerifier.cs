using GarageFlow.Serverless.Application.Customers.Authentication;

namespace GarageFlow.Serverless.Application.Customers.Ports;

public interface ICustomerCredentialsVerifier
{
    Task<VerifiedCustomer?> VerifyAsync(
        string normalizedCpf,
        string password,
        CancellationToken cancellationToken);
}
