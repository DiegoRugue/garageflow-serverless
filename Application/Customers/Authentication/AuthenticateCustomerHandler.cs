using GarageFlow.Serverless.Application.Customers.Ports;
using GarageFlow.Serverless.Domain.Customers.ValueObjects;

namespace GarageFlow.Serverless.Application.Customers.Authentication;

public sealed class AuthenticateCustomerHandler
{
    public const int MaximumPasswordLength = 1024;

    private readonly ICustomerCredentialsVerifier _credentialsVerifier;
    private readonly IUserTokenIssuer _tokenIssuer;

    public AuthenticateCustomerHandler(
        ICustomerCredentialsVerifier credentialsVerifier,
        IUserTokenIssuer tokenIssuer)
    {
        ArgumentNullException.ThrowIfNull(credentialsVerifier);
        ArgumentNullException.ThrowIfNull(tokenIssuer);

        _credentialsVerifier = credentialsVerifier;
        _tokenIssuer = tokenIssuer;
    }

    public async Task<CustomerAuthenticationResult?> HandleAsync(
        AuthenticateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            !Cpf.TryCreate(request.Cpf, out var cpf) ||
            cpf is null ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Password.Length > MaximumPasswordLength)
        {
            throw new AuthenticationValidationException();
        }

        var customer = await _credentialsVerifier
            .VerifyAsync(cpf.Value, request.Password, cancellationToken)
            .ConfigureAwait(false);

        if (customer is null)
        {
            return null;
        }

        var token = await _tokenIssuer.IssueAsync(customer, cancellationToken).ConfigureAwait(false);
        return new CustomerAuthenticationResult(token, customer.MustChangePassword);
    }
}
