namespace Synestra.Application.Workers;

public sealed class ClaimWorkOptions
{
    public int LeaseDurationSeconds
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 30;
}
