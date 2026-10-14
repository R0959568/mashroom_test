using CitibikeIngest.Api.Models;
using Npgsql;

namespace CitibikeIngest.Api.Data;

/// <summary>Npgsql implementation. One pooled connection per operation; every value is a bound parameter.</summary>
public sealed class WeatherRepository(NpgsqlDataSource dataSource) : IWeatherRepository
{
    private const string RunColumns =
        "id, started_at, finished_at, status, station, date_from, date_to, range_source, triggered_by, days_received, days_written, error";

    private const string DayColumns = "station, day, tmax_c, tmin_c, prcp_mm, snow_mm, snow_depth_mm, wind_ms";

    public async Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.weather_run
               SET status = 'failed', finished_at = now(), error = @error
             WHERE status = 'running'
            """);
        command.Parameters.AddWithValue("error", "Backend restarted while the load was in progress; start a new load");
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<WeatherRun?> GetRunningRunAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.weather_run WHERE status = 'running' ORDER BY started_at DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<WeatherRun> CreateRunAsync(WeatherRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO citibike_meta.weather_run (status, station, date_from, date_to, range_source, triggered_by)
            VALUES ('running', @station, @date_from, @date_to, @range_source, @triggered_by)
            RETURNING id, started_at
            """);
        command.Parameters.AddWithValue("station", run.Station);
        command.Parameters.AddWithValue("date_from", run.From);
        command.Parameters.AddWithValue("date_to", run.To);
        command.Parameters.AddWithValue("range_source", ToDb(run.RangeSource));
        command.Parameters.AddWithValue("triggered_by", run.Trigger);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            run.Id = reader.GetInt64(0);
            run.StartedAt = ToOffset(reader.GetDateTime(1));
            run.Status = RunStatus.Running;
            return run;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("A weather load is already in progress");
        }
    }

    public async Task UpdateRunAsync(WeatherRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.weather_run
               SET status = @status, finished_at = @finished_at, days_received = @days_received,
                   days_written = @days_written, error = @error
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("status", ToDb(run.Status));
        command.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt?.ToUniversalTime() ?? DBNull.Value);
        command.Parameters.AddWithValue("days_received", run.DaysReceived);
        command.Parameters.AddWithValue("days_written", run.DaysWritten);
        command.Parameters.AddWithValue("error", (object?)run.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<WeatherRun?> GetRunAsync(long id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.weather_run WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<WeatherRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.weather_run ORDER BY started_at DESC, id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var runs = new List<WeatherRun>();
        while (await reader.ReadAsync(ct))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<DateOnly?> GetLastDayAsync(string station, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT max(day) FROM citibike.weather_daily WHERE station = @station");
        command.Parameters.AddWithValue("station", station);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateOnly>(0);
    }

    public async Task<int> UpsertDaysAsync(long runId, IReadOnlyList<WeatherDay> days, CancellationToken ct)
    {
        if (days.Count == 0)
        {
            return 0;
        }

        // One statement for the whole load: the columns travel as parallel arrays and unnest() turns them into rows.
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO citibike.weather_daily
                   (station, day, tmax_c, tmin_c, prcp_mm, snow_mm, snow_depth_mm, wind_ms, run_id, loaded_at)
            SELECT d.station, d.day, d.tmax_c, d.tmin_c, d.prcp_mm, d.snow_mm, d.snow_depth_mm, d.wind_ms, @run_id, now()
              FROM unnest(@station, @day, @tmax_c, @tmin_c, @prcp_mm, @snow_mm, @snow_depth_mm, @wind_ms)
                   AS d (station, day, tmax_c, tmin_c, prcp_mm, snow_mm, snow_depth_mm, wind_ms)
            ON CONFLICT (station, day) DO UPDATE
               SET tmax_c = EXCLUDED.tmax_c, tmin_c = EXCLUDED.tmin_c, prcp_mm = EXCLUDED.prcp_mm,
                   snow_mm = EXCLUDED.snow_mm, snow_depth_mm = EXCLUDED.snow_depth_mm, wind_ms = EXCLUDED.wind_ms,
                   run_id = EXCLUDED.run_id, loaded_at = EXCLUDED.loaded_at
            """);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("station", days.Select(d => d.Station).ToArray());
        command.Parameters.AddWithValue("day", days.Select(d => d.Day).ToArray());
        command.Parameters.AddWithValue("tmax_c", days.Select(d => d.TmaxC).ToArray());
        command.Parameters.AddWithValue("tmin_c", days.Select(d => d.TminC).ToArray());
        command.Parameters.AddWithValue("prcp_mm", days.Select(d => d.PrcpMm).ToArray());
        command.Parameters.AddWithValue("snow_mm", days.Select(d => d.SnowMm).ToArray());
        command.Parameters.AddWithValue("snow_depth_mm", days.Select(d => d.SnowDepthMm).ToArray());
        command.Parameters.AddWithValue("wind_ms", days.Select(d => d.WindMs).ToArray());
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<WeatherDay>> GetDaysAsync(string station, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {DayColumns} FROM citibike.weather_daily WHERE station = @station AND day BETWEEN @from AND @to ORDER BY day");
        command.Parameters.AddWithValue("station", station);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var days = new List<WeatherDay>();
        while (await reader.ReadAsync(ct))
        {
            days.Add(new WeatherDay(
                reader.GetString(0),
                reader.GetFieldValue<DateOnly>(1),
                DoubleOrNull(reader, 2),
                DoubleOrNull(reader, 3),
                DoubleOrNull(reader, 4),
                DoubleOrNull(reader, 5),
                DoubleOrNull(reader, 6),
                DoubleOrNull(reader, 7)));
        }

        return days;
    }

    // ---- row mapping ------------------------------------------------------------------------

    private static WeatherRun ReadRun(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        StartedAt = ToOffset(r.GetDateTime(r.GetOrdinal("started_at"))),
        FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : ToOffset(r.GetDateTime(r.GetOrdinal("finished_at"))),
        Status = Enum.Parse<RunStatus>(r.GetString(r.GetOrdinal("status")), ignoreCase: true),
        Station = r.GetString(r.GetOrdinal("station")),
        From = r.GetFieldValue<DateOnly>(r.GetOrdinal("date_from")),
        To = r.GetFieldValue<DateOnly>(r.GetOrdinal("date_to")),
        RangeSource = Enum.Parse<WeatherRangeSource>(r.GetString(r.GetOrdinal("range_source")), ignoreCase: true),
        Trigger = r.GetString(r.GetOrdinal("triggered_by")),
        DaysReceived = r.GetInt32(r.GetOrdinal("days_received")),
        DaysWritten = r.GetInt32(r.GetOrdinal("days_written")),
        Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
    };

    private static double? DoubleOrNull(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);

    // Npgsql returns timestamptz as DateTime with Kind = Utc.
    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();
}
