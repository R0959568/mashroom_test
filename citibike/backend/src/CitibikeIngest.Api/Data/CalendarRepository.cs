using CitibikeIngest.Api.Models;
using Npgsql;

namespace CitibikeIngest.Api.Data;

/// <summary>Npgsql implementation. One pooled connection per operation; every value is a bound parameter.</summary>
public sealed class CalendarRepository(NpgsqlDataSource dataSource) : ICalendarRepository
{
    private const string RunColumns =
        "id, started_at, finished_at, status, country, subdivision, categories, year_from, year_to, range_source, "
        + "triggered_by, source, source_version, holidays_received, holidays_written, error";

    public async Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.calendar_run
               SET status = 'failed', finished_at = now(), error = @error
             WHERE status = 'running'
            """);
        command.Parameters.AddWithValue("error", "Backend restarted while the load was in progress; start a new load");
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CalendarRun?> GetRunningRunAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.calendar_run WHERE status = 'running' ORDER BY started_at DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<CalendarRun> CreateRunAsync(CalendarRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO citibike_meta.calendar_run
                   (status, country, subdivision, categories, year_from, year_to, range_source, triggered_by)
            VALUES ('running', @country, @subdivision, @categories, @year_from, @year_to, @range_source, @triggered_by)
            RETURNING id, started_at
            """);
        command.Parameters.AddWithValue("country", run.Country);
        command.Parameters.AddWithValue("subdivision", run.Subdivision);
        command.Parameters.AddWithValue("categories", run.Categories);
        command.Parameters.AddWithValue("year_from", run.FromYear);
        command.Parameters.AddWithValue("year_to", run.ToYear);
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
            throw new ConflictException("A calendar load is already in progress");
        }
    }

    public async Task UpdateRunAsync(CalendarRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.calendar_run
               SET status = @status, finished_at = @finished_at, source = @source, source_version = @source_version,
                   holidays_received = @holidays_received, holidays_written = @holidays_written, error = @error
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("status", ToDb(run.Status));
        command.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt?.ToUniversalTime() ?? DBNull.Value);
        command.Parameters.AddWithValue("source", (object?)run.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("source_version", (object?)run.SourceVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("holidays_received", run.HolidaysReceived);
        command.Parameters.AddWithValue("holidays_written", run.HolidaysWritten);
        command.Parameters.AddWithValue("error", (object?)run.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<CalendarRun?> GetRunAsync(long id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.calendar_run WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<CalendarRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.calendar_run ORDER BY started_at DESC, id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var runs = new List<CalendarRun>();
        while (await reader.ReadAsync(ct))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<int> ReplaceHolidaysAsync(long runId, string country, string subdivision, CalendarRange range,
        HolidayExport export, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand(
            """
            DELETE FROM citibike.holiday
             WHERE country = @country AND subdivision = @subdivision AND day BETWEEN @first_day AND @last_day
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("country", country);
            delete.Parameters.AddWithValue("subdivision", subdivision);
            delete.Parameters.AddWithValue("first_day", range.FirstDay);
            delete.Parameters.AddWithValue("last_day", range.LastDay);
            await delete.ExecuteNonQueryAsync(ct);
        }

        // One statement for the whole load: the columns travel as parallel arrays and unnest() turns them into rows.
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO citibike.holiday
                   (country, subdivision, day, name, category, is_observed, source, source_version, run_id, loaded_at)
            SELECT @country, @subdivision, h.day, h.name, h.category, h.is_observed, @source, @source_version, @run_id, now()
              FROM unnest(@day, @name, @category, @is_observed) AS h (day, name, category, is_observed)
            """, connection, transaction);
        insert.Parameters.AddWithValue("country", country);
        insert.Parameters.AddWithValue("subdivision", subdivision);
        insert.Parameters.AddWithValue("source", export.Source);
        insert.Parameters.AddWithValue("source_version", export.SourceVersion);
        insert.Parameters.AddWithValue("run_id", runId);
        insert.Parameters.AddWithValue("day", export.Holidays.Select(h => h.Day).ToArray());
        insert.Parameters.AddWithValue("name", export.Holidays.Select(h => h.Name).ToArray());
        insert.Parameters.AddWithValue("category", export.Holidays.Select(h => h.Category).ToArray());
        insert.Parameters.AddWithValue("is_observed", export.Holidays.Select(h => h.Observed).ToArray());
        var written = await insert.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return written;
    }

    public async Task<IReadOnlyList<StoredHoliday>> GetHolidaysAsync(string country, string subdivision, DateOnly from, DateOnly to,
        CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT day, name, category, is_observed, source, source_version
              FROM citibike.holiday
             WHERE country = @country AND subdivision = @subdivision AND day BETWEEN @from AND @to
             ORDER BY day, category, name
            """);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("subdivision", subdivision);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var holidays = new List<StoredHoliday>();
        while (await reader.ReadAsync(ct))
        {
            holidays.Add(new StoredHoliday(
                new Holiday(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return holidays;
    }

    // ---- row mapping ------------------------------------------------------------------------

    private static CalendarRun ReadRun(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        StartedAt = ToOffset(r.GetDateTime(r.GetOrdinal("started_at"))),
        FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : ToOffset(r.GetDateTime(r.GetOrdinal("finished_at"))),
        Status = Enum.Parse<RunStatus>(r.GetString(r.GetOrdinal("status")), ignoreCase: true),
        Country = r.GetString(r.GetOrdinal("country")),
        Subdivision = r.GetString(r.GetOrdinal("subdivision")),
        Categories = r.GetString(r.GetOrdinal("categories")),
        FromYear = r.GetInt32(r.GetOrdinal("year_from")),
        ToYear = r.GetInt32(r.GetOrdinal("year_to")),
        RangeSource = Enum.Parse<CalendarRangeSource>(r.GetString(r.GetOrdinal("range_source")), ignoreCase: true),
        Trigger = r.GetString(r.GetOrdinal("triggered_by")),
        Source = r.IsDBNull(r.GetOrdinal("source")) ? null : r.GetString(r.GetOrdinal("source")),
        SourceVersion = r.IsDBNull(r.GetOrdinal("source_version")) ? null : r.GetString(r.GetOrdinal("source_version")),
        HolidaysReceived = r.GetInt32(r.GetOrdinal("holidays_received")),
        HolidaysWritten = r.GetInt32(r.GetOrdinal("holidays_written")),
        Error = r.IsDBNull(r.GetOrdinal("error")) ? null : r.GetString(r.GetOrdinal("error")),
    };

    // Npgsql returns timestamptz as DateTime with Kind = Utc.
    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();
}
