namespace CitibikeIngest.Api.Dtos;

public sealed record RemoteFileDto(
    string Name,
    string Region,
    int Year,
    int? Month,
    string Label,
    long SizeBytes,
    DateTimeOffset LastModified);

/// <summary>Response of GET /api/s3files: what a run started now (or with this since) would process.</summary>
public sealed record NewFilesDto(
    DateTimeOffset Since,
    string SinceSource,
    int Count,
    long TotalBytes,
    IReadOnlyList<RemoteFileDto> Files);
