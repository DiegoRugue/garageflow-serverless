using GarageFlow.Serverless.Application.Customers.Authentication;
using GarageFlow.Serverless.Application.Customers.Ports;
using Moq;

namespace GarageFlow.Serverless.Tests.Unit.Customers.Authentication;

public sealed class AuthenticateCustomerHandlerTests
{
    [Fact]
    public async Task HandleWithInvalidCpfRejectsBeforeCallingDependencies()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var handler = new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object);

        await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            handler.HandleAsync(new AuthenticateCustomerRequest("11111111111", "valid-password"), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleWithBlankPasswordRejectsBeforeCallingDependencies(string password)
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var handler = new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object);

        await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            handler.HandleAsync(new AuthenticateCustomerRequest("52998224725", password), CancellationToken.None));
    }

    [Fact]
    public async Task HandleWithPasswordOverLimitRejectsBeforeCallingDependencies()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var handler = new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object);

        await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            handler.HandleAsync(new AuthenticateCustomerRequest("52998224725", new string('a', 1025)), CancellationToken.None));
    }

    [Fact]
    public async Task HandleWithValidCredentialsNormalizesCpfAndPreservesTemporaryPasswordFlag()
    {
        var cancellationToken = new CancellationTokenSource().Token;
        var customer = new VerifiedCustomer(
            Guid.Parse("2ab02472-9f29-4729-92ad-1f03ba994a84"),
            Guid.Parse("6334f73c-b315-4ab0-bb49-65e141a8927f"),
            "Customer",
            true);
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync("52998224725", "password", cancellationToken))
            .ReturnsAsync(customer);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        tokenIssuer
            .Setup(candidate => candidate.IssueAsync(customer, cancellationToken))
            .ReturnsAsync("issued-token");
        var handler = new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object);

        var result = await handler.HandleAsync(
            new AuthenticateCustomerRequest(" 529.982.247-25 ", "password"),
            cancellationToken);

        Assert.NotNull(result);
        Assert.Equal("issued-token", result.Token);
        Assert.True(result.MustChangePassword);
    }

    [Fact]
    public async Task HandleWhenApiReturnsNoCustomerDoesNotIssueToken()
    {
        var verifier = new Mock<ICustomerCredentialsVerifier>(MockBehavior.Strict);
        verifier
            .Setup(candidate => candidate.VerifyAsync("52998224725", "wrong-password", CancellationToken.None))
            .ReturnsAsync((VerifiedCustomer?)null);
        var tokenIssuer = new Mock<IUserTokenIssuer>(MockBehavior.Strict);
        var handler = new AuthenticateCustomerHandler(verifier.Object, tokenIssuer.Object);

        var result = await handler.HandleAsync(
            new AuthenticateCustomerRequest("52998224725", "wrong-password"),
            CancellationToken.None);

        Assert.Null(result);
    }
}
