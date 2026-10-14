namespace CitibikeIngest.Api.Models;

/// <summary>Where the years of a calendar load came from.</summary>
public enum CalendarRangeSource
{
    /// <summary>At least one of fromYear / toYear was passed by the caller.</summary>
    Parameter,

    /// <summary>Calendar:DefaultFromYear up to the current year plus Calendar:YearsAhead.</summary>
    Default,
}

public sealed record CalendarRange(int FromYear, int ToYear, CalendarRangeSource Source)
{
    public DateOnly FirstDay => new(FromYear, 1, 1);

    public DateOnly LastDay => new(ToYear, 12, 31);
}

/// <summary>One calendar load: row in citibike_meta.calendar_run.</summary>
public sealed class CalendarRun
{
    public long Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public string Country { get; set; } = "";

    /// <summary>Empty = country-wide only.</summary>
    public string Subdivision { get; set; } = "";

    /// <summary>Comma-separated package categories, as passed to the exporter.</summary>
    public string Categories { get; set; } = "";

    public int FromYear { get; set; }

    public int ToYear { get; set; }

    public CalendarRangeSource RangeSource { get; set; }

    /// <summary>"manual" or "cron", see <see cref="RunTrigger"/>.</summary>
    public string Trigger { get; set; } = RunTrigger.Manual;

    /// <summary>Package that produced the rows ("holidays") and its version ("0.105"). Null until the export ran.</summary>
    public string? Source { get; set; }

    public string? SourceVersion { get; set; }

    /// <summary>Rows the exporter returned.</summary>
    public int HolidaysReceived { get; set; }

    /// <summary>Rows written to citibike.holiday after the years of the load were cleared.</summary>
    public int HolidaysWritten { get; set; }

    public string? Error { get; set; }
}
