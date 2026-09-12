namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed record CustomerAuthenticationResult(string Token, bool MustChangePassword);
