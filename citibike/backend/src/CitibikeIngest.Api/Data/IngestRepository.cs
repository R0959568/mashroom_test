using System.Reflection;
using CitibikeIngest.Api.Models;
using Npgsql;

namespace CitibikeIngest.Api.Data;

/// <summary>Npgsql implementation. One pooled connection per operation; every value is a bound parameter.</summary>
public sealed class IngestRepository(NpgsqlDataSource dataSource) : IIngestRepository
{
    private const string RunColumns =
        "id, started_at, finished_at, status, since, since_source, triggered_by, files_found, files_done, files_failed, error";

    private const string FileColumns =
        "id, run_id, file_name, region, last_modified, size_bytes, status, months_written, rows_written, parquet_bytes, duration_s, error, processed_at";

    private static readonly Lazy<string> SchemaSql = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("Embedded resource schema.sql is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(SchemaSql.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MarkStaleRunsFailedAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.ingest_run
               SET status = 'failed', finished_at = now(), error = @error
             WHERE status = 'running'
            """);
        command.Parameters.AddWithValue("error", "Backend restarted while the run was in progress; start a new run");
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulRunStartAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT max(started_at) FROM citibike_meta.ingest_run WHERE status = 'succeeded'");
        var result = await command.ExecuteScalarAsync(ct);
        return result is DateTime value ? ToOffset(value) : null;
    }

    public async Task<IngestRun?> GetRunningRunAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.ingest_run WHERE status = 'running' ORDER BY started_at DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<IngestRun> CreateRunAsync(IngestRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO citibike_meta.ingest_run (status, since, since_source, triggered_by)
            VALUES ('running', @since, @since_source, @triggered_by)
            RETURNING id, started_at
            """);
        command.Parameters.AddWithValue("since", run.Since.ToUniversalTime());
        command.Parameters.AddWithValue("since_source", ToDb(run.SinceSource));
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
            throw new ConflictException("An ingest run is already in progress");
        }
    }

    public async Task UpdateRunAsync(IngestRun run, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.ingest_run
               SET status = @status, finished_at = @finished_at, files_found = @files_found,
                   files_done = @files_done, files_failed = @files_failed, error = @error
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", run.Id);
        command.Parameters.AddWithValue("status", ToDb(run.Status));
        command.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt?.ToUniversalTime() ?? DBNull.Value);
        command.Parameters.AddWithValue("files_found", run.FilesFound);
        command.Parameters.AddWithValue("files_done", run.FilesDone);
        command.Parameters.AddWithValue("files_failed", run.FilesFailed);
        command.Parameters.AddWithValue("error", (object?)run.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IngestRun?> GetRunAsync(long id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.ingest_run WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<IngestRun>> ListRunsAsync(int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {RunColumns} FROM citibike_meta.ingest_run ORDER BY started_at DESC, id DESC LIMIT @limit");
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var runs = new List<IngestRun>();
        while (await reader.ReadAsync(ct))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<IReadOnlyList<IngestFile>> AddFilesAsync(long runId, IEnumerable<RemoteFile> files, CancellationToken ct)
    {
        var result = new List<IngestFile>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var remote in files)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO citibike_meta.ingest_file (run_id, file_name, region, last_modified, size_bytes, status)
                VALUES (@run_id, @file_name, @region, @last_modified, @size_bytes, 'pending')
                RETURNING id
                """, connection, transaction);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("file_name", remote.Name);
            command.Parameters.AddWithValue("region", remote.Region);
            command.Parameters.AddWithValue("last_modified", remote.LastModified.ToUniversalTime());
            command.Parameters.AddWithValue("size_bytes", remote.SizeBytes);
            var id = (long)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("INSERT returned no id"));
            result.Add(new IngestFile
            {
                Id = id,
                RunId = runId,
                FileName = remote.Name,
                Region = remote.Region,
                LastModified = remote.LastModified,
                SizeBytes = remote.SizeBytes,
                Status = FileStatus.Pending,
            });
        }

        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task UpdateFileAsync(IngestFile file, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE citibike_meta.ingest_file
               SET status = @status, months_written = @months_written, rows_written = @rows_written,
                   parquet_bytes = @parquet_bytes, duration_s = @duration_s, error = @error, processed_at = @processed_at
             WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", file.Id);
        command.Parameters.AddWithValue("status", ToDb(file.Status));
        command.Parameters.AddWithValue("months_written", file.MonthsWritten);
        command.Parameters.AddWithValue("rows_written", file.RowsWritten);
        command.Parameters.AddWithValue("parquet_bytes", file.ParquetBytes);
        command.Parameters.AddWithValue("duration_s", (object?)file.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("error", (object?)file.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("processed_at", (object?)file.ProcessedAt?.ToUniversalTime() ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IngestFile>> GetRunFilesAsync(long runId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {FileColumns} FROM citibike_meta.ingest_file WHERE run_id = @run_id ORDER BY id");
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var files = new List<IngestFile>();
        while (await reader.ReadAsync(ct))
        {
            files.Add(ReadFile(reader));
        }

        return files;
    }

    // ---- row mapping ------------------------------------------------------------------------

    private static IngestRun ReadRun(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        StartedAt = ToOffset(r.GetDateTime(r.GetOrdinal("started_at"))),
        FinishedAt = OffsetOrNull(r, "finished_at"),
        Status = Enum.Parse<RunStatus>(r.GetString(r.GetOrdinal("status")), ignoreCase: true),
        Since = ToOffset(r.GetDateTime(r.GetOrdinal("since"))),
        SinceSource = Enum.Parse<SinceSource>(r.GetString(r.GetOrdinal("since_source")), ignoreCase: true),
        Trigger = r.GetString(r.GetOrdinal("triggered_by")),
        FilesFound = r.GetInt32(r.GetOrdinal("files_found")),
        FilesDone = r.GetInt32(r.GetOrdinal("files_done")),
        FilesFailed = r.GetInt32(r.GetOrdinal("files_failed")),
        Error = StringOrNull(r, "error"),
    };

    private static IngestFile ReadFile(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        RunId = r.GetInt64(r.GetOrdinal("run_id")),
        FileName = r.GetString(r.GetOrdinal("file_name")),
        Region = r.GetString(r.GetOrdinal("region")),
        LastModified = ToOffset(r.GetDateTime(r.GetOrdinal("last_modified"))),
        SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
        Status = Enum.Parse<FileStatus>(r.GetString(r.GetOrdinal("status")), ignoreCase: true),
        MonthsWritten = r.GetInt32(r.GetOrdinal("months_written")),
        RowsWritten = r.GetInt64(r.GetOrdinal("rows_written")),
        ParquetBytes = r.GetInt64(r.GetOrdinal("parquet_bytes")),
        DurationSeconds = r.IsDBNull(r.GetOrdinal("duration_s")) ? null : r.GetDouble(r.GetOrdinal("duration_s")),
        Error = StringOrNull(r, "error"),
        ProcessedAt = OffsetOrNull(r, "processed_at"),
    };

    // Npgsql returns timestamptz as DateTime with Kind = Utc.
    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private static DateTimeOffset? OffsetOrNull(NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : ToOffset(r.GetDateTime(ordinal));
    }

    private static string? StringOrNull(NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    private static string ToDb<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();
}
