namespace CitibikeIngest.Api.Models;

/// <summary>Who started a run: shared by the trip ingest and the weather load.</summary>
public static class RunTrigger
{
    public const string Manual = "manual";
    public const string Cron = "cron";

    private static readonly string[] All = [Manual, Cron];

    /// <summary>Empty means manual; otherwise trimmed, lower-cased and checked against the known triggers.</summary>
    public static string Normalize(string? trigger)
    {
        var value = string.IsNullOrWhiteSpace(trigger) ? Manual : trigger.Trim().ToLowerInvariant();
        return All.Contains(value)
            ? value
            : throw new ValidationException($"trigger must be one of {string.Join(", ", All)}");
    }
}
