namespace CitibikeIngest.Api.Models;

/// <summary>Where the start date of a weather load came from.</summary>
public enum WeatherRangeSource
{
    /// <summary>Passed explicitly by the caller.</summary>
    Parameter,

    /// <summary>The last stored day minus Weather:RefetchDays, so late NOAA corrections are picked up.</summary>
    LastDay,

    /// <summary>Weather:DefaultFrom: nothing stored yet (first load).</summary>
    Default,
}

public sealed record WeatherRange(DateOnly From, DateOnly To, WeatherRangeSource Source);

/// <summary>One weather load: row in citibike_meta.weather_run.</summary>
public sealed class WeatherRun
{
    public long Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public string Station { get; set; } = "";

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public WeatherRangeSource RangeSource { get; set; }

    /// <summary>"manual" or "cron", see <see cref="RunTrigger"/>.</summary>
    public string Trigger { get; set; } = RunTrigger.Manual;

    /// <summary>Days NOAA returned; can be fewer than the range when the latest days are not published yet.</summary>
    public int DaysReceived { get; set; }

    /// <summary>Days inserted or updated in citibike.weather_daily.</summary>
    public int DaysWritten { get; set; }

    public string? Error { get; set; }
}
