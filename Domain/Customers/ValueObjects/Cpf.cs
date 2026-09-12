namespace GarageFlow.Serverless.Domain.Customers.ValueObjects;

public sealed record Cpf
{
    private Cpf(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(string? input, out Cpf? cpf)
    {
        cpf = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        string digits;

        if (trimmed.Length == 11 && trimmed.All(IsAsciiDigit))
        {
            digits = trimmed;
        }
        else if (trimmed.Length == 14 &&
                 trimmed[3] == '.' &&
                 trimmed[7] == '.' &&
                 trimmed[11] == '-' &&
                 trimmed.Where(IsAsciiDigit).Count() == 11)
        {
            digits = string.Concat(trimmed.Where(IsAsciiDigit));
        }
        else
        {
            return false;
        }

        if (digits.All(character => character == digits[0]) || !HasValidCheckDigits(digits))
        {
            return false;
        }

        cpf = new Cpf(digits);
        return true;
    }

    public override string ToString() => Value;

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

    private static bool HasValidCheckDigits(string digits)
    {
        var firstCheckDigit = CalculateCheckDigit(digits.AsSpan(0, 9), 10);
        if (digits[9] - '0' != firstCheckDigit)
        {
            return false;
        }

        var secondCheckDigit = CalculateCheckDigit(digits.AsSpan(0, 10), 11);
        return digits[10] - '0' == secondCheckDigit;
    }

    private static int CalculateCheckDigit(ReadOnlySpan<char> digits, int initialWeight)
    {
        var sum = 0;
        for (var index = 0; index < digits.Length; index++)
        {
            sum += (digits[index] - '0') * (initialWeight - index);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
