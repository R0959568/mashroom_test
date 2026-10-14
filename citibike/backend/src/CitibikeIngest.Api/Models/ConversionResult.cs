namespace CitibikeIngest.Api.Models;

/// <summary>One Parquet file written by ingest/convert_zip.py (one per month in the zip).</summary>
public sealed record ConvertedMonth(string Region, int Year, int Month, long Rows, long ParquetBytes, double Seconds);

public sealed record ConversionResult(IReadOnlyList<ConvertedMonth> Months)
{
    public long TotalRows => Months.Sum(m => m.Rows);

    public long TotalParquetBytes => Months.Sum(m => m.ParquetBytes);
}
