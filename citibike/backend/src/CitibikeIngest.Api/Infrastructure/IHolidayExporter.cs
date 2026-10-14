using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>Produces the holidays of a range of years from the pinned `holidays` package.</summary>
public interface IHolidayExporter
{
    Task<HolidayExport> ExportAsync(string country, string subdivision, int fromYear, int toYear,
        IReadOnlyList<string> categories, CancellationToken ct);
}
