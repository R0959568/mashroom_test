using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>
/// Runs ingest/export_holidays.py as a subprocess, the same contract as <see cref="PythonZipConverter"/>:
/// arguments in, errors on stderr, one JSON document on stdout, exit code. The holiday rules live in the
/// `holidays` package (pinned in ingest/requirements.txt); this class only handles the process.
/// </summary>
public sealed class PythonHolidayExporter(
    IOptions<IngestOptions> ingestOptions,
    IOptions<CalendarOptions> calendarOptions,
    ILogger<PythonHolidayExporter> logger) : IHolidayExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<HolidayExport> ExportAsync(string country, string subdivision, int fromYear, int toYear,
        IReadOnlyList<string> categories, CancellationToken ct)
    {
        var python = ingestOptions.Value.PythonExecutable;
        var startInfo = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(calendarOptions.Value.ExporterScript);
        startInfo.ArgumentList.Add("--from-year");
        startInfo.ArgumentList.Add(fromYear.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--to-year");
        startInfo.ArgumentList.Add(toYear.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--country");
        startInfo.ArgumentList.Add(country);
        startInfo.ArgumentList.Add("--subdiv");
        startInfo.ArgumentList.Add(subdivision);
        startInfo.ArgumentList.Add("--categories");
        startInfo.ArgumentList.Add(string.Join(',', categories));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {python}");
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Holiday exporter exited with code {process.ExitCode}: {(stderr.Length > 0 ? stderr : "(no output)")}");
            }

            if (stderr.Length > 0)
            {
                logger.LogInformation("[export_holidays] {Output}", stderr);
            }

            var output = JsonSerializer.Deserialize<ExporterOutput>(stdout, JsonOptions)
                ?? throw new InvalidOperationException("Holiday exporter produced no JSON");
            return new HolidayExport(
                output.Source,
                output.SourceVersion,
                output.Holidays
                    .Select(h => new Holiday(DateOnly.ParseExact(h.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture), h.Name, h.Category, h.Observed))
                    .ToList());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    private sealed record ExporterOutput(string Source, string SourceVersion, List<ExporterHoliday> Holidays);

    private sealed record ExporterHoliday(string Day, string Name, string Category, bool Observed);
}
