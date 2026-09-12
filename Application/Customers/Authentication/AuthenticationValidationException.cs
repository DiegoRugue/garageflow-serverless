namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed class AuthenticationValidationException : Exception
{
    public AuthenticationValidationException()
        : base("The authentication request is invalid.")
    {
    }
}
