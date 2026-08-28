namespace Synestra.Domain;

internal static class DomainValidation
{
    public static string RequiredText(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return value;
    }

    public static string? OptionalText(string? value, int maxLength, string parameterName)
    {
        if (value is not null && value.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return value;
    }

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Date and time must be UTC.", parameterName);
        }

        return value;
    }
}
