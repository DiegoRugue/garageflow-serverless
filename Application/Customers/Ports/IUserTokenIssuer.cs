using GarageFlow.Serverless.Application.Customers.Authentication;

namespace GarageFlow.Serverless.Application.Customers.Ports;

public interface IUserTokenIssuer
{
    Task<string> IssueAsync(VerifiedCustomer customer, CancellationToken cancellationToken);
}
