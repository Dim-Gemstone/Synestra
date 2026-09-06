using System.Globalization;
using System.Text;
using System.Text.Json;
using Synestra.Domain.Workers;

namespace Synestra.Application.Workers;

public static class CompletionValidation
{
    public const int MaximumResultBytes = 64 * 1024;
    public const int MaximumResultDepth = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsValid(CompletionReport? report)
    {
        if (report is null) return false;
        try
        {
            WorkerRegistration.ValidateIdentity(report.ReportId, nameof(report.ReportId));
            return report.Outcome switch
            {
                "succeeded" => report.Error is null && ValidResult(report.Result),
                "failed" => report.Result is null && report.Error is { } error
                    && ValidText(error.Code, 100) && ValidText(error.Message, 2000),
                _ => false
            };
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ValidText(string? value, int length) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= length && !value.Contains('\0') && StrictUtf8.GetByteCount(value) > 0;

    private static bool ValidResult(string? result)
    {
        if (result is null || StrictUtf8.GetByteCount(result) > MaximumResultBytes) return false;
        using var document = JsonDocument.Parse(result, new JsonDocumentOptions { MaxDepth = MaximumResultDepth });
        return document.RootElement.ValueKind == JsonValueKind.Object && ValidValue(document.RootElement);
    }

    private static bool ValidValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    if (name.Contains('\0') || !names.Add(name) || !ValidValue(property.Value)) return false;
                    StrictUtf8.GetByteCount(name);
                }
                return true;
            case JsonValueKind.Array:
                return element.EnumerateArray().All(ValidValue);
            case JsonValueKind.String:
                var text = element.GetString()!;
                StrictUtf8.GetByteCount(text);
                return !text.Contains('\0');
            case JsonValueKind.Number:
                return FitsJsonbNumber(element.GetRawText());
            default:
                return true;
        }
    }

    private static bool FitsJsonbNumber(string text)
    {
        var number = text.AsSpan().TrimStart('-');
        var exponentIndex = number.IndexOfAny('e', 'E');
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(number[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out exponent)) return false;
            number = number[..exponentIndex];
        }

        var dot = number.IndexOf('.');
        var integerDigits = dot < 0 ? number.Length : dot;
        var fractionalDigits = dot < 0 ? 0 : number.Length - dot - 1;
        var leadingZeros = 0;
        foreach (var digit in number)
        {
            if (digit == '.') continue;
            if (digit != '0') break;
            leadingZeros++;
        }

        var significantIntegerDigits = integerDigits + (long)exponent - leadingZeros;
        return significantIntegerDigits <= 131072 && fractionalDigits - (long)exponent <= 16383;
    }
}
