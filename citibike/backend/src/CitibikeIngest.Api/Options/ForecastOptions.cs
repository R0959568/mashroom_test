namespace CitibikeIngest.Api.Options;

/// <summary>
/// Settings of the forecast feature. Bound from the "Forecast" section; in the container every value is a
/// Forecast__Xxx environment variable, Url and ApiKey from FORECAST_API_URL / FORECAST_API_KEY in the compose .env
/// (what notebook 11 wrote). Without Url the feature answers 503; everything else keeps working.
/// </summary>
public sealed class ForecastOptions
{
    public const string Section = "Forecast";

    /// <summary>The HTTP API in front of the SageMaker endpoints (API Gateway), e.g. https://xxx.execute-api.us-east-1.amazonaws.com.</summary>
    public string Url { get; set; } = "";

    /// <summary>The shared key the Lambda checks (header x-api-key). Never logged.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>A cold serverless endpoint answers in about 15 s; the second try after a 502/504 covers it.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>NOAA station whose daily weather feeds the day model (New York Central Park, as in 05).</summary>
    public string WeatherStation { get; set; } = "USW00094728";

    /// <summary>The cold-start rule of 05: no forecast before a station has this many days in its active period.</summary>
    public int MinHistoryDays { get; set; } = 28;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}
