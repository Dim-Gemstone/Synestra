using System.Text.Json;

namespace Synestra.Worker;

internal sealed class BoundedSumWorkload(TimeProvider timeProvider)
{
    public async Task<WorkloadOutcome> ExecuteAsync(JsonElement payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (payload.ValueKind != JsonValueKind.Object) return WorkloadOutcome.InvalidInput();
        JsonElement values = default, duration = default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return WorkloadOutcome.InvalidInput();
            switch (property.Name)
            {
                case "values": values = property.Value; break;
                case "durationMs": duration = property.Value; break;
                default: return WorkloadOutcome.InvalidInput();
            }
        }
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 1024
            || !TryInteger(duration, 0, 60000, out var durationMs)) return WorkloadOutcome.InvalidInput();
        long sum = 0;
        foreach (var value in values.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (!TryInteger(value, -1000000, 1000000, out var number)) return WorkloadOutcome.InvalidInput();
            sum += number;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(durationMs), timeProvider, token);
        token.ThrowIfCancellationRequested();
        return new(JsonSerializer.SerializeToElement(new { count = values.GetArrayLength(), sum }), null);
    }

    private static bool TryInteger(JsonElement element, int minimum, int maximum, out int number)
    {
        number = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value)
            || value < minimum || value > maximum || decimal.Truncate(value) != value) return false;
        number = (int)value;
        // Reject decimal rounding/underflow while accepting equivalent integer spellings from jsonb.
        return JsonElement.DeepEquals(element, JsonSerializer.SerializeToElement(number));
    }
}
