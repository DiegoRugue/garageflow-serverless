namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed class AuthenticationUnavailableException : Exception
{
    public AuthenticationUnavailableException()
        : base("Customer authentication is unavailable.")
    {
    }

    public AuthenticationUnavailableException(Exception innerException)
        : base("Customer authentication is unavailable.", innerException)
    {
    }
}
