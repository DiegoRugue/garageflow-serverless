using GarageFlow.Serverless.Domain.Customers.ValueObjects;

namespace GarageFlow.Serverless.Tests.Unit.Customers.ValueObjects;

public sealed class CpfTests
{
    [Theory]
    [InlineData("52998224725", "52998224725")]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData("  529.982.247-25  ", "52998224725")]
    public void TryCreateWithValidCpfNormalizesToElevenDigits(string input, string expected)
    {
        var success = Cpf.TryCreate(input, out var cpf);

        Assert.True(success);
        Assert.NotNull(cpf);
        Assert.Equal(expected, cpf.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("11111111111")]
    [InlineData("52998224724")]
    [InlineData("52998224725000")]
    [InlineData("529.982.247/25")]
    [InlineData("5299822472A")]
    [InlineData("529 982 247 25")]
    public void TryCreateWithInvalidCpfReturnsFalse(string? input)
    {
        var success = Cpf.TryCreate(input, out var cpf);

        Assert.False(success);
        Assert.Null(cpf);
    }
}
