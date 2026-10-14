using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CitibikeIngest.Api.Models;
using CitibikeIngest.Api.Options;
using Microsoft.Extensions.Options;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>
/// Runs ingest/convert_zip.py as a subprocess. The Python side owns every source quirk (nested zips,
/// duplicate months, both column layouts) and is unit-tested there; this class only handles the
/// process contract: arguments in, log lines on stderr, one JSON document on stdout, exit code.
/// </summary>
public sealed class PythonZipConverter(IOptions<IngestOptions> options, ILogger<PythonZipConverter> logger) : IZipConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ConversionResult> ConvertAsync(string zipPath, string region, CancellationToken ct)
    {
        var settings = options.Value;
        var fileName = Path.GetFileName(zipPath);
        var startInfo = new ProcessStartInfo(settings.PythonExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(settings.ConverterScript);
        startInfo.ArgumentList.Add("--zip");
        startInfo.ArgumentList.Add(zipPath);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(settings.RawDir);
        startInfo.ArgumentList.Add("--region");
        startInfo.ArgumentList.Add(region);
        startInfo.ArgumentList.Add("--min-year");
        startInfo.ArgumentList.Add(settings.MinYear.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--tmp");
        startInfo.ArgumentList.Add(Path.Combine(settings.LandingDir, "tmp"));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {settings.PythonExecutable}");
        try
        {
            var stderrTask = PumpStderrAsync(process.StandardError, fileName, ct);
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var lastStderrLine = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Converter exited with code {process.ExitCode} for {fileName}: {lastStderrLine ?? "(no output)"}");
            }

            var output = JsonSerializer.Deserialize<ConverterOutput>(stdout, JsonOptions)
                ?? throw new InvalidOperationException($"Converter produced no JSON for {fileName}");
            return new ConversionResult(output.Months);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private async Task<string?> PumpStderrAsync(StreamReader reader, string fileName, CancellationToken ct)
    {
        string? last = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            last = line;
            logger.LogInformation("[{File}] {Line}", fileName, line);
        }

        return last;
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

    private sealed record ConverterOutput(List<ConvertedMonth> Months);
}
