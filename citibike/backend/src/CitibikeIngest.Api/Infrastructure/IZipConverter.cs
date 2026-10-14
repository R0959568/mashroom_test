using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>Turns one downloaded zip into monthly Parquet files under Ingest:RawDir.</summary>
public interface IZipConverter
{
    Task<ConversionResult> ConvertAsync(string zipPath, string region, CancellationToken ct);
}
