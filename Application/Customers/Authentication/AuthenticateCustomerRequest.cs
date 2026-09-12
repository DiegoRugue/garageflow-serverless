namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed record AuthenticateCustomerRequest(string Cpf, string Password);
