namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed record VerifiedCustomer(
    Guid UserId,
    Guid CustomerId,
    string Role,
    bool MustChangePassword);
