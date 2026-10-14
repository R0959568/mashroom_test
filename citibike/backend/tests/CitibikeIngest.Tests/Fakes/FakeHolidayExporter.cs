using CitibikeIngest.Api.Infrastructure;
using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Tests.Fakes;

/// <summary>Returns the holidays in <see cref="Holidays"/> that fall in the requested years, or fails like a crashed exporter.</summary>
public sealed class FakeHolidayExporter : IHolidayExporter
{
    public List<Holiday> Holidays { get; } = [];

    public string Version { get; set; } = "0.105";

    public List<(string Country, string Subdivision, int FromYear, int ToYear, IReadOnlyList<string> Categories)> Calls { get; } = [];

    public Exception? FailWith { get; set; }

    /// <summary>When set, returned as-is instead of filtering <see cref="Holidays"/>: to simulate a misbehaving exporter.</summary>
    public IReadOnlyList<Holiday>? ReturnExactly { get; set; }

    public Task<HolidayExport> ExportAsync(string country, string subdivision, int fromYear, int toYear,
        IReadOnlyList<string> categories, CancellationToken ct)
    {
        Calls.Add((country, subdivision, fromYear, toYear, categories));
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var rows = ReturnExactly ?? Holidays.Where(h => h.Day.Year >= fromYear && h.Day.Year <= toYear).ToList();
        return Task.FromResult(new HolidayExport("holidays", Version, rows));
    }
}
