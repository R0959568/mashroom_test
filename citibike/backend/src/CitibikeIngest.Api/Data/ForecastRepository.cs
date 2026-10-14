using CitibikeIngest.Api.Models;
using Npgsql;

namespace CitibikeIngest.Api.Data;

/// <summary>Npgsql implementation. One pooled connection per operation; every value is a bound parameter.</summary>
public sealed class ForecastRepository(NpgsqlDataSource dataSource) : IForecastRepository
{
    private const string StationColumns = "station_key, name, lat, lng, trips_as_start";

    public async Task<IReadOnlyList<StationSummary>> ListStationsAsync(string query, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            SELECT {StationColumns}
              FROM citibike_prep.station_dim
             WHERE name IS NOT NULL AND (@q = '' OR name ILIKE '%' || @q || '%' OR station_key = @q)
             ORDER BY trips_as_start DESC NULLS LAST, name
             LIMIT @limit
            """);
        command.Parameters.AddWithValue("q", query);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var stations = new List<StationSummary>();
        while (await reader.ReadAsync(ct))
        {
            stations.Add(ReadStation(reader));
        }

        return stations;
    }

    public async Task<StationSummary?> GetStationAsync(string stationKey, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {StationColumns} FROM citibike_prep.station_dim WHERE station_key = @key");
        command.Parameters.AddWithValue("key", stationKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStation(reader) : null;
    }

    public async Task<DataRange> GetRangeAsync(string weatherStation, CancellationToken ct)
    {
        // day_features has one row per day of the data (05 §3): its bounds are the feature table's, without a scan of 3,9 M rows.
        await using var command = dataSource.CreateCommand(
            """
            SELECT (SELECT min(day) FROM citibike_prep.day_features),
                   (SELECT max(day) FROM citibike_prep.day_features),
                   (SELECT max(day) FROM citibike.weather_daily WHERE station = @station)
            """);
        command.Parameters.AddWithValue("station", weatherStation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new DataRange(reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateOnly>(1), DateOrNull(reader, 2));
    }

    public async Task<IReadOnlyList<MapStation>> ListMapStationsAsync(DateOnly today, int windowDays, CancellationToken ct)
    {
        // One range scan of station_day over the window (the index on day, see deploy/README.md) and 2,3 k groups.
        await using var command = dataSource.CreateCommand(
            """
            WITH w AS (
                SELECT station_key, avg(departures) AS mean_departures, count(*) AS active_days
                  FROM citibike_prep.station_day
                 WHERE day > @today - @window AND day <= @today
                 GROUP BY station_key)
            SELECT d.station_key, d.name, d.lat, d.lng, w.mean_departures, w.active_days
              FROM w JOIN citibike_prep.station_dim d USING (station_key)
             WHERE d.name IS NOT NULL AND d.lat IS NOT NULL AND d.lng IS NOT NULL
             ORDER BY w.mean_departures DESC, d.name
            """);
        command.Parameters.AddWithValue("today", today);
        command.Parameters.AddWithValue("window", windowDays);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var stations = new List<MapStation>();
        while (await reader.ReadAsync(ct))
        {
            stations.Add(new MapStation(
                reader.GetString(reader.GetOrdinal("station_key")),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetDouble(reader.GetOrdinal("lat")),
                reader.GetDouble(reader.GetOrdinal("lng")),
                reader.GetDouble(reader.GetOrdinal("mean_departures")),
                (int)reader.GetInt64(reader.GetOrdinal("active_days"))));
        }

        return stations;
    }

    public async Task<FeatureRow?> GetTableRowAsync(string stationKey, DateOnly day, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT history_days, days_in_horizon, y_day, y_rest_of_month,
                   dep_lag_1, dep_lag_7, dep_lag_14, dep_lag_28, dep_mean_7, dep_mean_4_weeks, dep_mtd, dep_sum_28,
                   member_share_28, ebike_share_28, arr_lag_1, arr_mean_7, dep_rest_of_month_ly, dep_sum_28_ly,
                   iso_dow, month, day_of_month, holiday, day_off, holiday_lag_7,
                   tmax_c, tmin_c, prcp_mm, snow_mm, snow_depth_mm, wind_ms
              FROM citibike_prep.station_day_features
             WHERE station_key = @key AND day = @day
            """);
        command.Parameters.AddWithValue("key", stationKey);
        command.Parameters.AddWithValue("day", day);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new FeatureRow
        {
            StationKey = stationKey,
            Day = day,
            Source = FeatureSource.Table,
            HistoryDays = reader.GetInt32(0),
            DaysInHorizon = reader.GetInt16(1),
            ActualDay = reader.GetInt32(2),
            ActualRestOfMonth = IntOrNull(reader, 3),
            DepLag1 = IntOrNull(reader, 4),
            DepLag7 = IntOrNull(reader, 5),
            DepLag14 = IntOrNull(reader, 6),
            DepLag28 = IntOrNull(reader, 7),
            DepMean7 = DoubleOrNull(reader, 8),
            DepMean4Weeks = DoubleOrNull(reader, 9),
            DepMtd = DoubleOrNull(reader, 10),
            DepSum28 = IntOrNull(reader, 11),
            MemberShare28 = DoubleOrNull(reader, 12),
            EbikeShare28 = DoubleOrNull(reader, 13),
            ArrLag1 = IntOrNull(reader, 14),
            ArrMean7 = DoubleOrNull(reader, 15),
            DepRestOfMonthLy = IntOrNull(reader, 16),
            DepSum28Ly = IntOrNull(reader, 17),
            IsoDow = reader.GetInt16(18),
            Month = reader.GetInt16(19),
            DayOfMonth = reader.GetInt16(20),
            Holiday = reader.GetBoolean(21),
            DayOff = reader.GetBoolean(22),
            HolidayLag7 = reader.GetBoolean(23),
            TmaxC = reader.GetDouble(24),
            TminC = reader.GetDouble(25),
            PrcpMm = reader.GetDouble(26),
            SnowMm = reader.GetDouble(27),
            SnowDepthMm = reader.GetDouble(28),
            WindMs = reader.GetDouble(29),
            WeatherDay = day,
        };
    }

    public async Task<FeatureRow?> ComputeRowAsync(string stationKey, DateOnly day, string weatherStation, CancellationToken ct)
    {
        // The window definitions of 05 §4 for one station-day: 28 preceding days of the station's last active period,
        // the month to date before the day, the same weekday of the last four weeks, the year earlier from the feature
        // table, the calendar of the day and of a week before, and the last observed value of each weather variable.
        await using var command = dataSource.CreateCommand(
            """
            WITH p AS (
                SELECT period_no, first_day, last_day
                  FROM citibike_prep.station_period
                 WHERE station_key = @key AND first_day < @day
                 ORDER BY last_day DESC LIMIT 1),
            h AS (
                SELECT s.day, s.departures, s.departures_member, s.departures_ebike, s.arrivals
                  FROM citibike_prep.station_day s JOIN p ON p.period_no = s.period_no
                 WHERE s.station_key = @key AND s.day >= @day - 28 AND s.day < @day),
            c AS (
                SELECT day, iso_dow, is_weekend, (has_public_holiday OR is_observed_day_off) AS holiday
                  FROM citibike.calendar_day WHERE day IN (@day, @day - 7)),
            w AS (
                SELECT day, tmax_c, tmin_c, prcp_mm, snow_mm, snow_depth_mm, wind_ms
                  FROM citibike.weather_daily
                 WHERE station = @station AND day <= @day AND day > @day - 90
                 ORDER BY day DESC),
            ly AS (
                SELECT y_rest_of_month, dep_sum_28
                  FROM citibike_prep.station_day_features
                 WHERE station_key = @key AND day = (@day - interval '1 year')::date)
            SELECT (SELECT last_day FROM p)                                                          AS period_last_day,
                   (@day - (SELECT first_day FROM p))                                               AS history_days,
                   (SELECT count(*) FROM h)                                                         AS rows_28,
                   (SELECT departures FROM h WHERE day = @day - 1)                                  AS dep_lag_1,
                   (SELECT departures FROM h WHERE day = @day - 7)                                  AS dep_lag_7,
                   (SELECT departures FROM h WHERE day = @day - 14)                                 AS dep_lag_14,
                   (SELECT departures FROM h WHERE day = @day - 21)                                 AS dep_lag_21,
                   (SELECT departures FROM h WHERE day = @day - 28)                                 AS dep_lag_28,
                   (SELECT avg(departures) FROM h WHERE day >= @day - 7)                            AS dep_mean_7,
                   (SELECT avg(departures) FROM h WHERE day >= date_trunc('month', @day::timestamp)::date) AS dep_mtd,
                   (SELECT sum(departures) FROM h)                                                  AS dep_sum_28,
                   (SELECT sum(departures_member) FROM h)                                           AS dep_member_28,
                   (SELECT sum(departures_ebike) FROM h)                                            AS dep_ebike_28,
                   (SELECT arrivals FROM h WHERE day = @day - 1)                                    AS arr_lag_1,
                   (SELECT avg(arrivals) FROM h WHERE day >= @day - 7)                              AS arr_mean_7,
                   (SELECT y_rest_of_month FROM ly)                                                 AS dep_rest_of_month_ly,
                   (SELECT dep_sum_28 FROM ly)                                                      AS dep_sum_28_ly,
                   (SELECT iso_dow FROM c WHERE day = @day)                                         AS iso_dow,
                   (SELECT is_weekend FROM c WHERE day = @day)                                      AS is_weekend,
                   (SELECT holiday FROM c WHERE day = @day)                                         AS holiday,
                   (SELECT holiday FROM c WHERE day = @day - 7)                                     AS holiday_lag_7,
                   (SELECT tmax_c FROM w WHERE tmax_c IS NOT NULL LIMIT 1)                          AS tmax_c,
                   (SELECT tmin_c FROM w WHERE tmin_c IS NOT NULL LIMIT 1)                          AS tmin_c,
                   (SELECT prcp_mm FROM w WHERE prcp_mm IS NOT NULL LIMIT 1)                        AS prcp_mm,
                   (SELECT snow_mm FROM w WHERE snow_mm IS NOT NULL LIMIT 1)                        AS snow_mm,
                   (SELECT snow_depth_mm FROM w WHERE snow_depth_mm IS NOT NULL LIMIT 1)            AS snow_depth_mm,
                   (SELECT wind_ms FROM w WHERE wind_ms IS NOT NULL LIMIT 1)                        AS wind_ms,
                   (SELECT max(day) FROM w)                                                         AS weather_day
            """);
        command.Parameters.AddWithValue("key", stationKey);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("station", weatherStation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        // By name, not by position: the select above is long, and a positional read is one shifted index away from
        // reading the weather as a boolean (found 2026-10-10 on the first call of the public URL).
        int Col(string name) => reader.GetOrdinal(name);
        if (!await reader.ReadAsync(ct) || DateOrNull(reader, Col("period_last_day")) is not { } periodLastDay || periodLastDay != day.AddDays(-1))
        {
            return null; // no active period ending yesterday: the station is closed or unknown
        }

        if (reader.IsDBNull(Col("iso_dow")) || reader.IsDBNull(Col("tmax_c")))
        {
            throw new UpstreamException($"The calendar or the weather does not cover {day:yyyy-MM-dd}; load them first (POST /api/calendar/runs, /api/weather/runs)");
        }

        var historyDays = reader.GetInt32(Col("history_days"));
        var rows28 = (int)reader.GetInt64(Col("rows_28"));
        var lag7 = IntOrNull(reader, Col("dep_lag_7"));
        var lag14 = IntOrNull(reader, Col("dep_lag_14"));
        var lag21 = IntOrNull(reader, Col("dep_lag_21"));
        var lag28 = IntOrNull(reader, Col("dep_lag_28"));
        var sum28 = LongOrNull(reader, Col("dep_sum_28"));
        var member28 = LongOrNull(reader, Col("dep_member_28"));
        var ebike28 = LongOrNull(reader, Col("dep_ebike_28"));
        var enough = historyDays >= 28 && rows28 == 28; // the window of 05 holds 28 rows only when the period is that old
        var holiday = reader.GetBoolean(Col("holiday"));
        var isWeekend = reader.GetBoolean(Col("is_weekend"));
        var monthEnd = new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
        return new FeatureRow
        {
            StationKey = stationKey,
            Day = day,
            Source = FeatureSource.Computed,
            HistoryDays = historyDays,
            DaysInHorizon = monthEnd.DayNumber - day.DayNumber + 1,
            DepLag1 = IntOrNull(reader, Col("dep_lag_1")),
            DepLag7 = lag7,
            DepLag14 = lag14,
            DepLag28 = lag28,
            DepMean7 = historyDays >= 7 ? DoubleOrNull(reader, Col("dep_mean_7")) : null,
            DepMean4Weeks = enough && lag7 is { } a && lag14 is { } b && lag21 is { } c2 && lag28 is { } d ? (a + b + c2 + d) / 4.0 : null,
            DepMtd = DoubleOrNull(reader, Col("dep_mtd")),
            DepSum28 = enough ? (int?)sum28 : null,
            MemberShare28 = enough && sum28 > 0 ? (double?)member28 / sum28 : null,
            EbikeShare28 = enough && sum28 > 0 ? (double?)ebike28 / sum28 : null,
            ArrLag1 = IntOrNull(reader, Col("arr_lag_1")),
            ArrMean7 = historyDays >= 7 ? DoubleOrNull(reader, Col("arr_mean_7")) : null,
            DepRestOfMonthLy = IntOrNull(reader, Col("dep_rest_of_month_ly")),
            DepSum28Ly = IntOrNull(reader, Col("dep_sum_28_ly")),
            IsoDow = reader.GetInt32(Col("iso_dow")), // integer in calendar_day, smallint in the feature table
            Month = day.Month,
            DayOfMonth = day.Day,
            Holiday = holiday,
            DayOff = holiday && !isWeekend,
            HolidayLag7 = !reader.IsDBNull(Col("holiday_lag_7")) && reader.GetBoolean(Col("holiday_lag_7")),
            TmaxC = reader.GetDouble(Col("tmax_c")),
            TminC = reader.GetDouble(Col("tmin_c")),
            PrcpMm = reader.GetDouble(Col("prcp_mm")),
            SnowMm = reader.GetDouble(Col("snow_mm")),
            SnowDepthMm = reader.GetDouble(Col("snow_depth_mm")),
            WindMs = reader.GetDouble(Col("wind_ms")),
            WeatherDay = DateOrNull(reader, Col("weather_day")),
        };
    }

    private static StationSummary ReadStation(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DoubleOrNull(reader, 2),
        DoubleOrNull(reader, 3),
        reader.IsDBNull(4) ? 0 : reader.GetInt64(4));

    private static int? IntOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? LongOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? DoubleOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static DateOnly? DateOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateOnly>(ordinal);
}
